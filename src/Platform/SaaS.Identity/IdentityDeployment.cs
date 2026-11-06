using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using SaaS.Tenancy.Data;

namespace SaaS.Identity;

/// <summary>Trusted deployment configuration. No value is obtained from an HTTP request.</summary>
public sealed class IdentityDeployment
{
    public Uri Issuer { get; }
    public X509Certificate2 SigningCertificate { get; }
    public X509Certificate2 EncryptionCertificate { get; }
    public string DataProtectionDirectory { get; }
    public string NativePrefix { get; }
    private readonly Func<Guid, string> _clientSecret;

    public IdentityDeployment(Uri issuer, X509Certificate2 signingCertificate, X509Certificate2 encryptionCertificate,
        string dataProtectionDirectory, string nativePrefix, Func<Guid, string> clientSecret)
    {
        if (!issuer.IsAbsoluteUri || issuer.Scheme != Uri.UriSchemeHttps || issuer.AbsolutePath != "/"
            || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 || issuer.UserInfo.Length != 0)
            throw new InvalidOperationException("Identity requires a canonical HTTPS origin.");
        if (!Path.IsPathFullyQualified(dataProtectionDirectory) || !Directory.Exists(dataProtectionDirectory)
            || !Regex.IsMatch(nativePrefix, "^[a-z][a-z0-9]{3,30}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Identity key storage or native profile is invalid.");
        RequireCertificate(signingCertificate);
        RequireCertificate(encryptionCertificate);
        if (signingCertificate.Thumbprint == encryptionCertificate.Thumbprint)
            throw new InvalidOperationException("Signing and encryption keys must be independent.");
        Issuer = issuer;
        SigningCertificate = signingCertificate;
        EncryptionCertificate = encryptionCertificate;
        DataProtectionDirectory = dataProtectionDirectory;
        NativePrefix = nativePrefix;
        _clientSecret = clientSecret;
    }

    public static bool Configured => Environment.GetEnvironmentVariable("SAAS_IDENTITY_ENABLED") == "true";
    public static IdentityDeployment Load() => new(
        new Uri(TenancyConnections.Required("SAAS_IDENTITY_ISSUER"), UriKind.Absolute),
        X509CertificateLoader.LoadPkcs12FromFile(TenancyConnections.Required("SAAS_IDENTITY_SIGNING_CERTIFICATE"),
            TenancyConnections.Required("SAAS_IDENTITY_SIGNING_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet),
        X509CertificateLoader.LoadPkcs12FromFile(TenancyConnections.Required("SAAS_IDENTITY_ENCRYPTION_CERTIFICATE"),
            TenancyConnections.Required("SAAS_IDENTITY_ENCRYPTION_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet),
        TenancyConnections.Required("SAAS_IDENTITY_DATA_PROTECTION_DIRECTORY"), "ocid",
        id => TenancyConnections.Required("SAAS_IDENTITY_CLIENT_" + id.ToString("N").ToUpperInvariant()));

    public string ClientSecret(Guid tenantId)
    {
        var value = _clientSecret(tenantId);
        if (value.Length < 32) throw new InvalidOperationException("Identity client credential is unavailable.");
        return value;
    }
    public static string ClientId(Guid tenantId) => "saas-" + tenantId.ToString("N");
    public static string Audience(Guid tenantId) => "urn:saas:tenant:" + tenantId.ToString("D");
    public Uri Callback(string slug) => new(Issuer, slug + "/signin-oidc");
    public Uri LogoutCallback(string slug) => new(Issuer, slug + "/signout-callback-oidc");
    public void ValidateClient(IdentityClient client)
    {
        if (client.TenantId == Guid.Empty || client.ClientId != ClientId(client.TenantId)
            || !Regex.IsMatch(client.ShellName, "^[a-z][a-z0-9-]{1,47}$", RegexOptions.CultureInvariant)
            || client.RedirectUri != Callback(client.ShellName).AbsoluteUri
            || client.LogoutRedirectUri != LogoutCallback(client.ShellName).AbsoluteUri)
            throw new InvalidOperationException("Identity client registration is invalid.");
    }
    private static void RequireCertificate(X509Certificate2 certificate)
    {
        using var key = certificate.GetRSAPrivateKey();
        var now = DateTime.UtcNow;
        if (!certificate.HasPrivateKey || key is null || key.KeySize < 2048 || certificate.NotBefore.ToUniversalTime() > now
            || certificate.NotAfter.ToUniversalTime() <= now)
            throw new InvalidOperationException("Identity certificate is unavailable or invalid.");
    }
}

public static class IdentityNames
{
    public const string TenantCookie = "SaaS.Tenant";
    public const string Oidc = "SaaS.Oidc";
    public const string Scope = "saas.identity";
    public const string User = "saas_uid";
    public const string Family = "saas_fid";
    public const string Session = "saas_sid";
    public const string Authorization = "saas_auth";
    public const string Tenant = "tenant_id";
}

public sealed class IdentityDeniedException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
