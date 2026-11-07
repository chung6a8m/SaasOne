using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM3")]
public sealed class M3RecoveryApplySqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly PermissionCatalog Catalog = new([new("Access.Manage", "AccessControl", Delegable: false), new("Sample.Read", "AccessControl")]);

    [Theory]
    [InlineData("UserRole")]
    [InlineData("RolePermission")]
    [InlineData("OverrideDeny")]
    public async Task Restored_rights_never_survive_explicit_full_replacement_and_fresh_credentials(string variant)
    {
        await using var fixture = await Setup.CreateAsync(variant);
        var service = fixture.Applier();
        var fresh = new CurrentAccess(fixture.Target.Id, fixture.User, 2, 1, new HashSet<string> { "AccessControl" });
        Assert.Equal(503, Assert.Throws<AccessDeniedException>(() => new PermissionEvaluator(Catalog).Require(fresh, fixture.Historical, "Sample.Read")).StatusCode);
        var marker = await service.ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct);
        Assert.Equal(2, marker.Generation);
        Assert.Equal(fixture.Approval.OperationId, marker.OperationId);
        var verified = await service.VerifyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct);
        Assert.Equal(marker, verified);
        await using var db = AccessDbContext.Open(fixture.Target.Connection);
        Assert.Empty(await db.Assignments.ToArrayAsync(Ct));
        Assert.Empty(await db.Roles.ToArrayAsync(Ct));
        Assert.Empty(await db.RoleGrants.ToArrayAsync(Ct));
        Assert.Equal(variant == "OverrideDeny" ? 1 : 0, await db.UserGrants.CountAsync(Ct));
        var current = new AccessSnapshot(fixture.Target.Id, 2, marker.Revision, fixture.Replacement);
        Assert.Equal(403, Assert.Throws<AccessDeniedException>(() => new PermissionEvaluator(Catalog).Require(fresh, current, "Sample.Read")).StatusCode);
        Assert.Single(await db.History.ToArrayAsync(Ct));
        await using var audit = AuditDbContext.Open(fixture.Target.Connection);
        var entry = Assert.Single(await audit.Events.ToArrayAsync(Ct));
        Assert.Equal("AclRevalidated", entry.Action);
        Assert.Equal("current-platform-approver", entry.OperatorActor);
        Assert.False((await TenantWriteGate.ReadAsync(fixture.Sql, null, Ct)).IsOpen);
    }

    [Fact]
    public async Task Recovery_apply_replay_rechecks_external_authority_memberships_and_audit_marker()
    {
        await using var fixture = await Setup.CreateAsync("OverrideDeny");
        var service = fixture.Applier();
        var first = await service.ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct);
        Assert.Equal(first, await service.ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct));
        fixture.AuthorityAllowed = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct))).StatusCode);
        fixture.AuthorityAllowed = true;
        fixture.MemberAllowed = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.VerifyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct))).StatusCode);
        fixture.MemberAllowed = true;
        await SqlOperations.ExecuteAsync(fixture.Sql, "DELETE FROM audit.Events", Ct);
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => service.VerifyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct))).StatusCode);
    }

    [Fact]
    public async Task Recovery_audit_failure_rolls_back_replacement_history_and_generation_marker()
    {
        await using var fixture = await Setup.CreateAsync("UserRole");
        await SqlOperations.ExecuteAsync(fixture.Sql, "ALTER TABLE audit.Events ADD CONSTRAINT CK_RecoveryAuditFailure CHECK (Action <> 'AclRevalidated')", Ct);
        Assert.Equal(503, (await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Applier().ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct))).StatusCode);
        await using var db = AccessDbContext.Open(fixture.Target.Connection);
        Assert.Equal(1, (await db.State.SingleAsync(Ct)).SecurityGeneration);
        Assert.Single(await db.Assignments.ToArrayAsync(Ct));
        Assert.Empty(await db.History.ToArrayAsync(Ct));
        Assert.False((await TenantWriteGate.ReadAsync(fixture.Sql, null, Ct)).IsOpen);
    }

    [Fact]
    public async Task Recovery_rejects_missing_external_decision_wrong_identity_open_gate_and_unapproved_document()
    {
        await using var fixture = await Setup.CreateAsync("RolePermission");
        fixture.AuthorityAllowed = false;
        await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Applier().ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct));
        fixture.AuthorityAllowed = true;
        var altered = fixture.Approval with { AclJson = fixture.Historical.Document.CanonicalJson() };
        await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Applier().ApplyAsync(fixture.Sql, altered, fixture.DemandAsync, Ct));
        var wrong = fixture.Approval with { TenantId = Guid.NewGuid() };
        await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Applier().ApplyAsync(fixture.Sql, wrong, (_, _) => Task.CompletedTask, Ct));
        var gate = await TenantWriteGate.ReadAsync(fixture.Sql, null, Ct);
        await TenantWriteGate.PrepareOpenAsync(fixture.Target.Connection, fixture.Target.Id, gate.Version, gate.OperationId, gate.Fence, Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => fixture.Applier().ApplyAsync(fixture.Sql, fixture.Approval, fixture.DemandAsync, Ct));
        await using var db = AccessDbContext.Open(fixture.Target.Connection);
        Assert.Equal(1, (await db.State.SingleAsync(Ct)).SecurityGeneration);
    }

    private sealed class Setup(IsolatedCustomerDatabase target, SqlConnection sql) : IAsyncDisposable
    {
        internal IsolatedCustomerDatabase Target { get; } = target;
        internal SqlConnection Sql { get; } = sql;
        internal Guid User { get; } = Guid.NewGuid();
        internal bool AuthorityAllowed { get; set; } = true;
        internal bool MemberAllowed { get; set; } = true;
        internal AccessSnapshot Historical { get; private set; } = null!;
        internal AclDocument Replacement { get; private set; } = AclDocument.Empty;
        internal RecoveryApproval Approval { get; private set; } = null!;
        internal RecoveryAclApplier Applier() => new(Catalog, (tenant, users, _) =>
        {
            Assert.Equal(Target.Id, tenant);
            if (!MemberAllowed || users.Any(x => x != User)) throw new AccessDeniedException(403, "current_membership_required");
            return Task.CompletedTask;
        });
        internal Task DemandAsync(RecoveryApproval approval, CancellationToken ct)
        {
            if (!AuthorityAllowed || approval != Approval) throw new AccessDeniedException(403, "current_platform_approval_required");
            return Task.CompletedTask;
        }
        internal static async Task<Setup> CreateAsync(string variant)
        {
            var target = await M3SqlTests.ReadyAsync();
            var sql = await SqlOperations.OpenAsync(target.Connection, Ct);
            var result = new Setup(target, sql);
            try
            {
                var role = Guid.NewGuid();
                var old = new AclDocument([new(role, "Restored Manager", [new("Access.Manage", PermissionEffect.Allow), new("Sample.Read", PermissionEffect.Allow)])],
                    [new(result.User, role)], variant == "OverrideDeny" ? [new(result.User, "Sample.Read", PermissionEffect.Allow)] : []);
                result.Historical = new(target.Id, 1, 1, old);
                result.Replacement = variant == "OverrideDeny" ? new([], [], [new(result.User, "Sample.Read", PermissionEffect.Deny)]) : AclDocument.Empty;
                await using var db = AccessDbContext.Open(target.Connection);
                db.State.Add(new AclState { TenantId = target.Id, SecurityGeneration = 1, Revision = 1, AppliedOperationId = Guid.NewGuid(), AclHash = old.Hash() });
                db.Roles.Add(new RoleRow { Id = role, Name = "Restored Manager" });
                foreach (var grant in old.Roles[0].Permissions) db.RoleGrants.Add(new RoleGrantRow { RoleId = role, Permission = grant.Permission, Effect = grant.Effect });
                db.Assignments.Add(new UserRoleRow { UserId = result.User, RoleId = role });
                foreach (var value in old.Overrides) db.UserGrants.Add(new UserGrantRow { UserId = value.UserId, Permission = value.Permission, Effect = value.Effect });
                await db.SaveChangesAsync(Ct);
                var operation = Guid.NewGuid();
                var gate = await TenantWriteGate.CloseAsync(target.Connection, target.Id, 1, operation, 2, 5000, Ct);
                result.Approval = new(target.Id, operation, 2, gate.Version, gate.Fence, result.Replacement.CanonicalJson(), result.Replacement.Hash(), "current-platform-approver", "Approved revocation after fixture restore");
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Sql.DisposeAsync(); await Target.DisposeAsync(); }
    }
}
