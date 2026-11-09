using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Auditing;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Orders;

/// <summary>One policy and one physical transaction for UI, API and future trusted application jobs.</summary>
public sealed class OrderService(string connection, Guid tenantId, PermissionCatalog catalog,
    Func<TenantTransaction, CancellationToken, Task<OrderAccessContext>> currentAccess,
    IOrderApprovalHandler? approvalHandler = null, TimeProvider? clock = null, string? correlationId = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    public Task<OrderCommandResult> ExecuteAsync(OrderCommand command, CancellationToken ct) => BoundaryAsync(async () =>
    {
        var request = OrderPolicy.Normalize(command);
        var fingerprint = OrderPolicy.Fingerprint(request);
        return await InTransactionAsync(async (tx, token) =>
        {
            var initial = await AccessAsync(tx, token);
            var partition = OrderPolicy.Partition(tenantId, initial.Current.UserId, initial.ClientId, request.Action, request.Key);
            await LockAsync(tx, "orders-command:" + partition, true, token);
            var access = await AccessAsync(tx, token);
            SameIdentity(initial, access);
            await using var db = OrdersDbContext.Open(tx.Connection);
            await db.Database.UseTransactionAsync(tx.Transaction, token);
            await TenancyConnections.VerifySchemaAsync(db, token);
            // A committed receipt is checked BEFORE state, revision or rowversion preconditions.
            var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.PartitionHash == partition, token);
            if (receipt is not null)
            {
                await LockAsync(tx, ResourceLock(receipt.OrderId), false, token);
                access = await AccessAsync(tx, token); SameIdentity(initial, access);
                var current = (await LoadAsync(db, receipt.OrderId, token)).View();
                OrderPermissions.Require(catalog, access, request.Action, current);
                if (receipt.TenantId != tenantId || receipt.ActorId != access.Current.UserId || receipt.ClientId != access.ClientId
                    || receipt.Action != request.Action || receipt.ResultRevision < 1 || receipt.ResultRevision > current.Revision)
                    throw new AccessDeniedException(503, "order_receipt_invalid");
                if (receipt.RequestHash != fingerprint) throw new AccessDeniedException(409, "idempotency_conflict");
                if (receipt.ExpiresAt <= Now) throw new AccessDeniedException(409, "idempotency_expired");
                var stored = JsonSerializer.Deserialize<OrderView>(receipt.ResultJson) ?? throw new AccessDeniedException(503, "order_receipt_invalid");
                OrderPolicy.DemandIntegrity(stored);
                if (stored.Id != current.Id || stored.CreatedBy != current.CreatedBy || stored.Revision != receipt.ResultRevision)
                    throw new AccessDeniedException(503, "order_receipt_invalid");
                return new OrderCommandResult(stored, true, receipt.ExpiresAt);
            }
            var id = request.OrderId ?? Guid.NewGuid();
            await LockAsync(tx, ResourceLock(id), true, token);
            access = await AccessAsync(tx, token); SameIdentity(initial, access);
            OrderRow row;
            OrderView? before = null;
            if (request.Action == OrderAction.Create)
            {
                OrderPermissions.Require(catalog, access, request.Action, null);
                row = new() { Id = id, CreatedBy = access.Current.UserId, Status = OrderStatus.Draft,
                    Revision = 1, CreatedAt = Now, UpdatedAt = Now };
                row.Apply(request.Draft!);
                db.Orders.Add(row);
            }
            else
            {
                row = await LoadAsync(db, id, token);
                before = row.View();
                OrderPermissions.Require(catalog, access, request.Action, before);
                var next = OrderPolicy.Next(before, request, access.Current.Entitlements.Contains("Approval"), approvalHandler is not null);
                if (next.Status == OrderStatus.PendingApproval || request.Action is OrderAction.Approve or OrderAction.Reject)
                    await approvalHandler!.DemandReadyAsync(tx, token);
                if (request.Draft is not null) row.Apply(request.Draft);
                row.Status = next.Status; row.Revision = next.Revision; row.ApprovalRequired = next.ApprovalRequired; row.UpdatedAt = Now;
            }
            await db.SaveChangesAsync(token); // Obtain this revision's database-generated rowversion.
            var after = row.View();
            if (request.Action == OrderAction.Submit && after.Status == OrderStatus.PendingApproval)
                await approvalHandler!.SubmittedAsync(new(after.Id, after.CreatedBy, after.Revision, after.Version, access.Current.UserId), tx, token);
            if (request.Action is OrderAction.Approve or OrderAction.Reject)
                await approvalHandler!.DecidedAsync(new(after.Id, before!.Revision, before.Version, after.Revision,
                    access.Current.UserId, request.Action == OrderAction.Approve, request.Reason), tx, token);
            // A slow handler must not turn a previously valid authority read into a cached allow.
            access = await AccessAsync(tx, token); SameIdentity(initial, access);
            OrderPermissions.Require(catalog, access, request.Action, after);
            var completed = Now;
            var snapshot = JsonSerializer.Serialize(after);
            db.Revisions.Add(new() { OrderId = id, Revision = after.Revision, ActorId = access.Current.UserId, Action = request.Action,
                Status = after.Status, ApprovalRequired = after.ApprovalRequired, SnapshotJson = snapshot, OccurredAt = completed });
            await TenantAuditWriter.AppendAsync(tx, new TenantAuditEvent
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ActorId = access.Current.UserId, Action = "Orders." + request.Action,
                PayloadHash = fingerprint, SecurityGeneration = access.Current.SecurityGeneration, ResultRevision = after.Revision,
                OccurredAt = completed, ResourceType = "Order", ResourceId = id, CorrelationId = correlationId ?? partition[..32],
                ChangesJson = JsonSerializer.Serialize(new { Before = before, After = after, request.Reason, access.ClientId })
            }, token);
            var expires = completed.AddDays(7);
            db.Receipts.Add(new() { PartitionHash = partition, TenantId = tenantId, ActorId = access.Current.UserId, ClientId = access.ClientId,
                Action = request.Action, RequestHash = fingerprint, OrderId = id, ResultRevision = after.Revision,
                ResultJson = snapshot, CompletedAt = completed, ExpiresAt = expires });
            await db.SaveChangesAsync(token);
            return new OrderCommandResult(after, false, expires);
        }, ct);
    }, ct);

    public Task<OrderView> GetAsync(Guid id, CancellationToken ct) => ReadAsync(id, includeDecisions: false, ct);
    public Task<OrderView> GetDetailAsync(Guid id, CancellationToken ct) => ReadAsync(id, includeDecisions: true, ct);

    private Task<OrderView> ReadAsync(Guid id, bool includeDecisions, CancellationToken ct) => BoundaryAsync(() => InTransactionAsync(async (tx, token) =>
    {
        if (id == Guid.Empty) throw new AccessDeniedException(404, "order_not_found");
        var initial = await AccessAsync(tx, token);
        await LockAsync(tx, ResourceLock(id), false, token);
        var access = await AccessAsync(tx, token); SameIdentity(initial, access);
        await using var db = OrdersDbContext.Open(tx.Connection);
        await db.Database.UseTransactionAsync(tx.Transaction, token);
        await TenancyConnections.VerifySchemaAsync(db, token);
        var value = (await LoadAsync(db, id, token)).View();
        OrderPermissions.RequireRead(catalog, access, value);
        if (includeDecisions && value.ApprovalRequired)
        {
            // The handler reads on this locked, caller-owned transaction, after Orders authorizes the reader.
            // Historical decisions stay visible after rework or a plan downgrade.
            if (approvalHandler is null) throw new AccessDeniedException(503, "approval_history_unavailable");
            await approvalHandler.DemandReadyAsync(tx, token);
            var decisions = await approvalHandler.ReadDecisionsAsync(value.Id, value.Revision, tx, token);
            if ((value.Status is OrderStatus.Rejected or OrderStatus.Confirmed)
                && !decisions.Any(x => x.ResultRevision == value.Revision && x.Approved == (value.Status == OrderStatus.Confirmed)))
                throw new AccessDeniedException(503, "approval_history_invalid");
            if (decisions.Length > 0) value = value with { ApprovalDecisions = decisions };
            // An intervening ACL revocation must not turn an earlier allow into a disclosed reason.
            var refreshed = await AccessAsync(tx, token); SameIdentity(initial, refreshed);
            OrderPermissions.RequireRead(catalog, refreshed, value);
        }
        return value;
    }, ct), ct);

    public Task<OrderPage> ListAsync(int page, CancellationToken ct) => BoundaryAsync(() => InTransactionAsync(async (tx, token) =>
    {
        if (page is < 0 or > 1000) throw new AccessDeniedException(400, "page_invalid");
        var access = await AccessAsync(tx, token);
        bool CanRead(bool own)
        {
            try
            {
                _ = new PermissionEvaluator(catalog).Require(access.Current, access.Snapshot, own ? "Orders.ViewOwn" : "Orders.ViewOthers",
                    new(tenantId, own ? access.Current.UserId : Guid.Empty, "list"));
                return true;
            }
            catch (AccessDeniedException error) when (error.StatusCode == 403 && error.Code == "permission_denied") { return false; }
        }
        var own = CanRead(true); var others = CanRead(false);
        if (!own && !others) throw new AccessDeniedException(403, "permission_denied");
        await using var db = OrdersDbContext.Open(tx.Connection);
        await db.Database.UseTransactionAsync(tx.Transaction, token);
        await TenancyConnections.VerifySchemaAsync(db, token);
        var actor = access.Current.UserId;
        var ids = await db.Orders.AsNoTracking().Where(x => own && x.CreatedBy == actor || others && x.CreatedBy != actor)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(page * 50).Take(51).Select(x => x.Id).ToArrayAsync(token);
        await SqlTransactionLocks.AcquireOrderedSharedAsync((SqlConnection)tx.Connection, (SqlTransaction)tx.Transaction,
            ids.Order().Select(ResourceLock).ToArray(), 30000, token);
        var refreshed = await AccessAsync(tx, token); SameIdentity(access, refreshed);
        // Resource locks and the refreshed current authority precede all batch reads.
        // Never run these reads concurrently on the enlisted transaction connection.
        var items = await LoadPageAsync(db, ids, catalog, refreshed, token);
        // Recheck even an empty page; a revoked entitlement must not produce a success from an old allow.
        access = refreshed;
        if (!CanRead(true) && !CanRead(false)) throw new AccessDeniedException(403, "permission_denied");
        return new OrderPage(items.Take(50).ToArray(), items.Count > 50);
    }, ct), ct);

    /// <summary>A current resource-aware UI hint; commands always authorize again inside their own transaction.</summary>
    public Task<bool> CanDecideAsync(Guid id, CancellationToken ct) => BoundaryAsync(() => InTransactionAsync(async (tx, token) =>
    {
        var initial = await AccessAsync(tx, token);
        await LockAsync(tx, ResourceLock(id), false, token);
        var access = await AccessAsync(tx, token); SameIdentity(initial, access);
        await using var db = OrdersDbContext.Open(tx.Connection);
        await db.Database.UseTransactionAsync(tx.Transaction, token);
        await TenancyConnections.VerifySchemaAsync(db, token);
        var current = (await LoadAsync(db, id, token)).View();
        OrderPermissions.RequireRead(catalog, access, current);
        if (current.Status != OrderStatus.PendingApproval || approvalHandler is null) return false;
        try { OrderPermissions.Require(catalog, access, OrderAction.Approve, current); return true; }
        catch (AccessDeniedException error) when (error.StatusCode == 403 && error.Code is "permission_denied" or "self_approval_forbidden") { return false; }
    }, ct), ct);

    public Task DemandCreateAsync(CancellationToken ct) => BoundaryAsync(() => InTransactionAsync(async (tx, token) =>
    {
        OrderPermissions.Require(catalog, await AccessAsync(tx, token), OrderAction.Create, null);
        return true;
    }, ct), ct);

    private async Task<T> InTransactionAsync<T>(Func<TenantTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        long version;
        await using (var sql = await SqlOperations.OpenAsync(connection, ct))
        {
            var gate = await TenantWriteGate.ReadAsync(sql, null, ct);
            if (gate.TenantId != tenantId || tenantId == Guid.Empty) throw new AccessDeniedException(403, "tenant_mismatch");
            if (!gate.IsOpen) throw new AccessDeniedException(503, "tenant_operation_in_progress");
            version = gate.Version;
        }
        return await new TenantTransactionCoordinator(connection).ExecuteAsync(tenantId, version, work, ct);
    }
    private async Task<OrderAccessContext> AccessAsync(TenantTransaction tx, CancellationToken ct)
    {
        var access = await currentAccess(tx, ct);
        if (access.Current.TenantId != tenantId || access.Snapshot.TenantId != tenantId) throw new AccessDeniedException(403, "tenant_mismatch");
        if (access.Current.UserId == Guid.Empty || string.IsNullOrWhiteSpace(access.ClientId)) throw new AccessDeniedException(401, "credential_invalid");
        return access;
    }
    private static void SameIdentity(OrderAccessContext first, OrderAccessContext next)
    {
        if (first.Current.TenantId != next.Current.TenantId || first.Current.UserId != next.Current.UserId || first.ClientId != next.ClientId)
            throw new AccessDeniedException(401, "credential_changed");
    }
    private static Task LockAsync(TenantTransaction tx, string resource, bool exclusive, CancellationToken ct) =>
        SqlTransactionLocks.AcquireAsync((SqlConnection)tx.Connection, (SqlTransaction)tx.Transaction, resource, exclusive, 30000, ct);
    private static string ResourceLock(Guid id) => "orders-resource:" + id.ToString("N");
    private static async Task<List<OrderView>> LoadPageAsync(OrdersDbContext db, Guid[] ids,
        PermissionCatalog catalog, OrderAccessContext access, CancellationToken ct)
    {
        var items = new List<OrderView>(ids.Length);
        if (ids.Length == 0) return items;

        // Four bounded SELECTs for a full page: page IDs, Orders, current revisions,
        // and grouped history counts/markers. The existing per-resource locks stay ordered.
        var rows = await db.Orders.AsNoTracking()
            .Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var latest = await (
            from revision in db.Revisions.AsNoTracking()
            join order in db.Orders.AsNoTracking() on revision.OrderId equals order.Id
            where ids.Contains(order.Id) && revision.Revision == order.Revision
            select revision
        ).ToDictionaryAsync(x => x.OrderId, ct);
        var history = await db.Revisions.AsNoTracking()
            .Where(x => ids.Contains(x.OrderId))
            .GroupBy(x => x.OrderId)
            .Select(group => new
            {
                OrderId = group.Key,
                Count = group.LongCount(),
                HistoricalMarker = group.Max(x => x.ApprovalRequired ? 1 : 0)
            }).ToDictionaryAsync(x => x.OrderId, ct);

        // The latest snapshot, exact revision count and *all* past approval markers
        // are still verified for every row, including the pagination sentinel.
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var row))
                throw new AccessDeniedException(404, "order_not_found");
            var current = row.View();
            OrderPolicy.DemandIntegrity(current);
            if (!latest.TryGetValue(id, out var revision)
                || revision.Status != row.Status || revision.ApprovalRequired != row.ApprovalRequired
                || JsonSerializer.Deserialize<OrderView>(revision.SnapshotJson) != current
                || !history.TryGetValue(id, out var summary) || summary.Count != row.Revision
                || !row.ApprovalRequired && summary.HistoricalMarker != 0)
                throw new AccessDeniedException(503, "order_history_invalid");
            // Preserve per-item integrity-then-current-permission failure ordering.
            OrderPermissions.RequireRead(catalog, access, current);
            items.Add(current);
        }
        return items;
    }

    private static async Task<OrderRow> LoadAsync(OrdersDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Orders.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AccessDeniedException(404, "order_not_found");
        var current = row.View(); OrderPolicy.DemandIntegrity(current);
        var latest = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id && x.Revision == row.Revision, ct);
        if (latest is null || latest.Status != row.Status || latest.ApprovalRequired != row.ApprovalRequired
            || JsonSerializer.Deserialize<OrderView>(latest.SnapshotJson) != current
            || await db.Revisions.LongCountAsync(x => x.OrderId == id, ct) != row.Revision
            || !row.ApprovalRequired && await db.Revisions.AnyAsync(x => x.OrderId == id && x.ApprovalRequired, ct))
            throw new AccessDeniedException(503, "order_history_invalid");
        return row;
    }
    private static async Task<T> BoundaryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (AccessDeniedException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException) { throw new AccessDeniedException(409, "order_revision_changed"); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw new AccessDeniedException(409, "order_conflict"); }
        catch (TenantContractException error) when (error.Code == "write_gate_changed") { throw new AccessDeniedException(409, error.Code); }
        catch { throw new AccessDeniedException(503, "order_dependency_unavailable"); }
    }
}
