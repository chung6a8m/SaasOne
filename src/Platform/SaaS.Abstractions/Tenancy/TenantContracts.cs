namespace SaaS.Abstractions.Tenancy;

public enum SubscriptionStatus { Active = 1, Suspended = 2, PendingDeletion = 3 }
public enum OperationStatus { None = 0, Provisioning = 1, Failed = 2, PlanChanging = 3, Migrating = 4, Restoring = 5, SecurityRevalidation = 6, Purging = 7 }

public sealed record TenantContext(Guid TenantId, string ShellName, string UrlPrefix);
public sealed record TenantStatus(Guid TenantId, SubscriptionStatus Subscription, DateTimeOffset ExpiresAt,
    OperationStatus Operation, long RegistryVersion, Guid? OperationId, Guid? OwnerId, long Fence,
    bool SecurityRevalidationRequired = false);
public sealed record OperationStamp(Guid OperationId, Guid OwnerId, long Fence, long RegistryVersion);

/// <summary>Safe machine-readable error. It must never include connection strings or provider messages.</summary>
public sealed class TenantContractException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
