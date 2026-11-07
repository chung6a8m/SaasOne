using SaaS.AccessControl;
namespace SaaS.Orders;

public static class OrderPermissions
{
    public static PermissionDefinition[] Definitions =>
    [
        new("Orders.Create", "Orders"),
        new("Orders.ViewOwn", "Orders", ResourceRule.Owner), new("Orders.ViewOthers", "Orders", ResourceRule.Any),
        new("Orders.EditOwn", "Orders", ResourceRule.Owner), new("Orders.EditOthers", "Orders", ResourceRule.Any),
        new("Orders.SubmitOwn", "Orders", ResourceRule.Owner), new("Orders.SubmitOthers", "Orders", ResourceRule.Any),
        new("OrderApprovals.Decide", "Approval", ResourceRule.Any)
    ];
    /// <summary>Same policy for a new mutation and a stored-result replay. No state/token preconditions here.</summary>
    public static void Require(PermissionCatalog catalog, OrderAccessContext access, OrderAction action, OrderView? order)
    {
        var evaluator = new PermissionEvaluator(catalog);
        if (action == OrderAction.Create)
        {
            _ = evaluator.Require(access.Current, access.Snapshot, "Orders.Create");
            if (order is not null && order.CreatedBy != access.Current.UserId) throw new AccessDeniedException(403, "permission_denied");
            _ = evaluator.Require(access.Current, access.Snapshot, "Orders.ViewOwn",
                new(access.Current.TenantId, access.Current.UserId, order?.Id.ToString("D") ?? "new"));
            return;
        }
        if (order is null) throw new AccessDeniedException(404, "order_not_found");
        RequireRead(catalog, access, order);
        var own = order.CreatedBy == access.Current.UserId;
        var resource = new ResourceDescriptor(access.Current.TenantId, order.CreatedBy, order.Id.ToString("D"));
        var permission = action switch
        {
            OrderAction.Edit => own ? "Orders.EditOwn" : "Orders.EditOthers",
            OrderAction.Submit => own ? "Orders.SubmitOwn" : "Orders.SubmitOthers",
            OrderAction.Approve or OrderAction.Reject => "OrderApprovals.Decide",
            _ => throw new AccessDeniedException(400, "order_command_invalid")
        };
        if (action is OrderAction.Approve or OrderAction.Reject && own) throw new AccessDeniedException(403, "self_approval_forbidden");
        _ = evaluator.Require(access.Current, access.Snapshot, permission, resource);
    }
    public static void RequireRead(PermissionCatalog catalog, OrderAccessContext access, OrderView order) =>
        _ = new PermissionEvaluator(catalog).Require(access.Current, access.Snapshot,
            order.CreatedBy == access.Current.UserId ? "Orders.ViewOwn" : "Orders.ViewOthers",
            new(access.Current.TenantId, order.CreatedBy, order.Id.ToString("D")));
}
