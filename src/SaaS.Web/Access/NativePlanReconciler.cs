using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Web.Orchard;
namespace SaaS.Web.Access;

/// <summary>All host-composed recovery paths verify the real native shell before opening traffic.</summary>
internal sealed class NativePlanReconciler(TenancyConnections connections) : IPlanReconciler
{
    private readonly ApplicationPlanReconciler _application = new(connections);
    public async Task MigrateAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        await _application.MigrateAsync(tenant, targetPlan, ownedConnection, ct);
        await new OrchardMaintenance().UpgradeAsync(tenant.ShellName, tenant.UrlPrefix, tenant.TenantId, Tagged(tenant, ownedConnection), ct);
    }
    public Task ApplyFeaturesAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct) =>
        _application.ApplyFeaturesAsync(tenant, targetPlan, ownedConnection, ct);
    public async Task<bool> VerifyAsync(TenantContext tenant, string targetPlan, DbConnection ownedConnection, CancellationToken ct)
    {
        if (!await _application.VerifyAsync(tenant, targetPlan, ownedConnection, ct)) return false;
        var native = await new OrchardMaintenance().VerifyAsync(tenant.ShellName, tenant.UrlPrefix, tenant.TenantId, Tagged(tenant, ownedConnection), ct);
        await using var platform = TenancyConnections.PlatformContext(connections.Platform("operator"));
        var registration = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.TenantId, ct);
        await SaaS.Tenancy.Operations.TenantDatabaseVerifier.VerifyAsync(registration, connections.Customer(tenant.TenantId, "runtime"), native, ct);
        return true;
    }
    private string Tagged(TenantContext tenant, DbConnection owned) => new SqlConnectionStringBuilder(connections.Customer(tenant.TenantId, "migrator"))
    {
        // Open SqlConnection strips its password; only copy its non-secret operation tag, never its connection string.
        ApplicationName = new SqlConnectionStringBuilder(owned.ConnectionString).ApplicationName
    }.ConnectionString;
}
