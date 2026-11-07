using SaaS.AccessControl;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M3AuthorizationTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _sales = Guid.NewGuid();
    private static PermissionCatalog Catalog() => new([
        new("Access.Manage", "Core", Delegable: false),
        new("Probe.Own", "Core", ResourceRule.Owner),
        new("Probe.Any", "Core", ResourceRule.Any),
        new("Probe.Approve", "Approval", ResourceRule.Any)]);
    private CurrentAccess Actor() => new(_tenant, _user, 4, 10, new HashSet<string>(["Core"], StringComparer.Ordinal));
    private AclDocument Document() => new([
        new(_manager, "Manager", [new("Access.Manage", PermissionEffect.Allow), new("Probe.Own", PermissionEffect.Allow)]),
        new(_sales, "Sales", [new("Probe.Own", PermissionEffect.Allow)])],
        [new(_user, _manager), new(_other, _sales)], []);
    private AccessSnapshot Snapshot(AclDocument? document = null) => new(_tenant, 4, 1, document ?? Document());

    [Fact]
    public void Owner_rule_and_tenant_binding_apply_even_when_permission_is_allowed()
    {
        var evaluator = new PermissionEvaluator(Catalog());
        Assert.Equal(_user, evaluator.Require(Actor(), Snapshot(), "Probe.Own", new(_tenant, _user, "1")).UserId);
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot(), "Probe.Own", new(_tenant, _other, "1")));
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot(), "Probe.Own", new(Guid.NewGuid(), _user, "1")));
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot(), "Probe.Own"));
    }
    [Fact]
    public void User_override_is_fresh_and_deny_wins_existing_roles()
    {
        var evaluator = new PermissionEvaluator(Catalog());
        var resource = new ResourceDescriptor(_tenant, _user, "1");
        evaluator.Require(Actor(), Snapshot(), "Probe.Own", resource);
        var changed = Document() with { Overrides = [new(_user, "Probe.Own", PermissionEffect.Deny)] };
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot(changed), "Probe.Own", resource));
    }
    [Fact]
    public void Fresh_credentials_do_not_make_an_old_restored_acl_authoritative()
    {
        var error = Assert.Throws<AccessDeniedException>(() => new PermissionEvaluator(Catalog()).Require(Actor() with { SecurityGeneration = 5 }, Snapshot(), "Access.Manage"));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("access_revalidation_required", error.Code);
    }
    [Fact]
    public void Entitlement_unknown_permission_and_cross_tenant_snapshot_fail_closed()
    {
        var evaluator = new PermissionEvaluator(Catalog());
        var allow = Document() with { Overrides = [new(_user, "Probe.Approve", PermissionEffect.Allow)] };
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot(allow), "Probe.Approve", new(_tenant, _user, "1")));
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot(), "OrchardCore.Features.Manage"));
        Assert.Throws<AccessDeniedException>(() => evaluator.Require(Actor(), Snapshot() with { TenantId = Guid.NewGuid() }, "Access.Manage"));
    }
    [Fact]
    public void Catalog_rejects_unknown_duplicate_and_invalid_effects_in_an_acl()
    {
        var doc = Document();
        Catalog().Validate(doc);
        Assert.Throws<AccessDeniedException>(() => Catalog().Validate(doc with { Overrides = [new(_other, "Administrator", PermissionEffect.Allow)] }));
        Assert.Throws<AccessDeniedException>(() => Catalog().Validate(doc with { Assignments = [new(_other, _sales), new(_other, _sales)] }));
        Assert.Throws<AccessDeniedException>(() => Catalog().Validate(doc with { Overrides = [new(_other, "Probe.Own", (PermissionEffect)12)] }));
        Assert.Throws<AccessDeniedException>(() => Catalog().Validate(doc with { Assignments = [new(_other, Guid.NewGuid())] }));
    }
    [Fact]
    public void Administration_can_edit_another_users_delegable_right()
    {
        var proposed = Document() with { Overrides = [new(_other, "Probe.Own", PermissionEffect.Deny)] };
        new AclAdministrationGuard(Catalog()).Demand(Actor(), Snapshot(), proposed);
    }
    [Fact]
    public void Administration_cannot_edit_self_directly_or_through_a_shared_role()
    {
        var guard = new AclAdministrationGuard(Catalog());
        Assert.Throws<AccessDeniedException>(() => guard.Demand(Actor(), Snapshot(), Document() with { Overrides = [new(_user, "Probe.Own", PermissionEffect.Allow)] }));
        Assert.Throws<AccessDeniedException>(() => guard.Demand(Actor(), Snapshot(), Document() with { Assignments = [new(_user, _manager), new(_user, _sales), new(_other, _sales)] }));
        var roles = Document().Roles.Select(x => x.Id == _manager ? x with { Permissions = [new("Access.Manage", PermissionEffect.Allow), new("Probe.Any", PermissionEffect.Allow)] } : x).ToArray();
        Assert.Throws<AccessDeniedException>(() => guard.Demand(Actor(), Snapshot(), Document() with { Roles = roles }));
    }
    [Fact]
    public void Removing_a_deny_or_assigning_a_reserved_role_is_a_grant_not_a_loophole()
    {
        var guard = new AclAdministrationGuard(Catalog());
        var old = Document() with { Overrides = [new(_other, "Probe.Any", PermissionEffect.Deny)] };
        Assert.Throws<AccessDeniedException>(() => guard.Demand(Actor(), Snapshot(old), Document()));
        Assert.Throws<AccessDeniedException>(() => guard.Demand(Actor(), Snapshot(), Document() with { Assignments = [new(_user, _manager), new(_other, _manager)] }));
    }
    [Fact]
    public void Canonical_acl_fingerprint_is_order_independent_but_includes_effect()
    {
        var doc = Document();
        Assert.Equal(doc.Hash(), (doc with { Roles = doc.Roles.Reverse().ToArray(), Assignments = doc.Assignments.Reverse().ToArray() }).Hash());
        Assert.NotEqual(doc.Hash(), (doc with { Overrides = [new(_other, "Probe.Own", PermissionEffect.Deny)] }).Hash());
    }
}
