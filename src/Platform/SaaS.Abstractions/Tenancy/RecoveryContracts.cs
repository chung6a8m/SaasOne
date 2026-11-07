using System.Data.Common;
namespace SaaS.Abstractions.Tenancy;

/// <summary>Complete current decision from the external platform; never reconstructed from restored tenant roles or audit.</summary>
public sealed record RecoveryApproval(Guid TenantId, Guid OperationId, long Generation, long GateVersion, long Fence,
    string AclJson, string AclHash, string ApprovedBy, string Reason);
public sealed record RecoveryMarker(Guid TenantId, Guid OperationId, long Generation, string AclHash, long Revision);

/// <summary>Private host composition only. Recheck current external approval and memberships inside the apply boundary.</summary>
public interface IRecoveryAclApplier
{
    Task<RecoveryMarker> ApplyAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, CancellationToken cancellationToken);
    Task<RecoveryMarker> VerifyAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, CancellationToken cancellationToken);
}
