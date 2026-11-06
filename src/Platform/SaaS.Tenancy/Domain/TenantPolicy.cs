using System.Globalization;
using System.Text.RegularExpressions;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Domain;

public sealed partial record TenantSlug
{
    private TenantSlug(string value) => Value = value;
    public string Value { get; }
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "default", "admin", "api", "connect", "health", "setup", "media", "templates", "features", "recipes",
        "account", "login", "logout", "signin", "signout", "assets", "css", "js", "lib", "favicon", "robots", "sitemap", "platform"
    };
    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
    public static TenantSlug Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 48 || !SlugPattern().IsMatch(value) || Reserved.Contains(value))
            throw new TenantContractException("invalid_tenant_slug");
        return new TenantSlug(value);
    }
}

public static class TenantPolicy
{
    public static bool IsOperational(TenantStatus status, DateTimeOffset now) =>
        status.TenantId != Guid.Empty && status.RegistryVersion > 0 && status.Subscription == SubscriptionStatus.Active
        && status.ExpiresAt > now && status.Operation == OperationStatus.None && status.OperationId is null
        && status.OwnerId is null && !status.SecurityRevalidationRequired;

    public static TenantStatus CompleteProvisioning(TenantStatus status, OperationStamp stamp, DateTimeOffset now)
    {
        if (status.TenantId == Guid.Empty || stamp.OperationId == Guid.Empty || stamp.OwnerId == Guid.Empty
            || stamp.Fence <= 0 || stamp.RegistryVersion <= 0 || status.Operation != OperationStatus.Provisioning
            || status.OperationId != stamp.OperationId || status.OwnerId != stamp.OwnerId
            || status.Fence != stamp.Fence || status.RegistryVersion != stamp.RegistryVersion
            || status.Subscription != SubscriptionStatus.Active || status.ExpiresAt <= now || status.SecurityRevalidationRequired)
            throw new TenantContractException("operation_reconciliation_required");
        return status with { Operation = OperationStatus.None, OperationId = null, OwnerId = null, RegistryVersion = checked(status.RegistryVersion + 1) };
    }
}

public static class TenantIdentity
{
    public static void Verify(Guid expected, Guid? actual, int rows)
    {
        if (expected == Guid.Empty || rows != 1 || actual != expected)
            throw new TenantContractException("database_identity_mismatch");
    }
}

public static class TenantAddress
{
    public static string Link(TenantContext context, string relative)
    {
        ValidateContext(context);
        if (relative is null || relative.StartsWith('/') || relative.Contains('\\') || relative.Contains('%')
            || relative.Contains(':') || relative.Contains('?') || relative.Contains('#')
            || relative.Any(char.IsControl) || relative.Split('/').Any(s => s is "." or ".."))
            throw new TenantContractException("invalid_tenant_link");
        return "/" + context.UrlPrefix + "/" + relative;
    }

    public static string CacheKey(TenantContext context, string purpose, string key)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(key);
        return string.Create(CultureInfo.InvariantCulture, $"tenant:{context.TenantId:N}:{purpose.Length}:{purpose}:{key.Length}:{key}");
    }

    private static void ValidateContext(TenantContext context)
    {
        if (context.TenantId == Guid.Empty || context.ShellName != context.UrlPrefix)
            throw new TenantContractException("invalid_tenant_context");
        _ = TenantSlug.Parse(context.UrlPrefix);
    }
}
