using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Domain;

public sealed record TenantSlug
{
    private TenantSlug(string value) => Value = value;
    public string Value { get; }
    public static TenantSlug Parse(string value) => throw new NotImplementedException();
}

public static class TenantPolicy
{
    public static bool IsOperational(TenantStatus status, DateTimeOffset now) => throw new NotImplementedException();
    public static TenantStatus CompleteProvisioning(TenantStatus status, OperationStamp stamp, DateTimeOffset now) => throw new NotImplementedException();
}

public static class TenantIdentity
{
    public static void Verify(Guid expected, Guid? actual, int rows) => throw new NotImplementedException();
}

public static class TenantAddress
{
    public static string Link(TenantContext context, string relative) => throw new NotImplementedException();
    public static string CacheKey(TenantContext context, string purpose, string key) => throw new NotImplementedException();
}
