using SaaS.Abstractions.Tenancy;
using SaaS.Orders;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM4")]
public sealed class M4ObligationSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<ObligationResult> ReadAsync(M4SqlHarness h, Guid? tenant = null)
    {
        await using var connection = await SqlOperations.OpenAsync(h.Target.Connection, Ct);
        return await new OrdersObligationReader().ReadAsync(new(tenant ?? h.Target.Id, "fixture", "fixture"), connection, Ct);
    }
    private static async Task<OrderView> PendingAsync(M4SqlHarness h)
    {
        var draft = await h.Service().ExecuteAsync(new(OrderAction.Create, "reader-create", Draft: M4PolicyTests.Draft), Ct);
        return (await h.Service().ExecuteAsync(M4PolicyTests.Command(draft.Order), Ct)).Order;
    }
    [Fact]
    public async Task Empty_verified_orders_schema_is_clear_but_a_different_database_identity_is_unknown()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        Assert.Equal(new(ObligationState.Clear, 0), await ReadAsync(h));
        Assert.Equal(ObligationState.Unknown, (await ReadAsync(h, Guid.NewGuid())).State);
    }
    [Fact]
    public async Task Reader_counts_pending_rejected_and_rework_without_an_entitlement_or_handler_dependency()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct, true);
        var creator = h.Actor;
        var pending = await PendingAsync(h);
        Assert.Equal(new(ObligationState.Outstanding, 1), await ReadAsync(h));
        h.SetActor(Guid.NewGuid());
        var rejected = await h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Reject, "reader-reject"), Ct);
        Assert.Equal(new(ObligationState.Outstanding, 1), await ReadAsync(h));
        h.SetActor(creator);
        var edited = await h.Service().ExecuteAsync(M4PolicyTests.Command(rejected.Order, OrderAction.Edit, "reader-rework"), Ct);
        h.ApprovalEntitled = false;
        Assert.Equal(OrderStatus.Draft, edited.Order.Status);
        Assert.Equal(new(ObligationState.Outstanding, 1), await ReadAsync(h));
    }
    [Fact]
    public async Task Confirmed_order_preserves_its_marker_but_no_longer_counts_as_outstanding()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct, true);
        var pending = await PendingAsync(h); h.SetActor(Guid.NewGuid());
        var confirmed = await h.Service().ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Approve, "reader-approve"), Ct);
        Assert.True(confirmed.Order.ApprovalRequired);
        Assert.Equal(new(ObligationState.Clear, 0), await ReadAsync(h));
    }
    [Fact]
    public async Task Missing_marker_or_history_never_becomes_an_empty_clear_result()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct, true);
        await PendingAsync(h);
        await using var sql = await SqlOperations.OpenAsync(h.Target.Connection, Ct);
        // Deliberately corrupt the disposable fixture; the enforced CHECK otherwise prevents this fault injection.
        await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [orders].[Orders] DROP CONSTRAINT [CK_OrderState]; EXEC sp_rename 'orders.Orders.ApprovalRequired', 'HiddenMarker', 'COLUMN';", Ct);
        Assert.Equal(ObligationState.Unknown, (await ReadAsync(h)).State);
        await SqlOperations.ExecuteAsync(sql, "EXEC sp_rename 'orders.Orders.HiddenMarker', 'ApprovalRequired', 'COLUMN'; DELETE FROM [orders].[Revisions];", Ct);
        Assert.Equal(ObligationState.Unknown, (await ReadAsync(h)).State);
    }
    [Fact]
    public async Task Unknown_state_and_erased_historical_marker_fail_closed()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct, true);
        var pending = await PendingAsync(h);
        await using var sql = await SqlOperations.OpenAsync(h.Target.Connection, Ct);
        await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [orders].[Orders] NOCHECK CONSTRAINT [CK_OrderState]; UPDATE [orders].[Orders] SET [Status] = 99 WHERE [Id] = @id;", Ct, ("@id", pending.Id));
        Assert.Equal(ObligationState.Unknown, (await ReadAsync(h)).State);
        await SqlOperations.ExecuteAsync(sql, "UPDATE [orders].[Orders] SET [Status] = 1, [ApprovalRequired] = 0 WHERE [Id] = @id;", Ct, ("@id", pending.Id));
        Assert.Equal(ObligationState.Unknown, (await ReadAsync(h)).State);
    }
}
