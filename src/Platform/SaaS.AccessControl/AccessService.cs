using SaaS.Abstractions.Tenancy;
namespace SaaS.AccessControl;

public sealed record AclView(AccessSnapshot Snapshot, long GateVersion);
public sealed record AclChange(Guid OperationId, long ExpectedRevision, long GateVersion, AclDocument Document);
public sealed class AccessService
{
    public AccessService(string connection, Guid routedTenantId, PermissionCatalog catalog,
        Func<CancellationToken, Task<CurrentAccess>> currentAuthority,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task> currentMemberships, TimeProvider? clock = null) { }
    public Task<AclView> ReadAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task<AccessTicket> RequireAsync(string permission, ResourceDescriptor? resource, CancellationToken ct) => throw new NotImplementedException();
    public Task<long> ChangeAsync(AclChange request, CancellationToken ct) => throw new NotImplementedException();
}
