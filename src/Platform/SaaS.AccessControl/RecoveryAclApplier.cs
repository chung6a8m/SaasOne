using System.Data.Common;
using SaaS.Abstractions.Tenancy;
namespace SaaS.AccessControl;

public sealed class RecoveryAclApplier : IRecoveryAclApplier
{
    public RecoveryAclApplier(PermissionCatalog catalog,
        Func<Guid, IReadOnlyCollection<Guid>, CancellationToken, Task> currentMemberships, TimeProvider? clock = null) { }
    public Task<RecoveryMarker> ApplyAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<RecoveryMarker> VerifyAsync(DbConnection connection, RecoveryApproval approval,
        Func<RecoveryApproval, CancellationToken, Task> demandCurrentApproval, CancellationToken cancellationToken) => throw new NotImplementedException();
}
