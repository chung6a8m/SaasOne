using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Access;

/// <summary>Only the product host composes business modules and their persisted recovery readers.</summary>
public static class ApplicationAccessProfile
{
    public static PermissionCatalog Catalog { get; } = new([
        new("Access.Manage", "Core", Delegable: false), new("Workspace.View", "Core"),
        new("Workspace.Approval", "Approval"), new("Profile.ReadOwn", "Core", ResourceRule.Owner), .. OrderPermissions.Definitions]);
    public static IReadOnlySet<string> Entitlements(string plan) => plan switch
    {
        "Standard" => new HashSet<string>(["Core", "Orders"], StringComparer.Ordinal),
        "Approval" => new HashSet<string>(["Core", "Orders", "Approval"], StringComparer.Ordinal),
        _ => throw new TenantContractException("application_plan_invalid")
    };
    public static string Hash(string plan)
    {
        _ = Entitlements(plan);
        return Digest("OrdersM4:v2\n" + LegacyHash(plan) + "\n" + string.Join("\n", OrderPermissions.Definitions.Select(x => $"{x.Code}:{x.Entitlement}:{x.ResourceRule}:{x.Delegable}")));
    }
    internal static string LegacyHash(string plan) => Digest("FoundationM3:v1\nAccess.Manage:Core:Nondelegable\nWorkspace.View:Core\nWorkspace.Approval:Approval\nProfile.ReadOwn:Core:Owner\n" + plan);
    private static string Digest(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public static TenantDbContext Open(DbConnection connection) => new(new DbContextOptionsBuilder<TenantDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);
    public static async Task VerifyAsync(Guid tenantId, string plan, DbConnection connection, CancellationToken ct)
    {
        await using var tenancy = Open(connection); await using var access = AccessDbContext.Open(connection);
        await using var audit = AuditDbContext.Open(connection); await using var orders = OrdersDbContext.Open(connection);
        foreach (var db in new DbContext[] { tenancy, access, audit, orders }) await TenancyConnections.VerifySchemaAsync(db, ct);
        var profile = await tenancy.ApplicationProfile.AsNoTracking().SingleAsync(ct);
        if (profile.Id != 1 || profile.TenantId != tenantId || profile.Version != 2 || profile.Plan != plan || profile.ManifestHash != Hash(plan))
            throw new TenantContractException("application_profile_mismatch");
    }
    public static async Task DemandMembershipsAsync(TenancyConnections connections, string role, Guid tenantId,
        IReadOnlyCollection<Guid> users, CancellationToken ct)
    {
        await using var platform = TenancyConnections.PlatformContext(connections.Platform(role));
        foreach (var batch in users.Distinct().Chunk(500))
        {
            var count = await (from membership in platform.IdentityMemberships.AsNoTracking()
                               join account in platform.IdentityAccounts.AsNoTracking() on membership.GlobalUserId equals account.Id
                               where membership.TenantId == tenantId && membership.Active && account.Enabled && batch.Contains(account.Id)
                               select account.Id).CountAsync(ct);
            if (count != batch.Length) throw new AccessDeniedException(403, "membership_required");
        }
    }
}

/// <summary>Closed inventory plus module-independent persisted Orders obligations. No runtime feature toggles.</summary>
internal sealed class FoundationObligationReader : IApprovalObligationReader
{
    public async Task<ObligationResult> ReadAsync(TenantContext tenant, DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var tenancy = ApplicationAccessProfile.Open(connection); await using var access = AccessDbContext.Open(connection);
            await using var audit = AuditDbContext.Open(connection); await using var orders = OrdersDbContext.Open(connection);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "dbo.oc_Document", "dbo.oc_Identifiers", "dbo.oc_SaaSFoundation",
                "tenancy.__EFMigrationsHistory", "access.__EFMigrationsHistory", "audit.__EFMigrationsHistory", "orders.__EFMigrationsHistory"
            };
            foreach (var db in new DbContext[] { tenancy, access, audit, orders })
                foreach (var table in db.Model.GetEntityTypes()) allowed.Add(table.GetSchema() + "." + table.GetTableName());
            var identity = await tenancy.Identity.AsNoTracking().SingleAsync(cancellationToken);
            if (identity.TenantId != tenant.TenantId) return new(ObligationState.Unknown, 0);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.tables WHERE is_ms_shipped = 0";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var table = reader.GetString(0);
                    if (!allowed.Contains(table)) return new(ObligationState.Unknown, 0);
                    actual.Add(table);
                }
            }
            var profile = actual.Contains("tenancy.ApplicationProfile")
                ? await tenancy.ApplicationProfile.AsNoTracking().SingleOrDefaultAsync(cancellationToken) : null;
            if (profile is not null)
            {
                if (profile.Id != 1 || profile.TenantId != tenant.TenantId || profile.Plan is not ("Standard" or "Approval"))
                    return new(ObligationState.Unknown, 0);
                var expectedHash = profile.Version switch
                {
                    1 => ApplicationAccessProfile.LegacyHash(profile.Plan),
                    2 => ApplicationAccessProfile.Hash(profile.Plan),
                    _ => null
                };
                // A readable current schema cannot authorize downgrading an unknown future profile.
                if (expectedHash is null || profile.ManifestHash != expectedHash) return new(ObligationState.Unknown, 0);
            }
            if (actual.Any(x => x.StartsWith("orders.", StringComparison.Ordinal)))
                return await new OrdersObligationReader().ReadAsync(tenant, connection, cancellationToken);
            // Only the verified pre-Orders foundation can be empty. An installed M4 profile with missing schema is corruption.
            if (profile is { Version: >= 2 }) return new(ObligationState.Unknown, 0);
            return new(ObligationState.Clear, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(ObligationState.Unknown, 0); }
    }
}

internal sealed class ApplicationPlanReconciler(TenancyConnections connections) : IPlanReconciler
{
    public async Task MigrateAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        _ = ApplicationAccessProfile.Entitlements(targetPlan);
        await using var tenancy = ApplicationAccessProfile.Open(ownedConnection);
        await tenancy.Database.MigrateAsync(ct);
        await using var access = AccessDbContext.Open(ownedConnection); await using var audit = AuditDbContext.Open(ownedConnection);
        await using var orders = OrdersDbContext.Open(ownedConnection);
        var installed = await tenancy.ApplicationProfile.AsNoTracking().SingleOrDefaultAsync(ct);
        if (installed is { Version: >= 2 } && (await new OrdersObligationReader().ReadAsync(tenant, ownedConnection, ct)).State == ObligationState.Unknown)
            throw new TenantContractException("orders_recovery_required");
        await access.Database.MigrateAsync(ct); await audit.Database.MigrateAsync(ct); await orders.Database.MigrateAsync(ct);
        if (!await access.State.AnyAsync(ct))
        {
            if (await access.Roles.AnyAsync(ct) || await access.Assignments.AnyAsync(ct) || await access.UserGrants.AnyAsync(ct)
                || await access.RoleGrants.AnyAsync(ct) || await access.History.AnyAsync(ct) || await audit.Events.AnyAsync(ct) || await orders.Orders.AnyAsync(ct))
                throw new TenantContractException("access_revalidation_required");
            await using var platform = TenancyConnections.PlatformContext(connections.Platform("operator"));
            var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.TenantId, ct);
            if (current.SecurityRevalidationRequired || current.Operation != OperationStatus.PlanChanging || current.OperationId is null)
                throw new TenantContractException("access_revalidation_required");
            access.State.Add(new AclState { TenantId = tenant.TenantId, Revision = 1, SecurityGeneration = current.AuthorizationGeneration,
                AppliedOperationId = current.OperationId.Value, AclHash = AclDocument.Empty.Hash() });
            await access.SaveChangesAsync(ct);
        }
    }
    public async Task ApplyFeaturesAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        await using var db = ApplicationAccessProfile.Open(ownedConnection);
        var profile = await db.ApplicationProfile.SingleOrDefaultAsync(ct);
        if (profile is null) { profile = new TenantApplicationProfile { TenantId = tenant.TenantId }; db.ApplicationProfile.Add(profile); }
        if (profile.TenantId != tenant.TenantId) throw new TenantContractException("application_identity_mismatch");
        profile.Plan = targetPlan; profile.ManifestHash = ApplicationAccessProfile.Hash(targetPlan); profile.Version = 2;
        await db.SaveChangesAsync(ct);
        await SqlOperations.ExecuteAsync((SqlConnection)ownedConnection, """
            DECLARE @principal nvarchar(258) = QUOTENAME(@login);
            DECLARE @ddl nvarchar(max) = N'GRANT SELECT ON SCHEMA::[access] TO ' + @principal + N';'
                + N'GRANT SELECT ON SCHEMA::[audit] TO ' + @principal + N';'
                + N'GRANT SELECT ON SCHEMA::[orders] TO ' + @principal + N';'
                + N'GRANT SELECT ON SCHEMA::[tenancy] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON SCHEMA::[tenancy] TO ' + @principal + N';';
            SELECT @ddl = @ddl + N'GRANT INSERT, UPDATE, DELETE ON [access].' + QUOTENAME(name) + N' TO ' + @principal + N';'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('access') AND name IN ('State', 'Roles', 'RoleGrants', 'Assignments', 'UserGrants');
            SET @ddl = @ddl + N'GRANT INSERT ON [access].[History] TO ' + @principal + N';'
                + N'DENY UPDATE, DELETE ON [access].[History] TO ' + @principal + N';'
                + N'GRANT INSERT ON [audit].[Events] TO ' + @principal + N';'
                + N'DENY UPDATE, DELETE ON [audit].[Events] TO ' + @principal + N';'
                + N'GRANT INSERT, UPDATE ON [orders].[Orders] TO ' + @principal + N';'
                + N'DENY DELETE ON [orders].[Orders] TO ' + @principal + N';'
                + N'GRANT INSERT ON [orders].[Revisions] TO ' + @principal + N';'
                + N'DENY UPDATE, DELETE ON [orders].[Revisions] TO ' + @principal + N';'
                + N'GRANT INSERT ON [orders].[Receipts] TO ' + @principal + N';'
                + N'DENY UPDATE, DELETE ON [orders].[Receipts] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON [access].[__EFMigrationsHistory] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON [audit].[__EFMigrationsHistory] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON [orders].[__EFMigrationsHistory] TO ' + @principal + N';';
            EXEC sys.sp_executesql @ddl;
            """, ct, ("@login", "saas_rt_" + tenant.TenantId.ToString("N")));
    }
    public async Task<bool> VerifyAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        await ApplicationAccessProfile.VerifyAsync(tenant.TenantId, targetPlan, ownedConnection, ct);
        return true;
    }
}
