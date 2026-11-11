using System.Diagnostics;

namespace SaaS.Tenancy.Operations;

// No dynamic tags or identifiers. When no listener is installed, StartActivity returns null.
// Observers MUST NOT be attached in production or export SQL/identity payloads.
public enum SaaSStage
{
    Routing, IdentityFresh, IdentityNative, AclAuthorize, AclSnapshot,
    OrdersRead, OrdersWrite, TenantTransaction,
    OrdersLockCommand, OrdersLockResource, OrdersLockPage, AclLock, WriteGateLock, OtherLock
}

public static class SaaSStageActivity
{
    public const string SourceName = "SaaS.RequestStages";
    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(SaaSStage stage) => Source.StartActivity(stage switch
    {
        SaaSStage.Routing => "routing",
        SaaSStage.IdentityFresh => "identity.fresh",
        SaaSStage.IdentityNative => "identity.native",
        SaaSStage.AclAuthorize => "acl.authorize",
        SaaSStage.AclSnapshot => "acl.snapshot",
        SaaSStage.OrdersRead => "orders.read",
        SaaSStage.OrdersWrite => "orders.write",
        SaaSStage.TenantTransaction => "tenant.transaction",
        SaaSStage.OrdersLockCommand => "orders.lock.command",
        SaaSStage.OrdersLockResource => "orders.lock.resource",
        SaaSStage.OrdersLockPage => "orders.lock.page",
        SaaSStage.AclLock => "acl.lock",
        SaaSStage.WriteGateLock => "writegate.lock",
        _ => "other.lock"
    });

    // Only called after a SQL transaction-owned application lock is acquired successfully.
    public static void Acquired(Activity? activity) => activity?.AddEvent(new ActivityEvent("acquired"));
}
