using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
namespace SaaS.Orders;

public enum OrderStatus { Draft = 1, PendingApproval = 2, Rejected = 3, Confirmed = 4 }
public enum OrderAction { Create = 1, Edit = 2, Submit = 3, Approve = 4, Reject = 5 }
public sealed record OrderDraft(string Number, string CustomerName, decimal Amount, string Currency, string Notes);
public sealed record OrderCommand(OrderAction Action, string Key, Guid? OrderId = null, long? ExpectedRevision = null,
    string? ExpectedVersion = null, OrderDraft? Draft = null, string? Reason = null);
public sealed record OrderView(Guid Id, Guid CreatedBy, string Number, string CustomerName, decimal Amount, string Currency,
    string Notes, OrderStatus Status, long Revision, string Version, bool ApprovalRequired,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record OrderTransition(OrderStatus Status, long Revision, bool ApprovalRequired);
public sealed record OrderCommandResult(OrderView Order, bool Replayed, DateTimeOffset ReplayUntil);
public sealed record OrderPage(OrderView[] Items, bool HasMore);
public sealed record OrderAccessContext(CurrentAccess Current, AccessSnapshot Snapshot, string ClientId);
public sealed record OrderApprovalSubmission(Guid OrderId, Guid CreatedBy, long SubmittedRevision, string Version, Guid ActorId);
public sealed record OrderApprovalDecision(Guid OrderId, long SubmittedRevision, string SubmittedVersion,
    long ResultRevision, Guid ActorId, bool Approved, string? Reason);

/// <summary>
/// An awaited, in-process extension of the caller-owned transaction. Implementations must enlist
/// every context on this connection and transaction; they may not commit, independently retry,
/// open another connection, or perform an irreversible external side effect. M4 registers none.
/// </summary>
public interface IOrderApprovalHandler
{
    Task DemandReadyAsync(TenantTransaction transaction, CancellationToken ct);
    Task SubmittedAsync(OrderApprovalSubmission submission, TenantTransaction transaction, CancellationToken ct);
    Task DecidedAsync(OrderApprovalDecision decision, TenantTransaction transaction, CancellationToken ct);
}
