using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Auditing;
using SaaS.Orders;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM4")]
public sealed class M4OrderSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static OrderCommand Create(string number = "SO-001", string key = "create-0001") => new(OrderAction.Create, key, Draft: M4PolicyTests.Draft with { Number = number });
    private static async Task Error(int status, Func<Task> action) => Assert.Equal(status, (await Assert.ThrowsAsync<AccessDeniedException>(action)).StatusCode);

    [Fact]
    public async Task Create_persists_one_order_revision_audit_and_receipt_and_reads_the_same_rowversion()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var result = await h.Service().ExecuteAsync(Create(), Ct);
        Assert.False(result.Replayed); Assert.Equal(OrderStatus.Draft, result.Order.Status);
        Assert.Equal(1, result.Order.Revision); Assert.False(result.Order.ApprovalRequired);
        Assert.Equal(h.Actor, result.Order.CreatedBy); Assert.Equal(8, Convert.FromBase64String(result.Order.Version).Length);
        Assert.Equal(result.Order, await h.Service().GetAsync(result.Order.Id, Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Standard_submit_confirms_and_commits_exactly_one_new_revision()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        var result = await h.Service().ExecuteAsync(M4PolicyTests.Command(created.Order), Ct);
        Assert.Equal(OrderStatus.Confirmed, result.Order.Status); Assert.Equal(2, result.Order.Revision);
        Assert.False(result.Order.ApprovalRequired); Assert.NotEqual(created.Order.Version, result.Order.Version);
        await h.CountsAsync(1, 2, 2, Ct);
    }
    [Fact]
    public async Task Lost_create_response_replays_the_original_result_without_duplicate_side_effects()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var command = Create();
        var committed = await h.Service().ExecuteAsync(command, Ct);
        var recovered = await h.Service().ExecuteAsync(command, Ct);
        Assert.True(recovered.Replayed); Assert.Equal(committed.Order, recovered.Order);
        Assert.Equal(committed.ReplayUntil, recovered.ReplayUntil);
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Lost_submit_response_replays_after_state_revision_and_rowversion_changed()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        var command = M4PolicyTests.Command(created.Order);
        var committed = await h.Service().ExecuteAsync(command, Ct);
        var recovered = await h.Service().ExecuteAsync(command, Ct);
        Assert.True(recovered.Replayed); Assert.Equal(committed.Order, recovered.Order);
        await Error(409, () => h.Service().ExecuteAsync(command with { Key = "new-submit-key" }, Ct));
        await h.CountsAsync(1, 2, 2, Ct);
    }
    [Fact]
    public async Task Edit_replay_returns_its_committed_snapshot_not_a_later_revision()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        var command = M4PolicyTests.Command(created.Order, OrderAction.Edit);
        var edited = await h.Service().ExecuteAsync(command, Ct);
        var next = await h.Service().ExecuteAsync(M4PolicyTests.Command(edited.Order, OrderAction.Edit, "next-edit-key") with { Draft = M4PolicyTests.Draft with { Amount = 88 } }, Ct);
        var replay = await h.Service().ExecuteAsync(command, Ct);
        Assert.True(replay.Replayed); Assert.Equal(edited.Order, replay.Order);
        Assert.Equal(3, next.Order.Revision); Assert.Equal(2, replay.Order.Revision);
        await h.CountsAsync(1, 3, 3, Ct);
    }
    [Fact]
    public async Task Same_key_different_payload_conflicts_without_writing_history_or_audit()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await h.Service().ExecuteAsync(Create(), Ct);
        await Error(409, () => h.Service().ExecuteAsync(Create() with { Draft = M4PolicyTests.Draft with { Amount = 15 } }, Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Concurrent_same_key_executes_once_and_the_waiter_replays()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var command = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Service().ExecuteAsync(command, Ct)));
        Assert.Single(results, x => !x.Replayed);
        Assert.All(results, x => Assert.Equal(results[0].Order, x.Order));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Concurrent_different_keys_on_one_revision_have_one_winner()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        async Task<int> Run(string key)
        {
            try { await h.Service().ExecuteAsync(M4PolicyTests.Command(created.Order, OrderAction.Edit, key), Ct); return 200; }
            catch (AccessDeniedException error) { return error.StatusCode; }
        }
        var statuses = await Task.WhenAll(Run("edit-key-a"), Run("edit-key-b"));
        Assert.Equal(new[] { 200, 409 }, statuses.Order().ToArray());
        await h.CountsAsync(1, 2, 2, Ct);
    }
    [Fact]
    public async Task Duplicate_normalized_number_rolls_back_the_complete_second_command()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await h.Service().ExecuteAsync(Create(), Ct);
        await Error(409, () => h.Service().ExecuteAsync(Create(" so-001 ", "different-key"), Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Tenant_actor_and_client_partitions_cannot_return_another_contexts_receipt()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var a = await h.Service().ExecuteAsync(Create(), Ct);
        h.SetActor(Guid.NewGuid());
        var b = await h.Service().ExecuteAsync(Create("SO-002"), Ct);
        h.Client = "another-client";
        var c = await h.Service().ExecuteAsync(Create("SO-003"), Ct);
        Assert.False(b.Replayed); Assert.False(c.Replayed);
        Assert.NotEqual(a.Order.CreatedBy, b.Order.CreatedBy); Assert.NotEqual(b.Order.Id, c.Order.Id);
        await using var other = await M4SqlHarness.CreateAsync(Ct);
        await Error(404, () => other.Service().GetAsync(a.Order.Id, Ct));
        var d = await other.Service().ExecuteAsync(Create(), Ct);
        Assert.False(d.Replayed); Assert.NotEqual(a.Order.Id, d.Order.Id);
        await h.CountsAsync(3, 3, 3, Ct); await other.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task List_filters_ownership_before_pagination_and_does_not_expose_other_owners_rows()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var owner = h.Actor;
        var own = await h.Service().ExecuteAsync(Create(), Ct);
        h.SetActor(Guid.NewGuid());
        var other = await h.Service().ExecuteAsync(Create("SO-OTHER", "other-create"), Ct);
        h.SetActor(owner); h.Deny("Orders.ViewOthers");
        var page = await h.Service().ListAsync(0, Ct);
        Assert.Equal(own.Order, Assert.Single(page.Items)); Assert.False(page.HasMore);
        await Error(403, () => h.Service().GetAsync(other.Order.Id, Ct));
        h.Deny("Orders.ViewOwn", "Orders.ViewOthers");
        await Error(403, () => h.Service().ListAsync(0, Ct));
    }
    [Fact]
    public async Task Revoked_action_denies_replay_before_disclosing_a_result_or_fingerprint_conflict()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        var submit = M4PolicyTests.Command(created.Order);
        await h.Service().ExecuteAsync(submit, Ct);
        h.Deny("Orders.SubmitOwn");
        await Error(403, () => h.Service().ExecuteAsync(submit, Ct));
        await Error(403, () => h.Service().ExecuteAsync(submit with { ExpectedRevision = 99 }, Ct));
        h.Deny("Orders.Create");
        await Error(403, () => h.Service().ExecuteAsync(Create(), Ct));
        await Error(403, () => h.Service().ExecuteAsync(Create("SO-DIFFERENT"), Ct));
        await h.CountsAsync(1, 2, 2, Ct);
    }
    [Fact]
    public async Task Revoked_read_and_entitlement_deny_replay_even_with_action_allowed()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await h.Service().ExecuteAsync(Create(), Ct);
        h.Deny("Orders.ViewOwn");
        await Error(403, () => h.Service().ExecuteAsync(Create(), Ct));
        h.Deny(); h.OrdersEntitled = false;
        await Error(403, () => h.Service().ExecuteAsync(Create(), Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Current_authority_source_failure_denies_replay_and_does_not_use_an_old_allow()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await h.Service().ExecuteAsync(Create(), Ct);
        h.SourceUnavailable = true;
        await Error(503, () => h.Service().ExecuteAsync(Create(), Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Expired_receipt_is_an_authorized_tombstone_not_a_reusable_key()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await h.Service().ExecuteAsync(Create(), Ct);
        h.Clock.Advance(TimeSpan.FromDays(8));
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => h.Service().ExecuteAsync(Create(), Ct));
        Assert.Equal(409, error.StatusCode); Assert.Equal("idempotency_expired", error.Code);
        h.Deny("Orders.Create");
        await Error(403, () => h.Service().ExecuteAsync(Create(), Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Real_audit_constraint_failure_rolls_back_order_history_and_receipt()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await using (var sql = await SqlOperations.OpenAsync(h.Target.Connection, Ct))
            await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [audit].[Events] ADD CONSTRAINT [CK_M4RejectAudit] CHECK ([Action] <> 'Orders.Create');", Ct);
        var probe = await Assert.ThrowsAsync<DbUpdateException>(() => new TenantTransactionCoordinator(h.Target.Connection).ExecuteAsync(h.Target.Id, 1, async (tx, ct) =>
        {
            await TenantAuditWriter.AppendAsync(tx, new TenantAuditEvent { Id = Guid.NewGuid(), TenantId = h.Target.Id,
                ActorId = h.Actor, Action = "Orders.Create", PayloadHash = new string('A', 64), SecurityGeneration = 1, ResultRevision = 1, OccurredAt = h.Clock.GetUtcNow() }, ct);
            return true;
        }, Ct));
        Assert.Equal(547, Assert.IsType<SqlException>(probe.InnerException).Number);
        await Error(503, () => h.Service().ExecuteAsync(Create(), Ct));
        await h.CountsAsync(0, 0, 0, Ct);
    }
    [Fact]
    public async Task Approval_entitlement_without_a_handler_never_confirms_the_order()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        h.ApprovalEntitled = true;
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        await Error(503, () => h.Service().ExecuteAsync(M4PolicyTests.Command(created.Order), Ct));
        Assert.Equal(created.Order, await h.Service().GetAsync(created.Order.Id, Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Handler_write_then_failure_rolls_back_the_entire_submit()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct, true);
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        h.Handler!.FailAfterWrite = true;
        await Error(503, () => h.Service().ExecuteAsync(M4PolicyTests.Command(created.Order), Ct));
        Assert.Equal(created.Order, await h.Service().GetAsync(created.Order.Id, Ct));
        Assert.Equal(0, await h.CountAsync("[fixture].[Requests]", Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
    [Fact]
    public async Task Rejected_edit_resubmit_and_approval_keep_revision_bound_obligation_and_history()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct, true);
        var creator = h.Actor;
        var created = await h.Service().ExecuteAsync(Create(), Ct);
        var pending = await h.Service().ExecuteAsync(M4PolicyTests.Command(created.Order), Ct);
        Assert.Equal(OrderStatus.PendingApproval, pending.Order.Status); Assert.True(pending.Order.ApprovalRequired);
        await Error(403, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending.Order, OrderAction.Reject, "reject-self"), Ct));
        h.SetActor(Guid.NewGuid());
        var rejected = await h.Service().ExecuteAsync(M4PolicyTests.Command(pending.Order, OrderAction.Reject, "reject-key"), Ct);
        Assert.Equal(OrderStatus.Rejected, rejected.Order.Status);
        var rejectedDetail = await h.Service().GetDetailAsync(rejected.Order.Id, Ct);
        var rejectedDecision = Assert.Single(rejectedDetail.ApprovalDecisions!);
        Assert.Equal(pending.Order.Revision, rejectedDecision.SubmittedRevision);
        Assert.Equal(rejected.Order.Revision, rejectedDecision.ResultRevision);
        Assert.False(rejectedDecision.Approved);
        Assert.Equal("Incorrect amount", rejectedDecision.Reason);
        Assert.NotEqual(default, rejectedDecision.DecidedAt);
        var replay = await h.Service().ExecuteAsync(M4PolicyTests.Command(pending.Order, OrderAction.Reject, "reject-key"), Ct);
        Assert.True(replay.Replayed); Assert.Equal(rejected.Order, replay.Order);
        h.SetActor(creator);
        var edited = await h.Service().ExecuteAsync(M4PolicyTests.Command(rejected.Order, OrderAction.Edit, "rework-key"), Ct);
        Assert.Equal(OrderStatus.Draft, edited.Order.Status); Assert.True(edited.Order.ApprovalRequired);
        h.ApprovalEntitled = false;
        await Error(403, () => h.Service().ExecuteAsync(M4PolicyTests.Command(edited.Order, key: "resubmit-key"), Ct));
        h.ApprovalEntitled = true;
        var resubmitted = await h.Service().ExecuteAsync(M4PolicyTests.Command(edited.Order, key: "resubmit-key"), Ct);
        h.SetActor(Guid.NewGuid());
        await Error(409, () => h.Service().ExecuteAsync(M4PolicyTests.Command(pending.Order, OrderAction.Approve, "stale-decision"), Ct));
        var approved = await h.Service().ExecuteAsync(M4PolicyTests.Command(resubmitted.Order, OrderAction.Approve, "approve-key"), Ct);
        Assert.Equal(OrderStatus.Confirmed, approved.Order.Status); Assert.Equal(6, approved.Order.Revision); Assert.True(approved.Order.ApprovalRequired);
        var decidedDetail = await h.Service().GetDetailAsync(approved.Order.Id, Ct);
        var history = Assert.IsType<OrderApprovalDecisionView[]>(decidedDetail.ApprovalDecisions);
        Assert.Equal(2, history.Length);
        Assert.Equal(pending.Order.Revision, history[0].SubmittedRevision);
        Assert.Equal(rejected.Order.Revision, history[0].ResultRevision);
        Assert.False(history[0].Approved);
        Assert.Equal("Incorrect amount", history[0].Reason);
        Assert.Equal(resubmitted.Order.Revision, history[1].SubmittedRevision);
        Assert.Equal(approved.Order.Revision, history[1].ResultRevision);
        Assert.True(history[1].Approved);
        Assert.Null(history[1].Reason);
        Assert.NotEqual(default, history[1].DecidedAt);
        Assert.Null((await h.Service().GetAsync(approved.Order.Id, Ct)).ApprovalDecisions);
        Assert.Equal(2, await h.CountAsync("[fixture].[Requests]", Ct));
        await h.CountsAsync(1, 6, 6, Ct);
    }
    [Fact]
    public async Task Closed_write_gate_denies_both_new_commands_and_replay()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        await h.Service().ExecuteAsync(Create(), Ct);
        await TenantWriteGate.CloseAsync(h.Target.Connection, h.Target.Id, 1, Guid.NewGuid(), 2, 5000, Ct);
        await Error(503, () => h.Service().ExecuteAsync(Create(), Ct));
        await Error(503, () => h.Service().ExecuteAsync(Create("SO-NEW", "new-create"), Ct));
        await h.CountsAsync(1, 1, 1, Ct);
    }
}

internal sealed class M4Clock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan time) => _now += time;
}

internal sealed class M4SqlHarness : IAsyncDisposable
{
    internal required IsolatedCustomerDatabase Target { get; init; }
    internal Guid Actor { get; private set; } = Guid.NewGuid();
    internal string Client { get; set; } = "test-client";
    internal bool ApprovalEntitled { get; set; }
    internal bool OrdersEntitled { get; set; } = true;
    internal bool SourceUnavailable { get; set; }
    internal M4Clock Clock { get; } = new();
    internal M4ApprovalFixture? Handler { get; private set; }
    private string[] _denied = [];
    internal void SetActor(Guid actor) { Actor = actor; _denied = []; }
    internal void Deny(params string[] permissions) => _denied = permissions;
    internal OrderService Service() => new(Target.Connection, Target.Id, M4PolicyTests.Catalog, (tx, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        Assert.Same(tx.Connection, tx.Transaction.Connection);
        if (SourceUnavailable) throw new IOException("Fixture current-authority outage");
        var access = M4PolicyTests.Access(Actor, _denied.Select(x => new UserOverride(Actor, x, PermissionEffect.Deny)).ToArray(), Target.Id);
        var entitlements = new HashSet<string>();
        if (OrdersEntitled) entitlements.Add("Orders");
        if (ApprovalEntitled) entitlements.Add("Approval");
        return Task.FromResult(access with { Current = access.Current with { Entitlements = entitlements }, ClientId = Client });
    }, Handler, Clock);
    internal static async Task<M4SqlHarness> CreateAsync(CancellationToken ct, bool approvals = false)
    {
        var target = await M3SqlTests.ReadyAsync();
        try
        {
            await using var orders = SaaS.Orders.Data.OrdersDbContext.Open(target.Connection);
            await orders.Database.MigrateAsync(ct);
            var result = new M4SqlHarness { Target = target, ApprovalEntitled = approvals };
            if (approvals)
            {
                await using var sql = await SqlOperations.OpenAsync(target.Connection, ct);
                await SqlOperations.ExecuteAsync(sql, "CREATE SCHEMA [fixture];", ct);
                await SqlOperations.ExecuteAsync(sql, "CREATE TABLE [fixture].[Requests] ([OrderId] uniqueidentifier NOT NULL, [Revision] bigint NOT NULL, [Version] varchar(16) NOT NULL, [Decision] bit NULL, [Reason] nvarchar(1000) NULL, [DecidedAt] datetimeoffset NULL, CONSTRAINT [PK_FixtureRequests] PRIMARY KEY ([OrderId], [Revision]));", ct);
                result.Handler = new();
            }
            return result;
        }
        catch { await target.DisposeAsync(); throw; }
    }
    internal async Task<int> CountAsync(string table, CancellationToken ct)
    {
        await using var sql = await SqlOperations.OpenAsync(Target.Connection, ct);
        return Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM " + table, ct));
    }
    internal async Task CountsAsync(int orders, int revisions, int receipts, CancellationToken ct)
    {
        Assert.Equal(orders, await CountAsync("[orders].[Orders]", ct));
        Assert.Equal(revisions, await CountAsync("[orders].[Revisions]", ct));
        Assert.Equal(receipts, await CountAsync("[orders].[Receipts]", ct));
        Assert.Equal(revisions, await CountAsync("[audit].[Events]", ct));
    }
    public ValueTask DisposeAsync() => Target.DisposeAsync();
}

internal sealed class M4ApprovalFixture : IOrderApprovalHandler
{
    internal bool FailAfterWrite { get; set; }
    public async Task DemandReadyAsync(TenantTransaction tx, CancellationToken ct)
    {
        await using var command = SqlOperations.Command((SqlConnection)tx.Connection, "SELECT TOP(0) [OrderId], [Revision], [Version], [Decision] FROM [fixture].[Requests]");
        command.Transaction = (SqlTransaction)tx.Transaction;
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<OrderApprovalDecisionView[]> ReadDecisionsAsync(Guid orderId, long currentRevision, TenantTransaction tx, CancellationToken ct)
    {
        if (orderId == Guid.Empty || currentRevision < 2) throw new AccessDeniedException(503, "approval_history_invalid");
        await using var command = SqlOperations.Command((SqlConnection)tx.Connection, """
            SELECT [Revision], [Decision], [Reason], [DecidedAt] FROM [fixture].[Requests]
            WHERE [OrderId] = @id AND [Decision] IS NOT NULL ORDER BY [Revision]
            """, ("@id", orderId));
        command.Transaction = (SqlTransaction)tx.Transaction;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var decisions = new List<OrderApprovalDecisionView>();
        while (await reader.ReadAsync(ct))
        {
            var revision = reader.GetInt64(0);
            var approved = reader.GetBoolean(1);
            var reason = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (revision < 2 || revision >= currentRevision || reader.IsDBNull(3)
                || (approved && reason is not null) || (!approved && string.IsNullOrWhiteSpace(reason)))
                throw new AccessDeniedException(503, "approval_history_invalid");
            decisions.Add(new(revision, checked(revision + 1), approved, reason, reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return decisions.ToArray();
    }
    public async Task SubmittedAsync(OrderApprovalSubmission submission, TenantTransaction tx, CancellationToken ct)
    {
        await using var command = SqlOperations.Command((SqlConnection)tx.Connection,
            "INSERT INTO [fixture].[Requests] ([OrderId], [Revision], [Version]) VALUES (@id, @revision, @version)",
            ("@id", submission.OrderId), ("@revision", submission.SubmittedRevision), ("@version", submission.Version));
        command.Transaction = (SqlTransaction)tx.Transaction;
        await command.ExecuteNonQueryAsync(ct);
        if (FailAfterWrite) throw new IOException("Fixture handler interrupted after its write");
    }
    public async Task DecidedAsync(OrderApprovalDecision decision, TenantTransaction tx, CancellationToken ct)
    {
        await using var command = SqlOperations.Command((SqlConnection)tx.Connection,
            "UPDATE [fixture].[Requests] SET [Decision] = @approved, [Reason] = @reason, [DecidedAt] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE [OrderId] = @id AND [Revision] = @revision AND [Version] = @version AND [Decision] IS NULL",
            ("@id", decision.OrderId), ("@revision", decision.SubmittedRevision), ("@version", decision.SubmittedVersion),
            ("@approved", decision.Approved), ("@reason", decision.Reason));
        command.Transaction = (SqlTransaction)tx.Transaction;
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new AccessDeniedException(409, "approval_revision_changed");
    }
}
