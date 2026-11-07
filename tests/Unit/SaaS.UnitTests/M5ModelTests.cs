using Microsoft.EntityFrameworkCore;
using SaaS.OrderApprovals.Data;
using SaaS.Orders.Data;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M5ModelTests
{
    [Fact]
    public void Approvals_owns_only_its_schema_and_enforces_one_decision_per_submitted_revision()
    {
        using var db = ApprovalsDbContext.Open("Server=localhost;Database=design;Integrated Security=True");
        Assert.Equal("approvals", db.Model.GetDefaultSchema());
        Assert.All(db.Model.GetEntityTypes(), x => Assert.Equal("approvals", x.GetSchema()));
        Assert.Null(db.Model.FindEntityType(typeof(OrderRow)));
        foreach (var type in new[] { typeof(ApprovalRequest), typeof(ApprovalDecision) })
            Assert.Equal(new[] { "OrderId", "SubmittedRevision" }, db.Model.FindEntityType(type)!.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Single(db.Model.FindEntityType(typeof(ApprovalDecision))!.GetForeignKeys());
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public void Approval_requests_and_decisions_are_append_only_before_opening_a_connection()
    {
        using var db = ApprovalsDbContext.Open("Server=localhost;Database=design;Integrated Security=True");
        var request = new ApprovalRequest { OrderId = Guid.NewGuid(), SubmittedRevision = 2, SubmittedVersion = "AAAAAAAAAAA=" };
        db.Attach(request); request.CreatedBy = Guid.NewGuid();
        Assert.Equal("approval_history_append_only", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
        db.ChangeTracker.Clear();
        var decision = new ApprovalDecision { OrderId = request.OrderId, SubmittedRevision = 2, ResultRevision = 3 };
        db.Attach(decision); db.Remove(decision);
        Assert.Equal("approval_history_append_only", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
    }
}
