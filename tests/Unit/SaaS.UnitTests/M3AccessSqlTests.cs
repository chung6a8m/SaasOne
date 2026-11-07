using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM3")]
public sealed class M3AccessSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly PermissionCatalog Catalog = new([
        new("Access.Manage", "AccessControl", Delegable: false), new("Sample.Read", "AccessControl"), new("Sample.Edit", "AccessControl")]);

    [Fact]
    public async Task Current_role_and_user_deny_changes_are_observed_without_new_credentials()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = fixture.Service(fixture.Other);
        await Assert.ThrowsAsync<AccessDeniedException>(() => user.RequireAsync("Sample.Read", null, Ct));
        var changed = fixture.Document with { Overrides = [new(fixture.Other, "Sample.Read", PermissionEffect.Allow)] };
        Assert.Equal(2, await fixture.Service(fixture.Manager).ChangeAsync(new(Guid.NewGuid(), 1, 1, changed), Ct));
        Assert.Equal(fixture.Other, (await user.RequireAsync("Sample.Read", null, Ct)).UserId);
        changed = changed with { Overrides = [new(fixture.Other, "Sample.Read", PermissionEffect.Deny)] };
        Assert.Equal(3, await fixture.Service(fixture.Manager).ChangeAsync(new(Guid.NewGuid(), 2, 1, changed), Ct));
        Assert.Equal(403, (await Assert.ThrowsAsync<AccessDeniedException>(() => user.RequireAsync("Sample.Read", null, Ct))).StatusCode);
    }

    [Fact]
    public async Task Administration_cannot_elevate_self_or_delegate_access_management()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service(fixture.Manager);
        var self = fixture.Document with { Overrides = [new(fixture.Manager, "Sample.Edit", PermissionEffect.Allow)] };
        Assert.Equal(403, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.ChangeAsync(new(Guid.NewGuid(), 1, 1, self), Ct))).StatusCode);
        var elevated = fixture.Document with { Assignments = [.. fixture.Document.Assignments, new(fixture.Other, fixture.Document.Roles[0].Id)] };
        Assert.Equal(403, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.ChangeAsync(new(Guid.NewGuid(), 1, 1, elevated), Ct))).StatusCode);
        Assert.Equal(1, (await service.ReadAsync(Ct)).Snapshot.Revision);
    }

    [Fact]
    public async Task Concurrent_acl_mutations_are_serialized_and_reauthorized_with_revision_CAS()
    {
        await using var fixture = await Fixture.CreateAsync();
        async Task<int> Attempt(string name)
        {
            var next = fixture.Document with { Roles = [..fixture.Document.Roles, new(Guid.NewGuid(), name, [new("Sample.Read", PermissionEffect.Allow)])] };
            try { await fixture.Service(fixture.Manager).ChangeAsync(new(Guid.NewGuid(), 1, 1, next), Ct); return 200; }
            catch (AccessDeniedException ex) { return ex.StatusCode; }
        }
        var outcomes = await Task.WhenAll(Attempt("One"), Attempt("Two"));
        Assert.Equal([200, 409], outcomes.Order().ToArray());
        Assert.Equal(2, (await fixture.Service(fixture.Manager).ReadAsync(Ct)).Snapshot.Revision);
        Assert.True(fixture.AuthorityReads >= 3);
        await using var audit = AuditDbContext.Open(fixture.Target.Connection);
        Assert.Single(await audit.Events.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Replay_requires_current_permission_and_same_actor_and_payload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service(fixture.Manager);
        var next = fixture.Document with { Overrides = [new(fixture.Other, "Sample.Read", PermissionEffect.Allow)] };
        var request = new AclChange(Guid.NewGuid(), 1, 1, next);
        Assert.Equal(2, await service.ChangeAsync(request, Ct));
        Assert.Equal(2, await service.ChangeAsync(request, Ct));
        Assert.Equal(409, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.ChangeAsync(request with { Document = fixture.Document }, Ct))).StatusCode);
        await using var db = AccessDbContext.Open(fixture.Target.Connection);
        db.UserGrants.Add(new UserGrantRow { UserId = fixture.Manager, Permission = "Access.Manage", Effect = PermissionEffect.Deny });
        var state = await db.State.SingleAsync(Ct);
        state.AclHash = (next with { Overrides = [..next.Overrides, new(fixture.Manager, "Access.Manage", PermissionEffect.Deny)] }).Hash();
        await db.SaveChangesAsync(Ct);
        Assert.Equal(403, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.ChangeAsync(request, Ct))).StatusCode);
        await using var audit = AuditDbContext.Open(fixture.Target.Connection);
        Assert.Single(await audit.Events.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_acl_marker_history_and_idempotent_result()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var sql = await SqlOperations.OpenAsync(fixture.Target.Connection, Ct))
            await SqlOperations.ExecuteAsync(sql, "ALTER TABLE audit.Events ADD CONSTRAINT CK_FixtureAuditFailure CHECK (Action <> 'AclChanged')", Ct);
        var next = fixture.Document with { Overrides = [new(fixture.Other, "Sample.Read", PermissionEffect.Allow)] };
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Service(fixture.Manager).ChangeAsync(new(Guid.NewGuid(), 1, 1, next), Ct))).StatusCode);
        var state = await fixture.Service(fixture.Manager).ReadAsync(Ct);
        Assert.Equal(1, state.Snapshot.Revision);
        Assert.Empty(state.Snapshot.Document.Overrides);
        await using var db = AccessDbContext.Open(fixture.Target.Connection);
        await using var audit = AuditDbContext.Open(fixture.Target.Connection);
        Assert.Empty(await db.History.ToArrayAsync(Ct));
        Assert.Empty(await audit.Events.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Required_source_errors_and_restored_acl_fail_closed_even_with_fresh_authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Generation = 2;
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Service(fixture.Manager).ReadAsync(Ct))).StatusCode);
        fixture.Generation = 1;
        fixture.MembersUnavailable = true;
        var next = fixture.Document with { Overrides = [new(fixture.Other, "Sample.Read", PermissionEffect.Allow)] };
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Service(fixture.Manager).ChangeAsync(new(Guid.NewGuid(), 1, 1, next), Ct))).StatusCode);
        fixture.MembersUnavailable = false;
        fixture.AuthorityUnavailable = true;
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Service(fixture.Manager).ReadAsync(Ct))).StatusCode);
        fixture.AuthorityUnavailable = false;
        await using (var sql = await SqlOperations.OpenAsync(fixture.Target.Connection, Ct))
            await SqlOperations.ExecuteAsync(sql, "DROP TABLE access.UserGrants", Ct);
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Service(fixture.Manager).ReadAsync(Ct))).StatusCode);
    }

    private sealed class Fixture(IsolatedCustomerDatabase target) : IAsyncDisposable
    {
        public IsolatedCustomerDatabase Target { get; } = target;
        public Guid Manager { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        public long Generation { get; set; } = 1;
        public bool AuthorityUnavailable { get; set; }
        public bool MembersUnavailable { get; set; }
        public int AuthorityReads;
        public AclDocument Document { get; private set; } = AclDocument.Empty;
        public AccessService Service(Guid user) => new(Target.Connection, Target.Id, Catalog, _ =>
        {
            Interlocked.Increment(ref AuthorityReads);
            if (AuthorityUnavailable) throw new InvalidOperationException("fixture_source_unavailable");
            return Task.FromResult(new CurrentAccess(Target.Id, user, Generation, 1, new HashSet<string> { "AccessControl" }));
        }, (users, _) =>
        {
            if (MembersUnavailable) throw new InvalidOperationException("fixture_members_unavailable");
            if (users.Any(x => x != Manager && x != Other)) throw new AccessDeniedException(403, "membership_required");
            return Task.CompletedTask;
        });
        public static async Task<Fixture> CreateAsync()
        {
            var target = await M3SqlTests.ReadyAsync();
            var result = new Fixture(target);
            try
            {
                var role = Guid.NewGuid();
                result.Document = new([new(role, "Manager", [new("Access.Manage", PermissionEffect.Allow), new("Sample.Read", PermissionEffect.Allow), new("Sample.Edit", PermissionEffect.Allow)])], [new(result.Manager, role)], []);
                await using var db = AccessDbContext.Open(target.Connection);
                db.State.Add(new AclState { TenantId = target.Id, SecurityGeneration = 1, Revision = 1, AppliedOperationId = Guid.NewGuid(), AclHash = result.Document.Hash() });
                db.Roles.Add(new RoleRow { Id = role, Name = "Manager" });
                foreach (var grant in result.Document.Roles[0].Permissions) db.RoleGrants.Add(new RoleGrantRow { RoleId = role, Permission = grant.Permission, Effect = grant.Effect });
                db.Assignments.Add(new UserRoleRow { UserId = result.Manager, RoleId = role });
                await db.SaveChangesAsync(Ct);
                return result;
            }
            catch { await target.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Target.DisposeAsync();
    }
}
