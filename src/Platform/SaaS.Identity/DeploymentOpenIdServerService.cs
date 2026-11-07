using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;
using OrchardCore.OpenId.Services;
using OrchardCore.OpenId.Settings;

namespace SaaS.Identity;

/// <summary>Native Orchard server configuration owned by private deployment, never an admin HTTP route.</summary>
public sealed class DeploymentOpenIdServerService(IdentityDeployment deployment) : IOpenIdServerService
{
    public Task<OpenIdServerSettings> GetSettingsAsync() => Task.FromResult(new OpenIdServerSettings
    {
        Authority = deployment.Issuer,
        AuthorizationEndpointPath = "/connect/authorize",
        TokenEndpointPath = "/connect/token",
        LogoutEndpointPath = "/connect/logout",
        AllowAuthorizationCodeFlow = true,
        AllowRefreshTokenFlow = true,
        RequireProofKeyForCodeExchange = true,
        DisableAccessTokenEncryption = true,
        DisableRollingRefreshTokens = false,
        UseReferenceAccessTokens = false,
        AccessTokenFormat = OpenIdServerSettings.TokenFormat.JsonWebToken
    });
    public Task<OpenIdServerSettings> LoadSettingsAsync() => GetSettingsAsync();
    public Task UpdateSettingsAsync(OpenIdServerSettings settings) => throw new InvalidOperationException("Private deployment configuration required.");
    public Task<ImmutableArray<ValidationResult>> ValidateSettingsAsync(OpenIdServerSettings settings) =>
        Task.FromResult(ImmutableArray<ValidationResult>.Empty);
    public Task<ImmutableArray<(X509Certificate2 certificate, StoreLocation location, StoreName name)>> GetAvailableCertificatesAsync() =>
        Task.FromResult(ImmutableArray<(X509Certificate2, StoreLocation, StoreName)>.Empty);
    public Task<ImmutableArray<SecurityKey>> GetEncryptionKeysAsync() =>
        Task.FromResult(ImmutableArray.Create<SecurityKey>(new X509SecurityKey(deployment.EncryptionCertificate)));
    public Task<ImmutableArray<SecurityKey>> GetSigningKeysAsync() =>
        Task.FromResult(ImmutableArray.Create<SecurityKey>(new X509SecurityKey(deployment.SigningCertificate)));
    public Task PruneManagedCertificatesAsync() => Task.CompletedTask; // Rotation belongs to deployment, not runtime deletion.
}
