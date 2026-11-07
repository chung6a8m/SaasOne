namespace SaaS.Orders.Data;

public sealed class OrderRow
{
    public Guid Id { get; set; }
    public Guid CreatedBy { get; set; }
    public string Number { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Notes { get; set; } = "";
    public OrderStatus Status { get; set; }
    public long Revision { get; set; }
    public byte[] Version { get; set; } = [];
    public bool ApprovalRequired { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public OrderView View() => new(Id, CreatedBy, Number, CustomerName, Amount, Currency, Notes,
        Status, Revision, Convert.ToBase64String(Version), ApprovalRequired, CreatedAt, UpdatedAt);
    public void Apply(OrderDraft draft)
    { Number = draft.Number; CustomerName = draft.CustomerName; Amount = draft.Amount; Currency = draft.Currency; Notes = draft.Notes; }
}

public sealed class OrderRevision
{
    public Guid OrderId { get; set; }
    public long Revision { get; set; }
    public Guid ActorId { get; set; }
    public OrderAction Action { get; set; }
    public OrderStatus Status { get; set; }
    public bool ApprovalRequired { get; set; }
    public string SnapshotJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class OrderReceipt
{
    public string PartitionHash { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string ClientId { get; set; } = "";
    public OrderAction Action { get; set; }
    public string RequestHash { get; set; } = "";
    public Guid OrderId { get; set; }
    public long ResultRevision { get; set; }
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
