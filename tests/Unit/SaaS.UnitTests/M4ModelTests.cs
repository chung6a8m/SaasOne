using Microsoft.EntityFrameworkCore;
using SaaS.Auditing;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M4ModelTests
{
    [Fact]
    public void Orders_owns_only_its_schema_with_database_rowversion_revision_and_unique_number()
    {
        using var db = OrdersDbContext.Open("Server=localhost;Database=design;Integrated Security=True");
        Assert.Equal("orders", db.Model.GetDefaultSchema());
        Assert.All(db.Model.GetEntityTypes(), x => Assert.Equal("orders", x.GetSchema()));
        Assert.Null(db.Model.FindEntityType(typeof(TenantAuditEvent)));
        var row = db.Model.FindEntityType(typeof(OrderRow))!;
        Assert.True(row.FindProperty(nameof(OrderRow.Revision))!.IsConcurrencyToken);
        Assert.True(row.FindProperty(nameof(OrderRow.Version))!.IsConcurrencyToken);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate, row.FindProperty(nameof(OrderRow.Version))!.ValueGenerated);
        Assert.Contains(row.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual([nameof(OrderRow.Number)]));
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public void Order_history_and_receipts_reject_mutation_before_opening_a_connection()
    {
        using var db = OrdersDbContext.Open("Server=localhost;Database=design;Integrated Security=True");
        var history = new OrderRevision { OrderId = Guid.NewGuid(), Revision = 1, SnapshotJson = "initial" };
        db.Attach(history); history.SnapshotJson = "rewritten";
        Assert.Equal("order_history_append_only", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
        db.ChangeTracker.Clear();
        var receipt = new OrderReceipt { PartitionHash = new string('A', 64) };
        db.Attach(receipt); db.Remove(receipt);
        Assert.Equal("order_history_append_only", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
    }
    [Fact]
    public void Context_cannot_erase_a_recorded_obligation_change_creator_or_skip_revisions()
    {
        using var db = OrdersDbContext.Open("Server=localhost;Database=design;Integrated Security=True");
        OrderRow Row() => new() { Id = Guid.NewGuid(), CreatedBy = Guid.NewGuid(), Status = OrderStatus.Draft, ApprovalRequired = true, Revision = 3 };
        var row = Row(); db.Attach(row); row.Revision++; row.ApprovalRequired = false;
        Assert.Equal("order_invariant_violation", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
        db.ChangeTracker.Clear(); row = Row(); db.Attach(row); row.Revision++; row.CreatedBy = Guid.NewGuid();
        Assert.Equal("order_invariant_violation", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
        db.ChangeTracker.Clear(); row = Row(); db.Attach(row); row.Revision += 2;
        Assert.Equal("order_invariant_violation", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
        db.ChangeTracker.Clear(); row = Row(); db.Attach(row); db.Remove(row);
        Assert.Equal("order_delete_forbidden", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
    }
    [Fact]
    public void Product_routes_are_explicit_and_never_enable_decision_CMS_or_unbounded_resource_paths()
    {
        var routes = new ProductApplicationEndpoints(); var id = Guid.NewGuid().ToString("D");
        foreach (var path in new[] { "/orders", "/orders/new", "/orders/" + id, "/api/v1/orders", "/api/v1/orders/" + id, "/access" })
            Assert.True(routes.Matches(path, "GET"));
        foreach (var path in new[] { "/orders/new", "/orders/" + id + "/edit", "/api/v1/orders", "/api/v1/orders/" + id + "/submit" })
            Assert.True(routes.Matches(path, "POST"));
        foreach (var path in new[] { "/orders/" + id + "/approve", "/api/v1/orders/" + id + "/reject", "/orders/not-a-guid", "/api/v1/orders/" + Guid.Empty, "/orders/" + id + "/edit/extra", "/admin", "/api/features", "/Setup" })
        { Assert.False(routes.Matches(path, "GET")); Assert.False(routes.Matches(path, "POST")); }
        Assert.False(routes.Matches("/api/v1/orders/" + id, "DELETE"));
    }
}
