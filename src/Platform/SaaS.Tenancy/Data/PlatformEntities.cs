using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Data;

public sealed class TenantRegistration
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public Guid DatabaseNonce { get; set; }
    public string RuntimeSecret { get; set; } = string.Empty;
    public string MigratorSecret { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public SubscriptionStatus Subscription { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public OperationStatus Operation { get; set; }
    public long RegistryVersion { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? OwnerId { get; set; }
    public long Fence { get; set; }
    public bool SecurityRevalidationRequired { get; set; }
    public TenantStatus Status() => new(Id, Subscription, ExpiresAt, Operation, RegistryVersion, OperationId, OwnerId, Fence, SecurityRevalidationRequired);
}

public enum ProvisioningCheckpoint { Registered = 0, DatabaseReady = 1, OrchardReady = 2, RuntimeVerified = 3, Completed = 4 }
public sealed class ProvisioningOperation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public ProvisioningCheckpoint Checkpoint { get; set; }
    public Guid? OwnerId { get; set; }
    public long Fence { get; set; }
    public bool Failed { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class OperationalAudit
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid OperationId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public long RegistryVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class PlatformBootstrap
{
    public int Id { get; set; }
    public Guid InstallationId { get; set; }
    public bool Completed { get; set; }
    public int BaselineVersion { get; set; }
}
