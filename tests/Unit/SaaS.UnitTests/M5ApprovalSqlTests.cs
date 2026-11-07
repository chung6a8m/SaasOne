using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.OrderApprovals;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM5")]
public sealed class M5ApprovalSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task Error(int status, Func<Task> action) => Assert.Equal(status, (await Assert.ThrowsAsync<AccessDeniedException>(action)).StatusCode);

    [Fact]
    public async Task Submit_records_the_exact_revision_and_cannot_confirm_without_a_decision()
    {
        await using var h = await M5Harness.CreateAsync(Ct);
        var pending = await h.PendingAsync(Ct);
        Assert.Equal(OrderStatus.PendingApproval, pending.Status); Assert.True(pending.ApprovalRequired);
        await using var db = ApprovalsDbContext.Open(h.Connection);
        var request = Assert.Single(await db.Requests.ToArrayAsync(Ct));
        Assert.Equal(pending.Id, request.OrderId); Assert.Equal(pending.Revision, request.SubmittedRevision);
        Assert.Equal(pending.Version, request.SubmittedVersion); Assert.Equal(h.Actor, request.CreatedBy);
        Assert.Equal(h.Actor, request.SubmittedBy); Assert.Empty(await db.Decisions.ToArrayAsync(Ct));
        await h.CountsAsync(2, 1, 0, Ct);
    }
    [Fact]
    public async Task Self_approval_and_self_rejection_are_forbidden_without_side_effects()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct);
        foreach (var action in new[] { OrderAction.Approve, OrderAction.Reject })
            await Error(403, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending, action), Ct));
        await h.CountsAsync(2, 1, 0, Ct);
    }
    [Fact]
    public async Task Rejection_requires_a_reason_and_stores_the_normalized_plain_text()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var command = M4PolicyTests.Command(pending, OrderAction.Reject);
        foreach (var reason in new string?[] { null, "", "  \r\n", new('x', 1001) })
            await Error(400, () => h.Service().ExecuteAsync(command with { Reason = reason }, Ct));
        var result = await h.Service().ExecuteAsync(command with { Reason = "  <script>no</script>\r\nReason  " }, Ct);
        Assert.Equal(OrderStatus.Rejected, result.Order.Status);
        await using var db = ApprovalsDbContext.Open(h.Connection);
        var decision = Assert.Single(await db.Decisions.ToArrayAsync(Ct));
        Assert.False(decision.Approved); Assert.Equal("<script>no</script>\nReason", decision.Reason);
        Assert.Equal(h.Actor, decision.DecidedBy); Assert.Equal(result.Order.Revision, decision.ResultRevision);
        await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Reject_edit_resubmit_preserves_both_requests_decisions_and_obligation()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var creator = h.Actor;
        var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var rejected = (await h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Reject), Ct)).Order;
        h.Actor = creator;
        var draft = (await h.Service().ExecuteAsync(M4PolicyTests.Command(rejected, OrderAction.Edit), Ct)).Order;
        Assert.True(draft.ApprovalRequired); Assert.Equal(OrderStatus.Draft, draft.Status);
        var next = (await h.Service().ExecuteAsync(M4PolicyTests.Command(draft, key: "second-submit"), Ct)).Order;
        h.Actor = Guid.NewGuid();
        await Error(409, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve), Ct));
        var approved = (await h.Service().ExecuteAsync(M4PolicyTests.Command(next, OrderAction.Approve, "new-approval"), Ct)).Order;
        Assert.True(approved.ApprovalRequired); Assert.Equal(OrderStatus.Confirmed, approved.Status);
        await h.CountsAsync(6, 2, 2, Ct); await h.VerifyAsync(Ct);
    }
    [Fact]
    public async Task Concurrent_same_key_approval_executes_once_and_replays_the_committed_revision()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var command = M4PolicyTests.Command(pending, OrderAction.Approve);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Service().ExecuteAsync(command, Ct)));
        Assert.Single(results, x => !x.Replayed); Assert.All(results, x => Assert.Equal(results[0].Order, x.Order));
        await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Concurrent_approve_and_reject_have_exactly_one_effective_decision()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        async Task<int> Decide(OrderAction action)
        {
            try { await h.Service().ExecuteAsync(M4PolicyTests.Command(pending, action, "decision-" + action), Ct); return 200; }
            catch (AccessDeniedException error) { return error.StatusCode; }
        }
        Assert.Equal(new[] { 200, 409 }, (await Task.WhenAll(Decide(OrderAction.Approve), Decide(OrderAction.Reject))).Order().ToArray());
        await h.CountsAsync(3, 1, 1, Ct); await h.VerifyAsync(Ct);
    }
    [Fact]
    public async Task Lost_approve_response_replays_old_expected_tokens_but_a_new_key_conflicts()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var command = M4PolicyTests.Command(pending, OrderAction.Approve);
        var committed = await h.Service().ExecuteAsync(command, Ct);
        var replay = await h.Service().ExecuteAsync(command, Ct);
        Assert.True(replay.Replayed); Assert.Equal(committed.Order, replay.Order);
        await Error(409, () => h.Service().ExecuteAsync(command with { Key = "new-decision-key" }, Ct));
        await Error(409, () => h.Service().ExecuteAsync(command with { ExpectedRevision = 99 }, Ct));
        await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Approval_replay_rechecks_action_visibility_entitlement_and_current_sources()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var command = M4PolicyTests.Command(pending, OrderAction.Approve); await h.Service().ExecuteAsync(command, Ct);
        foreach (var permission in new[] { "OrderApprovals.Decide", "Orders.ViewOthers" })
        {
            h.Denied = [permission];
            await Error(403, () => h.Service().ExecuteAsync(command, Ct));
            await Error(403, () => h.Service().ExecuteAsync(command with { ExpectedRevision = 99 }, Ct));
        }
        h.Denied = []; h.Entitled = false; await Error(403, () => h.Service().ExecuteAsync(command, Ct));
        h.Entitled = true; h.Unavailable = true; await Error(503, () => h.Service().ExecuteAsync(command, Ct));
        await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Decision_replay_is_partitioned_by_actor_client_and_tenant()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var command = M4PolicyTests.Command(pending, OrderAction.Approve); await h.Service().ExecuteAsync(command, Ct);
        h.Client = "other-client"; await Error(409, () => h.Service().ExecuteAsync(command, Ct));
        h.Client = "m5-client"; h.Actor = Guid.NewGuid(); await Error(409, () => h.Service().ExecuteAsync(command, Ct));
        await using var other = await M5Harness.CreateAsync(Ct);
        await Error(404, () => other.Service().ExecuteAsync(command, Ct));
        await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Failure_after_request_insert_rolls_back_Order_Approval_Audit_and_retry_is_single()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var draft = await h.DraftAsync(Ct);
        var command = M4PolicyTests.Command(draft);
        await Error(503, () => h.Service(new ThrowAfterHandler()).ExecuteAsync(command, Ct));
        await h.CountsAsync(1, 0, 0, Ct); Assert.Equal(draft, await h.Service().GetAsync(draft.Id, Ct));
        await h.Service().ExecuteAsync(command, Ct); await h.CountsAsync(2, 1, 0, Ct);
    }
    [Fact]
    public async Task Failure_after_decision_insert_rolls_back_all_contexts_and_retry_is_single()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var command = M4PolicyTests.Command(pending, OrderAction.Approve);
        await Error(503, () => h.Service(new ThrowAfterHandler()).ExecuteAsync(command, Ct));
        Assert.Equal(pending, await h.Service().GetAsync(pending.Id, Ct)); await h.CountsAsync(2, 1, 0, Ct);
        await h.Service().ExecuteAsync(command, Ct); await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Audit_constraint_failure_rolls_back_a_real_decision_and_its_receipt()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        await using var sql = await SqlOperations.OpenAsync(h.Connection, Ct);
        await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [audit].[Events] ADD CONSTRAINT [M5AuditFault] CHECK ([Action] <> 'Orders.Approve');", Ct);
        var command = M4PolicyTests.Command(pending, OrderAction.Approve);
        await Error(503, () => h.Service().ExecuteAsync(command, Ct)); await h.CountsAsync(2, 1, 0, Ct);
        await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [audit].[Events] DROP CONSTRAINT [M5AuditFault];", Ct);
        await h.Service().ExecuteAsync(command, Ct); await h.CountsAsync(3, 1, 1, Ct);
    }
    [Fact]
    public async Task Missing_or_wrong_submitted_request_never_confirms_the_order()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        await using var sql = await SqlOperations.OpenAsync(h.Connection, Ct);
        await SqlOperations.ExecuteAsync(sql, "UPDATE [approvals].[Requests] SET [SubmittedVersion] = 'AAAAAAAAAAA=';", Ct);
        await Error(409, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve), Ct));
        await SqlOperations.ExecuteAsync(sql, "DELETE FROM [approvals].[Requests];", Ct);
        await Error(409, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve), Ct));
        Assert.Equal(pending, await h.Service().GetAsync(pending.Id, Ct));
    }
    [Fact]
    public async Task Missing_schema_fails_closed_and_pending_content_cannot_be_edited()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct);
        await Error(409, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Edit), Ct));
        h.Actor = Guid.NewGuid();
        await using var sql = await SqlOperations.OpenAsync(h.Connection, Ct);
        await SqlOperations.ExecuteAsync(sql, "EXEC sp_rename 'approvals.Decisions.DecidedAt', 'HiddenDecidedAt', 'COLUMN';", Ct);
        await Error(503, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve), Ct));
        Assert.Equal(pending, await h.Service().GetAsync(pending.Id, Ct));
    }
    [Fact]
    public async Task Feature_off_preserves_history_and_does_not_clear_an_outstanding_obligation()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        var rejected = (await h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Reject), Ct)).Order;
        var draft = (await h.Service().ExecuteAsync(M4PolicyTests.Command(rejected, OrderAction.Edit), Ct)).Order;
        h.Entitled = false;
        await Error(403, () => h.Service().ExecuteAsync(M4PolicyTests.Command(draft, key: "disabled-submit"), Ct));
        await using var sql = await SqlOperations.OpenAsync(h.Connection, Ct);
        Assert.Equal(new(ObligationState.Outstanding, 1), await new OrdersObligationReader().ReadAsync(new(h.TenantId, "fixture", "fixture"), sql, Ct));
        await h.VerifyAsync(Ct); await h.CountsAsync(4, 1, 1, Ct);
    }
    [Fact]
    public async Task History_verification_rejects_a_missing_decision_even_after_confirmation()
    {
        await using var h = await M5Harness.CreateAsync(Ct); var pending = await h.PendingAsync(Ct); h.Actor = Guid.NewGuid();
        await h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve), Ct); await h.VerifyAsync(Ct);
        await using var sql = await SqlOperations.OpenAsync(h.Connection, Ct);
        await SqlOperations.ExecuteAsync(sql, "DELETE FROM [approvals].[Decisions];", Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovalHistoryVerifier.VerifyAsync(sql, Ct));
    }
    private sealed class ThrowAfterHandler : IOrderApprovalHandler
    {
        private readonly OrderApprovalHandler _inner = new();
        public Task DemandReadyAsync(TenantTransaction tx, CancellationToken ct) => _inner.DemandReadyAsync(tx, ct);
        public async Task SubmittedAsync(OrderApprovalSubmission value, TenantTransaction tx, CancellationToken ct)
        { await _inner.SubmittedAsync(value, tx, ct); throw new IOException("Injected failure after request write"); }
        public async Task DecidedAsync(OrderApprovalDecision value, TenantTransaction tx, CancellationToken ct)
        { await _inner.DecidedAsync(value, tx, ct); throw new IOException("Injected failure after decision write"); }
    }
}

internal sealed class M5Harness : IAsyncDisposable
{
    private readonly M4SqlHarness _inner;
    private M5Harness(M4SqlHarness inner) { _inner = inner; }
    internal string Connection => _inner.Target.Connection;
    internal Guid TenantId => _inner.Target.Id;
    internal Guid Actor { get; set; } = Guid.NewGuid();
    internal string Client { get; set; } = "m5-client";
    internal string[] Denied { get; set; } = [];
    internal bool Entitled { get; set; } = true;
    internal bool Unavailable { get; set; }
    internal OrderService Service(IOrderApprovalHandler? handler = null) => new(Connection, TenantId, M4PolicyTests.Catalog, (tx, ct) =>
    {
        ct.ThrowIfCancellationRequested(); Assert.Same(tx.Connection, tx.Transaction.Connection);
        if (Unavailable) throw new IOException("Current authority unavailable");
        var access = M4PolicyTests.Access(Actor, Denied.Select(x => new UserOverride(Actor, x, PermissionEffect.Deny)).ToArray(), TenantId);
        return Task.FromResult(access with { Current = access.Current with { Entitlements = new HashSet<string>(Entitled ? ["Orders", "Approval"] : ["Orders"]) }, ClientId = Client });
    }, handler ?? new OrderApprovalHandler());
    internal static async Task<M5Harness> CreateAsync(CancellationToken ct)
    {
        var inner = await M4SqlHarness.CreateAsync(ct);
        try { await using var db = ApprovalsDbContext.Open(inner.Target.Connection); await db.Database.MigrateAsync(ct); return new(inner); }
        catch { await inner.DisposeAsync(); throw; }
    }
    internal async Task<OrderView> DraftAsync(CancellationToken ct) => (await Service().ExecuteAsync(new(OrderAction.Create, "m5-create-key", Draft: M4PolicyTests.Draft), ct)).Order;
    internal async Task<OrderView> PendingAsync(CancellationToken ct) => (await Service().ExecuteAsync(M4PolicyTests.Command(await DraftAsync(ct)), ct)).Order;
    internal async Task CountsAsync(int revisions, int requests, int decisions, CancellationToken ct)
    {
        await _inner.CountsAsync(1, revisions, revisions, ct);
        Assert.Equal(requests, await _inner.CountAsync("[approvals].[Requests]", ct));
        Assert.Equal(decisions, await _inner.CountAsync("[approvals].[Decisions]", ct));
    }
    internal async Task VerifyAsync(CancellationToken ct)
    { await using var sql = await SqlOperations.OpenAsync(Connection, ct); await ApprovalHistoryVerifier.VerifyAsync(sql, ct); }
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
