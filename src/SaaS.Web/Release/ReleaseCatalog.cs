using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.OrderApprovals.Data;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Release;
namespace SaaS.Web.Release;

public sealed record ReleaseBuildEntry(string Project, ReleaseStream Stream);

/// <summary>Only the application composition root knows the complete product migration inventory.</summary>
public static class ReleaseCatalog
{
    private const string Offline = "Server=localhost;Database=saas_design_only;Integrated Security=True";
    public static ReleaseBuildEntry[] Describe(string runtimeIdentifier)
    {
        if (runtimeIdentifier is not ("linux-x64" or "win-x64")) throw new TenantContractException("release_runtime_unsupported");
        var suffix = runtimeIdentifier == "win-x64" ? ".exe" : "";
        var result = new List<ReleaseBuildEntry>();
        Add(TenancyConnections.PlatformContext(Offline), "platform", "platform", "src/Platform/SaaS.Tenancy");
        Add(TenancyConnections.CustomerContext(Offline), "tenancy", "tenant", "src/Platform/SaaS.Tenancy");
        Add(AccessDbContext.Open(Offline), "access", "tenant", "src/Platform/SaaS.AccessControl");
        Add(AuditDbContext.Open(Offline), "audit", "tenant", "src/Platform/SaaS.Auditing");
        Add(OrdersDbContext.Open(Offline), "orders", "tenant", "src/Modules/SaaS.Orders");
        Add(ApprovalsDbContext.Open(Offline), "approvals", "tenant", "src/Modules/SaaS.OrderApprovals");
        return result.ToArray();
        void Add(DbContext db, string id, string target, string project)
        {
            using (db) result.Add(new(project, new(id, target, db.GetType().Name, id,
                "bundles/" + id + suffix, "sql/" + id + ".sql", db.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray(), ["Standard", "Approval"])));
        }
    }

    public static void DemandCompatible(ReleasePackage package)
    {
        var runtime = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unsupported";
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || package.Manifest.RuntimeIdentifier != runtime)
            throw new TenantContractException("release_runtime_mismatch");
        var expected = Describe(runtime).Select(x => x.Stream).ToArray();
        if (package.Manifest.Streams.Length != expected.Length) throw new TenantContractException("release_context_inventory_mismatch");
        foreach (var stream in expected)
        {
            var actual = package.Manifest.Streams.SingleOrDefault(x => x.Id == stream.Id);
            if (actual is null || actual.Target != stream.Target || actual.Context != stream.Context || actual.HistorySchema != stream.HistorySchema
                || actual.Bundle != stream.Bundle || actual.Sql != stream.Sql
                || !actual.Migrations.SequenceEqual(stream.Migrations, StringComparer.Ordinal)
                || !actual.Plans.SequenceEqual(stream.Plans, StringComparer.Ordinal))
                throw new TenantContractException("release_context_inventory_mismatch");
        }
        foreach (var required in new[] { "host/SaaS.Web.dll", "host/SaaS.Web.deps.json", "host/SaaS.Web.runtimeconfig.json" }) _ = package.FilePath(required);
    }

    internal static DbContext Open(string id, string connection) => id switch
    {
        "platform" => TenancyConnections.PlatformContext(connection),
        "tenancy" => TenancyConnections.CustomerContext(connection),
        "access" => AccessDbContext.Open(connection), "audit" => AuditDbContext.Open(connection),
        "orders" => OrdersDbContext.Open(connection), "approvals" => ApprovalsDbContext.Open(connection),
        _ => throw new TenantContractException("release_context_inventory_mismatch")
    };
}
