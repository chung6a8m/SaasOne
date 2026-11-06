using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Domain;
namespace SaaS.Tenancy.Operations;

public sealed record ProvisionRequest(Guid TenantId, Guid OperationId, string Slug, string Plan, DateTimeOffset ExpiresAt)
{
    public string DatabaseName => "saas_t_" + TenantId.ToString("N");
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        TenantId, OperationId, Slug, Plan, ExpiresAt = ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    }))));

    public static ProvisionRequest Parse(string[] args)
    {
        if (args.Length != 6 || args[0] != "provision" || !Guid.TryParseExact(args[1], "D", out var tenantId)
            || !Guid.TryParseExact(args[2], "D", out var operationId) || tenantId == Guid.Empty || operationId == Guid.Empty
            || !DateTimeOffset.TryParseExact(args[5], "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow || args[4] is not ("Standard" or "Approval"))
            throw new TenantContractException("invalid_provision_request");
        return new(tenantId, operationId, TenantSlug.Parse(args[3]).Value, args[4], expiresAt);
    }
}

public sealed record OrchardBaseline(string FeatureHash, string MigrationHash);

/// <summary>The host owns the Orchard adapter; Tenancy never references business features or CMS packages.</summary>
public interface IOrchardMaintenance
{
    Task<OrchardBaseline> InitializeAsync(string shell, string prefix, Guid tenantId, string connection, CancellationToken cancellationToken);
    Task<OrchardBaseline> VerifyAsync(string shell, string prefix, Guid tenantId, string connection, CancellationToken cancellationToken);
}
