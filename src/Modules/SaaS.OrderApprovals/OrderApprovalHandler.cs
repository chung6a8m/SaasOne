using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
namespace SaaS.OrderApprovals;

/// <summary>No independent connection, commit, retry, audit or dedup record. Orders owns the command.</summary>
public sealed class OrderApprovalHandler(TimeProvider? clock = null) : IOrderApprovalHandler
{
    public async Task DemandReadyAsync(TenantTransaction transaction, CancellationToken ct)
    {
        await using var db = ApprovalsDbContext.Open(transaction.Connection);
        await db.Database.UseTransactionAsync(transaction.Transaction, ct);
        await TenancyConnections.VerifySchemaAsync(db, ct);
        await ApprovalHistoryVerifier.DemandColumnsAsync(transaction.Connection, transaction.Transaction, ct);
    }
    public async Task SubmittedAsync(OrderApprovalSubmission value, TenantTransaction transaction, CancellationToken ct)
    {
        await using var db = ApprovalsDbContext.Open(transaction.Connection);
        await db.Database.UseTransactionAsync(transaction.Transaction, ct);
        await using var orders = OrdersDbContext.Open(transaction.Connection);
        await orders.Database.UseTransactionAsync(transaction.Transaction, ct);
        var current = await orders.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == value.OrderId, ct);
        if (current is null || current.CreatedBy != value.CreatedBy || value.ActorId == Guid.Empty || !current.ApprovalRequired
            || current.Status != OrderStatus.PendingApproval || current.Revision != value.SubmittedRevision
            || current.View().Version != value.Version || value.SubmittedRevision < 2)
            throw new AccessDeniedException(409, "approval_revision_changed");
        if (await db.Requests.AnyAsync(x => x.OrderId == value.OrderId && !db.Decisions.Any(d => d.OrderId == x.OrderId && d.SubmittedRevision == x.SubmittedRevision), ct))
            throw new AccessDeniedException(409, "approval_request_outstanding");
        db.Requests.Add(new() { OrderId = value.OrderId, SubmittedRevision = value.SubmittedRevision,
            SubmittedVersion = value.Version, CreatedBy = value.CreatedBy, SubmittedBy = value.ActorId,
            SubmittedAt = (clock ?? TimeProvider.System).GetUtcNow() });
        await db.SaveChangesAsync(ct);
    }
    public async Task DecidedAsync(OrderApprovalDecision value, TenantTransaction transaction, CancellationToken ct)
    {
        await using var db = ApprovalsDbContext.Open(transaction.Connection);
        await db.Database.UseTransactionAsync(transaction.Transaction, ct);
        var request = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == value.OrderId && x.SubmittedRevision == value.SubmittedRevision, ct);
        if (request is null || request.SubmittedVersion != value.SubmittedVersion || value.SubmittedRevision == long.MaxValue
            || value.ResultRevision != value.SubmittedRevision + 1 || value.ActorId == Guid.Empty
            || await db.Decisions.AnyAsync(x => x.OrderId == value.OrderId && x.SubmittedRevision == value.SubmittedRevision, ct))
            throw new AccessDeniedException(409, "approval_revision_changed");
        if (request.CreatedBy == value.ActorId) throw new AccessDeniedException(403, "self_approval_forbidden");
        // Defense in depth for trusted callers of the extension, using the same normalization as the command owner.
        var normalized = OrderPolicy.Normalize(new(value.Approved ? OrderAction.Approve : OrderAction.Reject,
            "approval-contract", value.OrderId, value.SubmittedRevision, value.SubmittedVersion, Reason: value.Reason));
        await using var orders = OrdersDbContext.Open(transaction.Connection);
        await orders.Database.UseTransactionAsync(transaction.Transaction, ct);
        var current = await orders.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == value.OrderId, ct);
        if (current is null || current.CreatedBy != request.CreatedBy || !current.ApprovalRequired || current.Revision != value.ResultRevision
            || current.Status != (value.Approved ? OrderStatus.Confirmed : OrderStatus.Rejected))
            throw new AccessDeniedException(409, "approval_revision_changed");
        db.Decisions.Add(new() { OrderId = value.OrderId, SubmittedRevision = value.SubmittedRevision, ResultRevision = value.ResultRevision,
            DecidedBy = value.ActorId, Approved = value.Approved, Reason = normalized.Reason, DecidedAt = (clock ?? TimeProvider.System).GetUtcNow() });
        await db.SaveChangesAsync(ct);
    }
}
