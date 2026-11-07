using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Access;

/// <summary>Product composition, not a business-module dependency of the platform.</summary>
public static class ApplicationAccessProfile
{
    public static PermissionCatalog Catalog { get; } = new([
        new("Access.Manage", "Core", Delegable: false), new("Workspace.View", "Core"),
        new("Workspace.Approval", "Approval"), new("Profile.ReadOwn", "Core", ResourceRule.Owner)]);
    public static IReadOnlySet<string> Entitlements(string plan) => plan switch
    {
        "Standard" => new HashSet<string>(["Core"], StringComparer.Ordinal),
        "Approval" => new HashSet<string>(["Core", "Approval"], StringComparer.Ordinal),
        _ => throw new TenantContractException("application_plan_invalid")
    };
    public static string Hash(string plan)
    {
        _ = Entitlements(plan);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "FoundationM3:v1\nAccess.Manage:Core:Nondelegable\nWorkspace.View:Core\nWorkspace.Approval:Approval\nProfile.ReadOwn:Core:Owner\n" + plan)));
    }
    public static TenantDbContext Open(DbConnection connection) => new(new DbContextOptionsBuilder<TenantDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);

    public static async Task VerifyAsync(Guid tenantId, string plan, DbConnection connection, CancellationToken ct)
    {
        await using var tenancy = Open(connection);
        await using var access = AccessDbContext.Open(connection);
        await using var audit = AuditDbContext.Open(connection);
        await TenancyConnections.VerifySchemaAsync(tenancy, ct);
        await TenancyConnections.VerifySchemaAsync(access, ct);
        await TenancyConnections.VerifySchemaAsync(audit, ct);
        var profile = await tenancy.ApplicationProfile.AsNoTracking().SingleAsync(ct);
        if (profile.Id != 1 || profile.TenantId != tenantId || profile.Version != 1 || profile.Plan != plan || profile.ManifestHash != Hash(plan))
            throw new TenantContractException("application_profile_mismatch");
    }

    public static async Task DemandMembershipsAsync(TenancyConnections connections, string role, Guid tenantId,
        IReadOnlyCollection<Guid> users, CancellationToken ct)
    {
        await using var platform = TenancyConnections.PlatformContext(connections.Platform(role));
        var ids = users.Distinct().ToArray();
        // Bounded batches stay under SQL Server's parameter limit. Missing/disabled accounts never inherit restored rights.
        foreach (var batch in ids.Chunk(500))
        {
            var count = await (from membership in platform.IdentityMemberships.AsNoTracking()
                               join account in platform.IdentityAccounts.AsNoTracking() on membership.GlobalUserId equals account.Id
                               where membership.TenantId == tenantId && membership.Active && account.Enabled && batch.Contains(account.Id)
                               select account.Id).CountAsync(ct);
            if (count != batch.Length) throw new AccessDeniedException(403, "membership_required");
        }
    }
}

/// <summary>No business module is installed in the M3 product. Unknown schemas/tables fail closed;
/// M4/M5 must replace this reader with readers for their persisted approval obligations.</summary>
internal sealed class FoundationObligationReader : IApprovalObligationReader
{
    public async Task<ObligationResult> ReadAsync(TenantContext tenant, DbConnection connection, CancellationToken cancellationToken)
    {
        await using var tenancy = ApplicationAccessProfile.Open(connection);
        await using var access = AccessDbContext.Open(connection);
        await using var audit = AuditDbContext.Open(connection);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "dbo.oc_Document", "dbo.oc_Identifier", "dbo.oc_SaaSFoundation",
            "tenancy.__EFMigrationsHistory", "access.__EFMigrationsHistory", "audit.__EFMigrationsHistory"
        };
        foreach (var db in new DbContext[] { tenancy, access, audit })
            foreach (var table in db.Model.GetEntityTypes()) allowed.Add(table.GetSchema() + "." + table.GetTableName());
        var identity = await tenancy.Identity.AsNoTracking().SingleAsync(cancellationToken);
        if (identity.TenantId != tenant.TenantId) return new(ObligationState.Unknown, 0);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.tables WHERE is_ms_shipped = 0";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!allowed.Contains(reader.GetString(0))) return new(ObligationState.Unknown, 0);
        return new(ObligationState.Clear, 0);
    }
}

internal sealed class ApplicationPlanReconciler(TenancyConnections connections) : IPlanReconciler
{
    public async Task MigrateAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        _ = ApplicationAccessProfile.Entitlements(targetPlan);
        await using var access = AccessDbContext.Open(ownedConnection);
        await using var audit = AuditDbContext.Open(ownedConnection);
        await access.Database.MigrateAsync(ct);
        await audit.Database.MigrateAsync(ct);
        // Never replace existing ACL during an ordinary install/plan transition or trust its historical generation.
        if (!await access.State.AnyAsync(ct))
        {
            if (await access.Roles.AnyAsync(ct) || await access.Assignments.AnyAsync(ct) || await access.UserGrants.AnyAsync(ct)
                || await access.RoleGrants.AnyAsync(ct) || await access.History.AnyAsync(ct) || await audit.Events.AnyAsync(ct))
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
        // The M3 workspace is an explicit host feature. It does not enable native CMS or future Orders modules.
        await using var db = ApplicationAccessProfile.Open(ownedConnection);
        var profile = await db.ApplicationProfile.SingleOrDefaultAsync(ct);
        if (profile is null) { profile = new TenantApplicationProfile { TenantId = tenant.TenantId }; db.ApplicationProfile.Add(profile); }
        if (profile.TenantId != tenant.TenantId) throw new TenantContractException("application_identity_mismatch");
        profile.Plan = targetPlan; profile.ManifestHash = ApplicationAccessProfile.Hash(targetPlan); profile.Version = 1;
        await db.SaveChangesAsync(ct);
        await SqlOperations.ExecuteAsync((SqlConnection)ownedConnection, """
            DECLARE @principal nvarchar(258) = QUOTENAME(@login);
            DECLARE @ddl nvarchar(max) = N'GRANT SELECT ON SCHEMA::[access] TO ' + @principal + N';'
                + N'GRANT SELECT ON SCHEMA::[audit] TO ' + @principal + N';'
                + N'GRANT SELECT ON SCHEMA::[tenancy] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON SCHEMA::[tenancy] TO ' + @principal + N';';
            SELECT @ddl = @ddl + N'GRANT INSERT, UPDATE, DELETE ON [access].' + QUOTENAME(name) + N' TO ' + @principal + N';'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('access') AND name IN ('State', 'Roles', 'RoleGrants', 'Assignments', 'UserGrants');
            SET @ddl = @ddl + N'GRANT INSERT ON [access].[History] TO ' + @principal + N';'
                + N'DENY UPDATE, DELETE ON [access].[History] TO ' + @principal + N';'
                + N'GRANT INSERT ON [audit].[Events] TO ' + @principal + N';'
                + N'DENY UPDATE, DELETE ON [audit].[Events] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON [access].[__EFMigrationsHistory] TO ' + @principal + N';'
                + N'DENY INSERT, UPDATE, DELETE ON [audit].[__EFMigrationsHistory] TO ' + @principal + N';';
            EXEC sys.sp_executesql @ddl;
            """, ct, ("@login", "saas_rt_" + tenant.TenantId.ToString("N")));
    }
    public async Task<bool> VerifyAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        await ApplicationAccessProfile.VerifyAsync(tenant.TenantId, targetPlan, ownedConnection, ct);
        return true;
    }
}
