namespace SaaS.OrderApprovals.Data;

/// <summary>The immutable submitted revision. No mutable status or client-supplied creator.</summary>
public sealed class ApprovalRequest
{
    public Guid OrderId { get; set; }
    public long SubmittedRevision { get; set; }
    public string SubmittedVersion { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public Guid SubmittedBy { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
}

/// <summary>Exactly one append-only decision per request, retained across rework and plan changes.</summary>
public sealed class ApprovalDecision
{
    public Guid OrderId { get; set; }
    public long SubmittedRevision { get; set; }
    public long ResultRevision { get; set; }
    public Guid DecidedBy { get; set; }
    public bool Approved { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}
