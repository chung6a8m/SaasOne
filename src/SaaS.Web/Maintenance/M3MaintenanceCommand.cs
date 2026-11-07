using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
namespace SaaS.Web.Maintenance;

/// <summary>Private command composition. No service, credential or command is registered in HTTP DI.</summary>
internal static class M3MaintenanceCommand
{
    public static async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        var connections = new TenancyConnections();
        var maintenance = new AccessMaintenance(connections);
        ResourceResult result;
        switch (args[0])
        {
            case "ops-list" when args.Length == 1:
                await ListAsync(connections, ct);
                return;
            case "ops-lifecycle" when args.Length == 5:
                var state = args[3] switch
                {
                    "active" => SubscriptionStatus.Active, "suspended" => SubscriptionStatus.Suspended,
                    "delete" => SubscriptionStatus.PendingDeletion, _ => throw Invalid()
                };
                if (!DateTimeOffset.TryParseExact(args[4], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) throw Invalid();
                await new IdentityOperations(connections).SetTenantAsync(Id(args[1]), state, expiry, ct, Version(args[2]));
                await ListAsync(connections, ct, Id(args[1]));
                return;
            case "access-install" when args.Length == 4:
                result = await maintenance.InstallAsync(Id(args[1]), Id(args[2]), Version(args[3]), ct);
                break;
            case "ops-plan" when args.Length == 5 && args[4] is "Standard" or "Approval":
                result = await maintenance.Operations.ChangePlanAsync(Id(args[1]), Id(args[2]), Version(args[3]), args[4], ct);
                break;
            case "ops-resume" when args.Length == 4:
                result = await maintenance.Operations.ResumeAsync(Id(args[1]), Id(args[2]), Version(args[3]), ct);
                break;
            case "ops-recovery-begin" when args.Length == 4:
                result = await maintenance.Operations.BeginRecoveryAsync(Id(args[1]), Id(args[2]), Version(args[3]), ct);
                break;
            case "ops-recovery-approve" when args.Length == 6:
                var tenant = Id(args[1]); var operation = Id(args[2]); var version = Version(args[3]);
                var upgrade = args[5] switch { "keep-plan" => false, "allow-approval-upgrade" => true, _ => throw Invalid() };
                var document = await ReadAclAsync(args[4], ct);
                result = await maintenance.ApproveAsync(tenant, operation, version, document,
                    TenancyConnections.Required("SAAS_RECOVERY_REASON"), upgrade, ct);
                break;
            case "ops-recovery-resume" when args.Length == 4:
                result = await maintenance.ResumeRecoveryAsync(Id(args[1]), Id(args[2]), Version(args[3]), ct);
                break;
            case "ops-purge" when args.Length == 5:
                var target = Id(args[1]);
                if (Id(args[4]) != target) throw new TenantContractException("purge_confirmation_mismatch");
                result = await maintenance.Operations.PurgeAsync(target, Id(args[2]), Version(args[3]), ct);
                break;
            default: throw Invalid();
        }
        Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static async Task ListAsync(TenancyConnections connections, CancellationToken ct, Guid? selected = null)
    {
        await using var sql = await SqlOperations.OpenAsync(connections.Platform("operator"), ct);
        _ = await SqlOperations.ScalarAsync(sql, """
            SELECT CASE WHEN DB_NAME() = 'saas_platform' AND IS_ROLEMEMBER('saas_operator') = 1
                AND IS_SRVROLEMEMBER('sysadmin') = 0 THEN ORIGINAL_LOGIN() ELSE NULL END;
            """, ct) as string ?? throw new TenantContractException("operator_not_authorized");
        await using var db = TenancyConnections.PlatformContext(sql);
        await TenancyConnections.VerifySchemaAsync(db, ct);
        // Project only public operational fields, never secret references, connections or approved ACL payloads.
        var rows = await db.Tenants.AsNoTracking().Where(x => selected == null || x.Id == selected)
            .OrderBy(x => x.Slug).Select(x => new
            {
                tenantId = x.Id, x.Slug, subscription = x.Subscription.ToString(), x.ExpiresAt, x.Plan,
                x.RegistryVersion, operation = x.Operation.ToString(), x.OperationId, x.OwnerId, x.Fence,
                x.AuthorizationGeneration, x.SecurityRevalidationRequired, x.RetiredAt, x.PurgedAt
            }).ToArrayAsync(ct);
        Console.WriteLine(JsonSerializer.Serialize(rows));
    }
    private static async Task<AclDocument> ReadAclAsync(string path, CancellationToken ct)
    {
        const int limit = 1024 * 1024;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (input.Length is < 1 or > limit) throw Invalid();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await input.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + count > limit) throw Invalid();
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        var document = JsonSerializer.Deserialize<AclDocument>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16 }) ?? throw Invalid();
        ApplicationAccessProfile.Catalog.Validate(document);
        return document;
    }
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    private static long Version(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0 ? version : throw Invalid();
    private static TenantContractException Invalid() => new("invalid_m3_command");
}
