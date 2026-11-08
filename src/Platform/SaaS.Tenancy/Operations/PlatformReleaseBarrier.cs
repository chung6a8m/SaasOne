using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

public static class PlatformReleaseBarrier
{
    public const string Resource = "platform-release";
    public const string Lock = "saas-platform-schema";
    public const string Bound = "PlatformReleaseBound";
    public const string Completed = "PlatformReleaseCompleted";
    public static async Task DemandClearAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var db = TenancyConnections.PlatformContext(connection);
        if (await db.Audit.AnyAsync(start => start.TenantId == null && start.Action == Bound
            && !db.Audit.Any(end => end.TenantId == null && end.OperationId == start.OperationId && end.Action == Completed), ct))
            throw new TenantContractException("platform_release_incomplete");
    }
}
