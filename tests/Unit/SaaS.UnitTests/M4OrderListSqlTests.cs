using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SaaS.AccessControl;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Operations;
using Xunit;

namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM4")]
public sealed class M4OrderListSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_page_uses_bounded_SQL_commands_and_preserves_Own_Others_pagination()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var rows = await SeedAsync(h, 51, 1);
        using (var queries = new OrdersReadCounter(h.Target.Connection))
        {
            var page = await h.Service().ListAsync(0, Ct);
            Assert.Equal(50, page.Items.Count);
            Assert.True(page.HasMore);
            Assert.Equal(rows.Take(50).Select(x => x.Id), page.Items.Select(x => x.Id));
            // Includes the ID query and all integrity reads, regardless of page size.
            Assert.InRange(queries.Count, 4, 5);
        }

        h.Deny("Orders.ViewOthers");
        var own = await h.Service().ListAsync(0, Ct);
        Assert.True(own.HasMore);
        Assert.Equal(rows.Take(50).Select(x => x.Id), own.Items.Select(x => x.Id));
        var ownLast = await h.Service().ListAsync(1, Ct);
        Assert.Equal(rows[50].Id, Assert.Single(ownLast.Items).Id);
        Assert.False(ownLast.HasMore);

        h.Deny("Orders.ViewOwn");
        var others = await h.Service().ListAsync(0, Ct);
        Assert.Equal(rows[51].Id, Assert.Single(others.Items).Id);
        Assert.False(others.HasMore);
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => h.Service().GetAsync(rows[0].Id, Ct));
        Assert.Equal(403, denied.StatusCode);

        h.Deny("Orders.ViewOwn", "Orders.ViewOthers");
        denied = await Assert.ThrowsAsync<AccessDeniedException>(() => h.Service().ListAsync(0, Ct));
        Assert.Equal(403, denied.StatusCode);
    }

    [Fact]
    public async Task Full_page_rejects_an_old_approval_marker_even_when_latest_snapshot_and_count_match()
    {
        await using var h = await M4SqlHarness.CreateAsync(Ct);
        var rows = await SeedAsync(h, 51, 0);
        var corrupt = rows[0];

        // A valid-looking latest revision must not hide a historical approval obligation.
        await using (var sql = await SqlOperations.OpenAsync(h.Target.Connection, Ct))
            await SqlOperations.ExecuteAsync(sql,
                "UPDATE [orders].[Orders] SET [Revision] = 2 WHERE [Id] = @id; " +
                "UPDATE [orders].[Revisions] SET [ApprovalRequired] = 1 WHERE [OrderId] = @id AND [Revision] = 1;",
                Ct, ("@id", corrupt.Id));
        await using (var db = OrdersDbContext.Open(h.Target.Connection))
        {
            var latest = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == corrupt.Id, Ct);
            db.Revisions.Add(new()
            {
                OrderId = corrupt.Id, Revision = 2, ActorId = h.Actor, Action = OrderAction.Edit,
                Status = OrderStatus.Draft, ApprovalRequired = false,
                SnapshotJson = JsonSerializer.Serialize(latest.View()), OccurredAt = latest.UpdatedAt
            });
            await db.SaveChangesAsync(Ct);
        }

        using var queries = new OrdersReadCounter(h.Target.Connection);
        var invalid = await Assert.ThrowsAsync<AccessDeniedException>(() => h.Service().ListAsync(0, Ct));
        Assert.Equal(503, invalid.StatusCode);
        Assert.Equal("order_history_invalid", invalid.Code);
        Assert.InRange(queries.Count, 4, 5);
    }

    private static async Task<OrderRow[]> SeedAsync(M4SqlHarness h, int owned, int other)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var otherActor = Guid.NewGuid();
        var rows = Enumerable.Range(0, owned + other).Select(i => new OrderRow
        {
            Id = Guid.NewGuid(),
            CreatedBy = i < owned ? h.Actor : otherActor,
            Number = $"LIST-BATCH-{i:D3}",
            CustomerName = "List test",
            Amount = 1m,
            Currency = "USD",
            Notes = "",
            Status = OrderStatus.Draft,
            Revision = 1,
            CreatedAt = timestamp.AddSeconds(-i),
            UpdatedAt = timestamp.AddSeconds(-i)
        }).ToArray();
        await using var db = OrdersDbContext.Open(h.Target.Connection);
        db.Orders.AddRange(rows);
        await db.SaveChangesAsync(Ct);
        db.Revisions.AddRange(rows.Select(row => new OrderRevision
        {
            OrderId = row.Id, Revision = 1, ActorId = row.CreatedBy, Action = OrderAction.Create,
            Status = row.Status, ApprovalRequired = false,
            SnapshotJson = JsonSerializer.Serialize(row.View()), OccurredAt = row.CreatedAt
        }));
        await db.SaveChangesAsync(Ct);
        return rows;
    }

    private sealed class OrdersReadCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database;
        private readonly IDisposable _listeners;
        private readonly List<IDisposable> _subscriptions = [];
        private int _count;

        public OrdersReadCounter(string connection)
        {
            _database = new SqlConnectionStringBuilder(connection).InitialCatalog;
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int Count => Volatile.Read(ref _count);

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
                _subscriptions.Add(listener.Subscribe(this, key => key == RelationalEventId.CommandExecuted.Name));
        }

        public void OnNext(KeyValuePair<string, object?> entry)
        {
            if (entry.Key != RelationalEventId.CommandExecuted.Name
                || entry.Value is not CommandExecutedEventData data
                || data.Context is not OrdersDbContext
                || !string.Equals(data.Command.Connection?.Database, _database, StringComparison.OrdinalIgnoreCase)
                || !data.Command.CommandText.Contains("[orders].[", StringComparison.OrdinalIgnoreCase))
                return;
            Interlocked.Increment(ref _count);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            _listeners.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }
}
