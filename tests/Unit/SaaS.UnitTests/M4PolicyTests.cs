using SaaS.AccessControl;
using SaaS.Orders;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M4PolicyTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Actor = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static OrderDraft Draft => new("SO-001", "Customer", 12.50m, "USD", "text");
    internal static OrderView View(OrderStatus status = OrderStatus.Draft, bool required = false, long revision = 1) =>
        new(Guid.NewGuid(), Actor, "SO-001", "Customer", 12.50m, "USD", "text", status, revision,
            Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }), required, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    internal static OrderCommand Command(OrderView view, OrderAction action = OrderAction.Submit, string key = "key-0001") =>
        new(action, key, view.Id, view.Revision, view.Version, action == OrderAction.Edit ? Draft : null, action == OrderAction.Reject ? "Incorrect amount" : null);
    private static void Denied(int status, Action action) => Assert.Equal(status, Assert.Throws<AccessDeniedException>(action).StatusCode);

    [Fact]
    public void Normalization_is_explicit_and_fingerprint_is_independent_of_equivalent_decimal_scale()
    {
        var a = new OrderCommand(OrderAction.Create, "key-0001", Draft: new(" so-001 ", " Cafe\u0301 ", 1.0m, " usd ", " a\r\nb "));
        var b = a with { Draft = new("SO-001", "Caf\u00e9", 1.00m, "USD", "a\nb") };
        Assert.Equal(b, OrderPolicy.Normalize(a));
        Assert.Equal(OrderPolicy.Fingerprint(a), OrderPolicy.Fingerprint(b));
    }
    [Fact]
    public void Fingerprint_includes_original_payload_revision_token_resource_and_action()
    {
        var view = View(); var command = Command(view, OrderAction.Edit);
        var hash = OrderPolicy.Fingerprint(command);
        Assert.NotEqual(hash, OrderPolicy.Fingerprint(command with { Draft = Draft with { Amount = 99 } }));
        Assert.NotEqual(hash, OrderPolicy.Fingerprint(command with { ExpectedRevision = 2 }));
        Assert.NotEqual(hash, OrderPolicy.Fingerprint(command with { ExpectedVersion = Convert.ToBase64String(new byte[8]) }));
        Assert.NotEqual(hash, OrderPolicy.Fingerprint(command with { OrderId = Guid.NewGuid() }));
        Assert.NotEqual(hash, OrderPolicy.Fingerprint(Command(view)));
    }
    [Fact]
    public void Key_partitions_are_case_sensitive_and_include_tenant_actor_client_and_action()
    {
        var key = OrderPolicy.Partition(Tenant, Actor, "client-a", OrderAction.Create, "key-0001");
        Assert.Equal(64, key.Length);
        Assert.NotEqual(key, OrderPolicy.Partition(Guid.NewGuid(), Actor, "client-a", OrderAction.Create, "key-0001"));
        Assert.NotEqual(key, OrderPolicy.Partition(Tenant, Guid.NewGuid(), "client-a", OrderAction.Create, "key-0001"));
        Assert.NotEqual(key, OrderPolicy.Partition(Tenant, Actor, "client-b", OrderAction.Create, "key-0001"));
        Assert.NotEqual(key, OrderPolicy.Partition(Tenant, Actor, "client-a", OrderAction.Edit, "key-0001"));
        Assert.NotEqual(key, OrderPolicy.Partition(Tenant, Actor, "client-a", OrderAction.Create, "KEY-0001"));
    }
    [Fact]
    public void Invalid_keys_and_server_owned_create_identity_are_rejected()
    {
        foreach (var key in new[] { "", "short", "key with space", "key-\u03b1-0001", new string('x', 129) })
            Denied(400, () => OrderPolicy.Normalize(new(OrderAction.Create, key, Draft: Draft)));
        Denied(400, () => OrderPolicy.Normalize(new(OrderAction.Create, "key-0001", Guid.NewGuid(), Draft: Draft)));
        Denied(400, () => OrderPolicy.Normalize(new(OrderAction.Create, "key-0001", ExpectedRevision: 1, Draft: Draft)));
    }
    [Fact]
    public void Missing_and_overposted_command_shapes_are_rejected()
    {
        var view = View();
        Denied(400, () => OrderPolicy.Normalize(Command(view) with { ExpectedRevision = null }));
        Denied(400, () => OrderPolicy.Normalize(Command(view) with { ExpectedVersion = "invalid" }));
        Denied(400, () => OrderPolicy.Normalize(Command(view) with { Draft = Draft }));
        Denied(400, () => OrderPolicy.Normalize(Command(view, OrderAction.Edit) with { Draft = null }));
        Denied(400, () => OrderPolicy.Normalize(Command(view, OrderAction.Reject) with { Reason = " " }));
        Denied(400, () => OrderPolicy.Normalize(Command(view) with { Reason = "not allowed" }));
        Denied(400, () => OrderPolicy.Normalize(Command(view) with { Action = (OrderAction)99 }));
    }
    [Fact]
    public void Draft_numeric_precision_text_bounds_and_control_characters_are_validated()
    {
        foreach (var draft in new[] { Draft with { Amount = -1 }, Draft with { Amount = 0.001m },
            Draft with { Amount = 10000000000000000m }, Draft with { Number = "<script>" },
            Draft with { Number = new string('A', 65) }, Draft with { CustomerName = "\u0000" },
            Draft with { CustomerName = new string('a', 161) }, Draft with { Currency = "US" },
            Draft with { Notes = new string('a', 2001) } })
            Denied(400, () => OrderPolicy.Normalize(new(OrderAction.Create, "key-0001", Draft: draft)));
    }
    [Fact]
    public void Standard_submit_confirms_only_an_order_without_a_persisted_obligation()
    {
        var view = View();
        Assert.Equal(new(OrderStatus.Confirmed, 2, false), OrderPolicy.Next(view, Command(view), false, false));
    }
    [Fact]
    public void Approval_submit_sets_the_obligation_on_the_new_pending_revision()
    {
        var view = View();
        Assert.Equal(new(OrderStatus.PendingApproval, 2, true), OrderPolicy.Next(view, Command(view), true, true));
    }
    [Fact]
    public void Missing_handler_cannot_fall_back_to_standard_confirmation()
    {
        var view = View();
        Denied(503, () => OrderPolicy.Next(view, Command(view), true, false));
    }
    [Fact]
    public void A_persisted_obligation_requires_current_approval_entitlement_even_after_downgrade()
    {
        var view = View(required: true);
        Denied(403, () => OrderPolicy.Next(view, Command(view), false, true));
        Denied(503, () => OrderPolicy.Next(view, Command(view), true, false));
    }
    [Fact]
    public void Rejected_edit_returns_draft_but_never_erases_the_obligation()
    {
        var view = View(OrderStatus.Rejected, true, 3);
        Assert.Equal(new(OrderStatus.Draft, 4, true), OrderPolicy.Next(view, Command(view, OrderAction.Edit), false, false));
    }
    [Fact]
    public void Pending_and_confirmed_orders_cannot_be_edited_or_submitted()
    {
        foreach (var status in new[] { OrderStatus.PendingApproval, OrderStatus.Confirmed })
        {
            var view = View(status, true, 2);
            Denied(409, () => OrderPolicy.Next(view, Command(view, OrderAction.Edit), true, true));
            Denied(409, () => OrderPolicy.Next(view, Command(view), true, true));
        }
    }
    [Fact]
    public void Approve_and_reject_are_revision_bound_and_preserve_the_marker()
    {
        var view = View(OrderStatus.PendingApproval, true, 2);
        Assert.Equal(new(OrderStatus.Confirmed, 3, true), OrderPolicy.Next(view, Command(view, OrderAction.Approve), true, true));
        Assert.Equal(new(OrderStatus.Rejected, 3, true), OrderPolicy.Next(view, Command(view, OrderAction.Reject), true, true));
        Denied(409, () => OrderPolicy.Next(view, Command(view, OrderAction.Approve) with { ExpectedRevision = 1 }, true, true));
        Denied(409, () => OrderPolicy.Next(view, Command(view, OrderAction.Reject) with { ExpectedVersion = Convert.ToBase64String(new byte[8]) }, true, true));
    }
    [Fact]
    public void Rejected_requires_edit_before_resubmit_and_unknown_states_fail_closed()
    {
        var view = View(OrderStatus.Rejected, true, 3);
        Denied(409, () => OrderPolicy.Next(view, Command(view), true, true));
        var corrupt = View((OrderStatus)99, true);
        Denied(503, () => OrderPolicy.Next(corrupt, Command(corrupt), true, true));
        corrupt = View(OrderStatus.PendingApproval, false);
        Denied(503, () => OrderPolicy.Next(corrupt, Command(corrupt, OrderAction.Approve), true, true));
    }
    [Fact]
    public void Resource_Deny_is_not_bypassed_by_an_alternate_own_or_others_permission()
    {
        var access = Access(Actor, [new(Actor, "Orders.EditOwn", PermissionEffect.Deny)]);
        Denied(403, () => OrderPermissions.Require(Catalog, access, OrderAction.Edit, View()));
    }
    [Fact]
    public void Mutations_and_replays_need_the_corresponding_current_read_permission()
    {
        var access = Access(Actor, [new(Actor, "Orders.ViewOwn", PermissionEffect.Deny)]);
        foreach (var action in new[] { OrderAction.Create, OrderAction.Edit, OrderAction.Submit })
            Denied(403, () => OrderPermissions.Require(Catalog, access, action, View()));
    }
    [Fact]
    public void Another_owners_order_cannot_use_own_permission_as_a_fallback()
    {
        var access = Access(Actor, [new(Actor, "Orders.ViewOthers", PermissionEffect.Deny)]);
        Denied(403, () => OrderPermissions.RequireRead(Catalog, access, View() with { CreatedBy = Guid.NewGuid() }));
    }
    [Fact]
    public void Creator_cannot_approve_or_reject_even_with_a_Decide_allow()
    {
        var access = Access(Actor);
        foreach (var action in new[] { OrderAction.Approve, OrderAction.Reject })
            Denied(403, () => OrderPermissions.Require(Catalog, access, action, View(OrderStatus.PendingApproval, true)));
    }
    [Fact]
    public void Replay_policy_does_not_reapply_execution_state_or_revision_preconditions()
    {
        var access = Access(Actor);
        OrderPermissions.Require(Catalog, access, OrderAction.Submit, View(OrderStatus.Confirmed, false, 9));
        OrderPermissions.Require(Catalog, access, OrderAction.Edit, View(OrderStatus.PendingApproval, true, 8));
    }
    [Fact]
    public void Revoked_entitlement_or_security_generation_denies_replay_policy()
    {
        var access = Access(Actor);
        Denied(403, () => OrderPermissions.Require(Catalog, access with { Current = access.Current with { Entitlements = new HashSet<string>() } }, OrderAction.Submit, View()));
        Denied(503, () => OrderPermissions.Require(Catalog, access with { Current = access.Current with { SecurityGeneration = 2 } }, OrderAction.Submit, View()));
    }
    internal static PermissionCatalog Catalog => new(OrderPermissions.Definitions);
    internal static OrderAccessContext Access(Guid user, UserOverride[]? overrides = null, Guid? tenant = null)
    {
        var role = Guid.Parse("33333333-3333-3333-3333-333333333333");
        return new(new(tenant ?? Tenant, user, 1, 1, new HashSet<string> { "Orders", "Approval" }),
            new(tenant ?? Tenant, 1, 1, new([new(role, "Operator", OrderPermissions.Definitions.Select(x => new PermissionGrant(x.Code, PermissionEffect.Allow)).ToArray())], [new(user, role)], overrides ?? [])), "test-client");
    }
}
