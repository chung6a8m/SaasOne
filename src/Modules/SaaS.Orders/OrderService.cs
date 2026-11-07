using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
namespace SaaS.Orders;

// Contract-first SQL regression skeleton. Replaced only after observing the red runtime suite.
public sealed class OrderService
{
    public OrderService(string connection, Guid tenantId, PermissionCatalog catalog,
        Func<TenantTransaction, CancellationToken, Task<OrderAccessContext>> currentAccess,
        IOrderApprovalHandler? approvalHandler = null, TimeProvider? clock = null)
    { _ = connection; _ = tenantId; _ = catalog; _ = currentAccess; _ = approvalHandler; _ = clock; }
    public Task<OrderCommandResult> ExecuteAsync(OrderCommand command, CancellationToken ct) => throw new NotImplementedException();
    public Task<OrderView> GetAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    public Task<OrderPage> ListAsync(int page, CancellationToken ct) => throw new NotImplementedException();
}
