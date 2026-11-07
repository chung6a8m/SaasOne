using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

public enum ResourceCheckpoint { Claimed, GateClosed, ObligationsChecked, SchemaReady, FeaturesReady, EntitlementsCommitted, AclApplied, GatePrepared, Completed }
public sealed record ResourceResult(Guid OperationId, Guid TenantId, string Plan, long RegistryVersion, ResourceCheckpoint Checkpoint);

/// <summary>Private SQL-authenticated resource operations; never registered in customer HTTP DI.</summary>
public sealed class TenantOperations
{
    public TenantOperations(TenancyConnections connections, IPlanReconciler reconciler, IApprovalObligationReader obligations,
        TimeProvider? clock = null, Func<ResourceCheckpoint, CancellationToken, Task>? progress = null, int drainTimeoutMilliseconds = 30000) { }
    public Task<ResourceResult> ChangePlanAsync(Guid tenant, Guid operation, long expectedVersion, string target, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<ResourceResult> ResumeAsync(Guid tenant, Guid operation, long expectedVersion, CancellationToken ct)
        => throw new NotImplementedException();
}
