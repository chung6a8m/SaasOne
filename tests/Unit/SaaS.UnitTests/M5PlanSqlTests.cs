using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.OrderApprovals;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M5 HTTP SQL")]
[Trait("Category", "SqlM5")]
public sealed class M5PlanSqlTests(M1SqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants.Single(x => x.Slug == "alpha");
    private M4HttpTests Setup => new(fixture);
    private async Task<TenantRegistration> RegistrationAsync()
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == Alpha.Id, Ct);
    }
    // A real database/transaction authority fixture; HTTP identity and current ACL are exercised separately by M5HttpTests.
    private OrderService Service(Guid actor, IOrderApprovalHandler? handler = null) => new(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
        async (_, ct) =>
        {
            var current = await RegistrationAsync();
            var access = M4PolicyTests.Access(actor, tenant: Alpha.Id);
            return access with { Current = access.Current with { Entitlements = ApplicationAccessProfile.Entitlements(current.Plan) } };
        }, handler ?? new OrderApprovalHandler());
    private async Task<OrderView> PendingAsync(Guid actor)
    {
        var draft = (await Service(actor).ExecuteAsync(new(OrderAction.Create, "plan-create", Draft: M4PolicyTests.Draft), Ct)).Order;
        return (await Service(actor).ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
    }
    [Fact]
    public async Task Real_rejected_and_rework_downgrade_orders_preserve_history_until_correct_revision_is_approved()
    {
        await using var setup = await Setup.HttpAsync(approval: true);
        var creator = Guid.NewGuid(); var manager = Guid.NewGuid(); var pending = await PendingAsync(creator);
        var rejected = (await Service(manager).ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Reject, "reject-first"), Ct)).Order;
        async Task ChangeAsync(string expected)
        {
            var current = await RegistrationAsync();
            var result = await new AccessMaintenance(new()).Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, "Standard", Ct);
            Assert.Equal(expected, result.Plan); Assert.Equal(OperationStatus.None, (await RegistrationAsync()).Operation);
        }
        await ChangeAsync("Approval"); // Rejected -> downgrade -> edit.
        var draft = (await Service(creator).ExecuteAsync(M4PolicyTests.Command(rejected, OrderAction.Edit, "rework-first"), Ct)).Order;
        await ChangeAsync("Approval"); // Rejected -> edit -> downgrade.
        var again = (await Service(creator).ExecuteAsync(M4PolicyTests.Command(draft, key: "submit-again"), Ct)).Order;
        Assert.Equal(409, (await Assert.ThrowsAsync<AccessDeniedException>(() => Service(manager).ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve, "stale-decision"), Ct))).StatusCode);
        await Service(manager).ExecuteAsync(M4PolicyTests.Command(again, OrderAction.Approve, "approve-current"), Ct);
        await ChangeAsync("Standard");
        await using var approvals = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal(2, await approvals.Requests.CountAsync(Ct)); Assert.Equal(2, await approvals.Decisions.CountAsync(Ct));
        var ordinary = (await Service(creator).ExecuteAsync(new(OrderAction.Create, "standard-create", Draft: M4PolicyTests.Draft with { Number = "STANDARD-NEW" }), Ct)).Order;
        Assert.Equal(OrderStatus.Confirmed, (await Service(creator).ExecuteAsync(M4PolicyTests.Command(ordinary, key: "standard-submit"), Ct)).Order.Status);
        Assert.Equal(2, await approvals.Requests.CountAsync(Ct));
    }
    [Fact]
    public async Task Real_plan_reconciliation_survives_every_checkpoint_and_rejects_stale_worker_versions()
    {
        foreach (var crash in new[] { ResourceCheckpoint.Claimed, ResourceCheckpoint.GateClosed, ResourceCheckpoint.ObligationsChecked,
            ResourceCheckpoint.SchemaReady, ResourceCheckpoint.FeaturesReady, ResourceCheckpoint.EntitlementsCommitted, ResourceCheckpoint.GatePrepared })
        {
            await using var setup = await Setup.HttpAsync();
            var actor = Guid.NewGuid();
            var draft = (await Service(actor).ExecuteAsync(new(OrderAction.Create, "checkpoint-draft", Draft: M4PolicyTests.Draft), Ct)).Order;
            var current = await RegistrationAsync(); var operation = Guid.NewGuid();
            var interrupted = new AccessMaintenance(new(), (stage, _) => stage == crash ? throw new IOException("M5 checkpoint fault") : Task.CompletedTask);
            await Assert.ThrowsAsync<IOException>(() => interrupted.Operations.ChangePlanAsync(Alpha.Id, operation, current.RegistryVersion, "Approval", Ct));
            var closed = await RegistrationAsync();
            Assert.False(TenantPolicy.IsOperational(closed.Status(), DateTimeOffset.UtcNow));
            var maintenance = new AccessMaintenance(new());
            await Assert.ThrowsAsync<TenantContractException>(() => maintenance.Operations.ResumeAsync(Alpha.Id, operation, current.RegistryVersion, Ct));
            var completed = await maintenance.Operations.ResumeAsync(Alpha.Id, operation, closed.RegistryVersion, Ct);
            Assert.Equal(ResourceCheckpoint.Completed, completed.Checkpoint); Assert.Equal("Approval", completed.Plan);
            Assert.True((await RegistrationAsync()).Fence > closed.Fence);
            // Idempotent retry must not duplicate schema or overwrite the business row.
            Assert.Equal(completed, await maintenance.Operations.ResumeAsync(Alpha.Id, operation, completed.RegistryVersion, Ct));
            Assert.Equal(draft, await Service(actor).GetAsync(draft.Id, Ct));
            var pending = (await Service(actor).ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
            Assert.Equal(OrderStatus.PendingApproval, pending.Status);
            await using var approvals = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha)); Assert.Single(await approvals.Requests.ToArrayAsync(Ct));
        }
    }
    [Fact]
    public async Task Downgrade_drains_real_submit_approve_reject_and_rework_transactions_before_reading_obligations()
    {
        foreach (var action in new[] { OrderAction.Submit, OrderAction.Approve, OrderAction.Reject, OrderAction.Edit })
        {
            await using var setup = await Setup.HttpAsync(approval: true);
            var creator = Guid.NewGuid(); var manager = Guid.NewGuid();
            var draft = (await Service(creator).ExecuteAsync(new(OrderAction.Create, "race-create", Draft: M4PolicyTests.Draft), Ct)).Order;
            var order = action == OrderAction.Submit ? draft : (await Service(creator).ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
            if (action == OrderAction.Edit) order = (await Service(manager).ExecuteAsync(M4PolicyTests.Command(order, OrderAction.Reject, "race-reject"), Ct)).Order;
            var actor = action is OrderAction.Approve or OrderAction.Reject ? manager : creator;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalAuthorityRead = 0;
            // Hold the command's shared gate after it has read current authority; no mocks replace its writes/handler.
            var service = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog, async (_, ct) =>
            {
                if (Interlocked.Increment(ref finalAuthorityRead) == 2) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
                return M4PolicyTests.Access(actor, tenant: Alpha.Id);
            }, new OrderApprovalHandler());
            var command = service.ExecuteAsync(M4PolicyTests.Command(order, action, "race-command"), Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            var before = await RegistrationAsync();
            var operation = new AccessMaintenance(new(), (stage, _) => { if (stage == ResourceCheckpoint.Claimed) claimed.TrySetResult(); return Task.CompletedTask; });
            var downgrade = operation.Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), before.RegistryVersion, "Standard", Ct);
            try
            {
                await claimed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                Assert.False(downgrade.IsCompleted); release.TrySetResult();
                var changed = await command; var result = await downgrade;
                Assert.Equal(action == OrderAction.Approve ? "Standard" : "Approval", result.Plan);
                Assert.Equal(changed.Order, await Service(actor).GetAsync(order.Id, Ct));
                await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct); await ApprovalHistoryVerifier.VerifyAsync(sql, Ct);
            }
            finally { release.TrySetResult(); await Task.WhenAll(command, downgrade); }
        }
    }
    [Fact]
    public async Task A_pre_authorized_writer_waiting_on_the_gate_cannot_execute_after_a_generation_change()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var pending = await PendingAsync(Guid.NewGuid());
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        var gate = await TenantWriteGate.ReadAsync(sql, null, Ct);
        await using var hold = (SqlTransaction)await sql.BeginTransactionAsync(Ct);
        await SqlTransactionLocks.AcquireAsync(sql, hold, TenantWriteGate.LockResource, true, 30000, Ct);
        var entered = false;
        var waiterName = "M5GateWaiter-" + Guid.NewGuid().ToString("N");
        var waiterConnection = new SqlConnectionStringBuilder(Environment.CustomerAdmin(Alpha)) { ApplicationName = waiterName, Pooling = false };
        var waiter = new TenantTransactionCoordinator(waiterConnection.ConnectionString).ExecuteAsync(Alpha.Id, gate.Version,
            (_, _) => { entered = true; return Task.FromResult(true); }, Ct);
        await using (var observer = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct))
        {
            var blocked = false;
            for (var attempt = 0; attempt < 100 && !blocked; attempt++)
            {
                blocked = Convert.ToInt32(await SqlOperations.ScalarAsync(observer,
                    "SELECT COUNT(*) FROM sys.dm_exec_sessions s JOIN sys.dm_exec_requests r ON s.session_id=r.session_id WHERE s.program_name=@name AND r.wait_type LIKE 'LCK_M_%'", Ct, ("@name", waiterName))) == 1;
                if (!blocked) await Task.Delay(50, Ct);
            }
            Assert.True(blocked, "The pre-authorized writer must actually wait on SQL's gate lock.");
        }
        // This is a fixture-only gate change under the same exclusive SQL lock used by lifecycle operations.
        await using (var update = sql.CreateCommand())
        {
            update.Transaction = hold; update.CommandText = "UPDATE tenancy.WriteGate SET Version=Version+1 WHERE Id=1";
            await update.ExecuteNonQueryAsync(Ct);
        }
        await hold.CommitAsync(Ct);
        Assert.Equal("write_gate_changed", (await Assert.ThrowsAsync<TenantContractException>(() => waiter)).Code);
        Assert.False(entered); Assert.Equal(pending, await Service(Guid.NewGuid()).GetAsync(pending.Id, Ct));
    }
    [Fact]
    public async Task Real_plan_worker_cannot_reopen_over_a_new_suspend_or_pending_deletion()
    {
        foreach (var state in new[] { SubscriptionStatus.Suspended, SubscriptionStatus.PendingDeletion })
        {
            await using var setup = await Setup.HttpAsync(); var current = await RegistrationAsync();
            var maintenance = new AccessMaintenance(new(), async (stage, ct) =>
            {
                if (stage == ResourceCheckpoint.GatePrepared) await new IdentityOperations(new()).SetTenantAsync(Alpha.Id, state, current.ExpiresAt, ct);
            });
            try
            {
                await Assert.ThrowsAsync<TenantContractException>(() => maintenance.Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, "Approval", Ct));
                var changed = await RegistrationAsync(); Assert.Equal(state, changed.Subscription);
                Assert.False(TenantPolicy.IsOperational(changed.Status(), DateTimeOffset.UtcNow)); Assert.NotEqual(OperationStatus.None, changed.Operation);
            }
            finally
            {
                // Fixture cleanup only: the shared HTTP fixture cannot use the public lifecycle
                // operation to reactivate a tombstone. Preserve that production prohibition.
                await using var cleanup = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                var tenant = await cleanup.Tenants.SingleAsync(x => x.Id == Alpha.Id, CancellationToken.None);
                tenant.Subscription = current.Subscription;
                await cleanup.SaveChangesAsync(CancellationToken.None);
            }
        }
    }
    [Fact]
    public async Task Recovery_with_real_feature_off_obligations_requires_approved_child_upgrade_and_keeps_parent_closed()
    {
        await using var setup = await Setup.HttpAsync(approval: true);
        var pending = await PendingAsync(Guid.NewGuid());
        // Simulated restored business data under the current Standard entitlement. This is NOT a SQL restore rehearsal.
        await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            var tenant = await platform.Tenants.SingleAsync(x => x.Id == Alpha.Id, Ct); tenant.Plan = "Standard"; tenant.RegistryVersion++;
            var authority = await platform.RecoveryAuthorities.SingleOrDefaultAsync(x => x.Actor == "saas_platform_operator", Ct);
            if (authority is null) { authority = new RecoveryAuthority { Actor = "saas_platform_operator" }; platform.RecoveryAuthorities.Add(authority); }
            authority.Enabled = true; authority.CanChangePlan = true; authority.RegisteredAt = DateTimeOffset.UtcNow.AddDays(-1); authority.RegisteredBy = "fixture-independent-admin";
            await platform.SaveChangesAsync(Ct);
        }
        await using (var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct))
            await SqlOperations.ExecuteAsync(sql, "UPDATE tenancy.ApplicationProfile SET [Plan]='Standard', ManifestHash=@hash", Ct, ("@hash", ApplicationAccessProfile.Hash("Standard")));
        var maintenance = new AccessMaintenance(new()); var operation = Guid.NewGuid(); var current = await RegistrationAsync();
        await maintenance.Operations.BeginRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Ct);
        current = await RegistrationAsync();
        await Assert.ThrowsAsync<TenantContractException>(() => maintenance.ResumeRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Ct));
        var document = M4HttpTests.SalesAcl(setup.Http.UserId);
        current = await RegistrationAsync(); await maintenance.ApproveAsync(Alpha.Id, operation, current.RegistryVersion, document, "Reviewed restored obligations and current memberships.", false, Ct);
        current = await RegistrationAsync();
        Assert.Equal("recovery_child_upgrade_requires_approval", (await Assert.ThrowsAsync<TenantContractException>(() => maintenance.ResumeRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Ct))).Code);
        current = await RegistrationAsync(); await maintenance.ApproveAsync(Alpha.Id, operation, current.RegistryVersion, document, "Reviewed restored obligations and current memberships.", true, Ct);
        var observed = false;
        var fault = new AccessMaintenance(new(), async (stage, ct) =>
        {
            if (stage != ResourceCheckpoint.FeaturesReady) return;
            observed = true; var parent = await RegistrationAsync();
            Assert.Equal(OperationStatus.Restoring, parent.Operation); Assert.True(parent.SecurityRevalidationRequired);
            Assert.Equal(operation, parent.OperationId); Assert.False(TenantPolicy.IsOperational(parent.Status(), DateTimeOffset.UtcNow));
            await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), ct);
            Assert.False((await TenantWriteGate.ReadAsync(sql, null, ct)).IsOpen);
            throw new IOException("Crash after child features, before parent open");
        });
        current = await RegistrationAsync();
        await Assert.ThrowsAsync<IOException>(() => fault.ResumeRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Ct)); Assert.True(observed);
        current = await RegistrationAsync();
        var result = await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Ct);
        Assert.Equal("Approval", result.Plan); Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint);
        Assert.Equal(pending, await Service(setup.Http.UserId).GetAsync(pending.Id, Ct));
        var decision = (await Service(setup.Http.UserId).ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve, "after-recovery"), Ct)).Order;
        Assert.Equal(OrderStatus.Confirmed, decision.Status); Assert.True(decision.ApprovalRequired);
    }
    [Fact]
    public async Task Missing_real_approval_history_keeps_plan_reconciliation_closed_instead_of_repairing_it()
    {
        await using var setup = await Setup.HttpAsync(approval: true); await PendingAsync(Guid.NewGuid());
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, "DELETE FROM approvals.Requests", Ct);
        var current = await RegistrationAsync();
        Assert.Equal("obligations_unknown", (await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).Operations.ChangePlanAsync(
            Alpha.Id, Guid.NewGuid(), current.RegistryVersion, "Standard", Ct))).Code);
        Assert.False((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
        Assert.Equal("Approval", (await RegistrationAsync()).Plan);
    }
}
