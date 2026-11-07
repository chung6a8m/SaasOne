using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.OrderApprovals;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M5 HTTP SQL")]
[Trait("Category", "SqlM5")]
public sealed class M5UpgradeSqlTests(M1SqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants.Single(x => x.Slug == "alpha");
    private async Task<TenantRegistration> CurrentAsync()
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == Alpha.Id, Ct);
    }
    private OrderService Service(Guid actor) => new(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
        async (_, _) =>
        {
            var current = await CurrentAsync(); var access = M4PolicyTests.Access(actor, tenant: Alpha.Id);
            return access with { Current = access.Current with { Entitlements = ApplicationAccessProfile.Entitlements(current.Plan) } };
        }, new OrderApprovalHandler());
    private async Task<OrderView> CreateAsync(Guid actor, string key) => (await Service(actor).ExecuteAsync(
        new(OrderAction.Create, key, Draft: M4PolicyTests.Draft with { Number = key }), Ct)).Order;

    // Reproduce the exact persisted M4 manifest contract, rather than asking the current implementation to invent a legacy hash.
    private static string M4Hash(string plan)
    {
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var foundation = Hash("FoundationM3:v1\nAccess.Manage:Core:Nondelegable\nWorkspace.View:Core\nWorkspace.Approval:Approval\nProfile.ReadOwn:Core:Owner\n" + plan);
        return Hash("OrdersM4:v2\n" + foundation + "\n" + string.Join("\n", OrderPermissions.Definitions.Select(x => $"{x.Code}:{x.Entitlement}:{x.ResourceRule}:{x.Delegable}")));
    }
    private async Task SimulateM4SchemaAsync(string plan)
    {
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, """
            DROP TABLE approvals.Decisions; DROP TABLE approvals.Requests; DROP TABLE approvals.__EFMigrationsHistory;
            UPDATE tenancy.ApplicationProfile SET Version=2, ManifestHash=@hash, [Plan]=@plan;
            """, Ct, ("@hash", M4Hash(plan)), ("@plan", plan));
    }
    [Fact]
    public async Task Existing_M4_Standard_orders_upgrade_without_data_loss_or_fabricated_approval_history()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync(); var actor = Guid.NewGuid();
        var old = await CreateAsync(actor, "legacy-create");
        old = (await Service(actor).ExecuteAsync(M4PolicyTests.Command(old, key: "legacy-submit"), Ct)).Order;
        Assert.Equal(OrderStatus.Confirmed, old.Status); Assert.False(old.ApprovalRequired);
        await SimulateM4SchemaAsync("Standard");
        var current = await CurrentAsync();
        var result = await new AccessMaintenance(new()).Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, "Approval", Ct);
        Assert.Equal(ResourceCheckpoint.Completed, result.Checkpoint); Assert.Equal("Approval", result.Plan);
        Assert.Equal(old, await Service(actor).GetAsync(old.Id, Ct));
        await using var db = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Empty(await db.Requests.ToArrayAsync(Ct)); Assert.Empty(await db.Decisions.ToArrayAsync(Ct));
        var next = await CreateAsync(actor, "after-upgrade-create");
        next = (await Service(actor).ExecuteAsync(M4PolicyTests.Command(next, key: "after-upgrade-submit"), Ct)).Order;
        Assert.Equal(OrderStatus.PendingApproval, next.Status);
        var request = Assert.Single(await db.Requests.ToArrayAsync(Ct));
        Assert.Equal(next.Id, request.OrderId); Assert.Equal(next.Version, request.SubmittedVersion);
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await ApplicationAccessProfile.VerifyAsync(Alpha.Id, "Approval", sql, Ct);
        await ApprovalHistoryVerifier.VerifyAsync(sql, Ct);
    }
    [Fact]
    public async Task Legacy_fixture_approval_markers_without_requests_cannot_be_upgraded_or_silently_backfilled()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync(approval: true); var actor = Guid.NewGuid();
        var pending = await CreateAsync(actor, "legacy-marker-create");
        pending = (await Service(actor).ExecuteAsync(M4PolicyTests.Command(pending, key: "legacy-marker-submit"), Ct)).Order;
        await SimulateM4SchemaAsync("Approval");
        var current = await CurrentAsync();
        var failure = await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).Operations.ChangePlanAsync(
            Alpha.Id, Guid.NewGuid(), current.RegistryVersion, "Standard", Ct));
        Assert.Equal("obligations_unknown", failure.Code);
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        Assert.False((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
        Assert.Equal(0, Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT CASE WHEN OBJECT_ID('approvals.Requests') IS NULL THEN 0 ELSE 1 END", Ct)));
        await using var orders = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal(pending, (await orders.Orders.AsNoTracking().SingleAsync(Ct)).View());
        Assert.Equal("Approval", (await CurrentAsync()).Plan);
    }
    [Fact]
    public async Task Real_downgrade_crash_at_every_checkpoint_retains_approved_history_and_resumes_idempotently()
    {
        foreach (var crash in new[] { ResourceCheckpoint.Claimed, ResourceCheckpoint.GateClosed, ResourceCheckpoint.ObligationsChecked,
            ResourceCheckpoint.SchemaReady, ResourceCheckpoint.FeaturesReady, ResourceCheckpoint.EntitlementsCommitted, ResourceCheckpoint.GatePrepared })
        {
            await using var setup = await new M4HttpTests(fixture).HttpAsync(approval: true);
            var creator = Guid.NewGuid(); var manager = Guid.NewGuid();
            var order = await CreateAsync(creator, "downgrade-crash-create");
            order = (await Service(creator).ExecuteAsync(M4PolicyTests.Command(order, key: "downgrade-crash-submit"), Ct)).Order;
            order = (await Service(manager).ExecuteAsync(M4PolicyTests.Command(order, OrderAction.Approve, "downgrade-crash-decide"), Ct)).Order;
            var current = await CurrentAsync(); var operation = Guid.NewGuid();
            var fault = new AccessMaintenance(new(), (stage, _) => stage == crash ? throw new IOException("M5 downgrade checkpoint fault") : Task.CompletedTask);
            await Assert.ThrowsAsync<IOException>(() => fault.Operations.ChangePlanAsync(Alpha.Id, operation, current.RegistryVersion, "Standard", Ct));
            var closed = await CurrentAsync(); Assert.False(TenantPolicy.IsOperational(closed.Status(), DateTimeOffset.UtcNow));
            var maintenance = new AccessMaintenance(new());
            await Assert.ThrowsAsync<TenantContractException>(() => maintenance.Operations.ResumeAsync(Alpha.Id, operation, current.RegistryVersion, Ct));
            var completed = await maintenance.Operations.ResumeAsync(Alpha.Id, operation, closed.RegistryVersion, Ct);
            Assert.Equal("Standard", completed.Plan); Assert.Equal(ResourceCheckpoint.Completed, completed.Checkpoint);
            Assert.Equal(completed, await maintenance.Operations.ResumeAsync(Alpha.Id, operation, completed.RegistryVersion, Ct));
            Assert.Equal(order, await Service(manager).GetAsync(order.Id, Ct));
            await using var db = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.Single(await db.Requests.ToArrayAsync(Ct)); Assert.Single(await db.Decisions.ToArrayAsync(Ct));
            await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
            await ApprovalHistoryVerifier.VerifyAsync(sql, Ct);
        }
    }
}
