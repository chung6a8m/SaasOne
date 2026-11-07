namespace SaaS.Orders;

// Contract-first skeleton for the red M4 policy run. Not a functional implementation.
public static class OrderPolicy
{
    public static OrderCommand Normalize(OrderCommand command) => throw new NotImplementedException();
    public static string Fingerprint(OrderCommand command) => throw new NotImplementedException();
    public static string Partition(Guid tenant, Guid actor, string client, OrderAction action, string key) => throw new NotImplementedException();
    public static OrderTransition Next(OrderView current, OrderCommand command, bool approvalEntitled, bool handlerReady) => throw new NotImplementedException();
}
