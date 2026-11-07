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
    public static void Require(PermissionCatalog catalog, OrderAccessContext access, OrderAction action, OrderView? order) => throw new NotImplementedException();
    public static void RequireRead(PermissionCatalog catalog, OrderAccessContext access, OrderView order) => throw new NotImplementedException();
}
