using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

internal static class PhysicalRestoreBarrier
{
    internal const string Requested = "PhysicalRestoreRequested";
    internal const string Bound = "PhysicalRestoreBound";
    internal const string Verified = "PhysicalRestoreIdentityVerified";

    internal static async Task DemandClearAsync(SqlConnection connection, string resource, CancellationToken ct)
    {
        if (!resource.StartsWith("tenant-", StringComparison.Ordinal)
            || !Guid.TryParseExact(resource[7..], "N", out var id)) return;
        await using var db = TenancyConnections.PlatformContext(connection);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (tenant?.OperationId is not Guid operation) return;
        if (await db.Audit.AnyAsync(x => x.TenantId == id && x.OperationId == operation && (x.Action == Bound || x.Action == Requested), ct)
            && !await db.Audit.AnyAsync(x => x.TenantId == id && x.OperationId == operation && x.Action == Verified, ct))
            throw new TenantContractException("physical_restore_incomplete");
    }
}
