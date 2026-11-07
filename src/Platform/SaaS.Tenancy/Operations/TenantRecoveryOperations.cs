using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Operations;

public sealed record RecoveryDecision(string AclJson, string AclHash, string Reason, bool AllowApprovalUpgrade = false);

public sealed partial class TenantOperations
{
    public Task<ResourceResult> BeginRecoveryAsync(Guid tenantId, Guid operationId, long expectedVersion, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<ResourceResult> ApproveRecoveryAsync(Guid tenantId, Guid operationId, long expectedVersion, RecoveryDecision decision, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<ResourceResult> ResumeRecoveryAsync(Guid tenantId, Guid operationId, long expectedVersion, IRecoveryAclApplier applier, CancellationToken ct)
        => throw new NotImplementedException();
}
