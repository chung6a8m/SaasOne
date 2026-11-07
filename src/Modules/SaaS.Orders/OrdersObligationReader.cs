using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
namespace SaaS.Orders;

/// <summary>Recovery/plan-change reader. The caller drains writers first. No entitlement or handler is needed.</summary>
public sealed class OrdersObligationReader : IApprovalObligationReader
{
    public async Task<ObligationResult> ReadAsync(TenantContext tenant, DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var identity = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);
            var source = await identity.Identity.AsNoTracking().SingleAsync(cancellationToken);
            if (tenant.TenantId == Guid.Empty || source.TenantId != tenant.TenantId) return Unknown;
            await using var db = OrdersDbContext.Open(connection);
            await TenancyConnections.VerifySchemaAsync(db, cancellationToken);
            // Validate physical schema even for an empty table; a history row alone cannot establish readiness.
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT TOP(0) [Id],[CreatedBy],[Number],[CustomerName],[Amount],[Currency],[Notes],[Status],[Revision],[Version],[ApprovalRequired],[CreatedAt],[UpdatedAt] FROM [orders].[Orders]; SELECT TOP(0) [OrderId],[Revision],[ActorId],[Action],[Status],[ApprovalRequired],[SnapshotJson],[OccurredAt] FROM [orders].[Revisions]; SELECT TOP(0) [PartitionHash],[TenantId],[ActorId],[ClientId],[Action],[RequestHash],[OrderId],[ResultRevision],[ResultJson],[CompletedAt],[ExpiresAt] FROM [orders].[Receipts];";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (await db.Revisions.AnyAsync(r => r.Revision < 1 || r.ActorId == Guid.Empty || r.Action < OrderAction.Create || r.Action > OrderAction.Reject
                || r.Status < OrderStatus.Draft || r.Status > OrderStatus.Confirmed
                || (r.Status == OrderStatus.PendingApproval || r.Status == OrderStatus.Rejected) && !r.ApprovalRequired
                || !db.Orders.Any(o => o.Id == r.OrderId && o.Revision >= r.Revision), cancellationToken)) return Unknown;
            var query = db.Orders.AsNoTracking().Select(order => new
            {
                Order = order,
                Snapshot = db.Revisions.Where(r => r.OrderId == order.Id && r.Revision == order.Revision).Select(r => r.SnapshotJson).FirstOrDefault(),
                Count = db.Revisions.LongCount(r => r.OrderId == order.Id),
                WasRequired = db.Revisions.Any(r => r.OrderId == order.Id && r.ApprovalRequired)
            });
            var count = 0;
            await foreach (var item in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var order = item.Order.View(); OrderPolicy.DemandIntegrity(order);
                if (item.Count != order.Revision || item.Snapshot is null || item.WasRequired != order.ApprovalRequired
                    || JsonSerializer.Deserialize<OrderView>(item.Snapshot) != order) return Unknown;
                if (order.ApprovalRequired && order.Status != OrderStatus.Confirmed) count = checked(count + 1);
            }
            return new(count == 0 ? ObligationState.Clear : ObligationState.Outstanding, count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Unknown; }
    }
    private static ObligationResult Unknown => new(ObligationState.Unknown, 0);
}
