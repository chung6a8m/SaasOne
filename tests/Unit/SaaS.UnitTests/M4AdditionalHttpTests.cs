using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M4HttpTests
{
    [Fact]
    public async Task Native_web_and_API_share_successful_edit_submit_and_cross_transport_replay()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await CreateAsync(http, token); var path = "/alpha/orders/" + order.Id;
        using var page = await browser.GetAsync(path);
        var csrf = HttpBrowser.Csrf(await page.Content.ReadAsStringAsync(Ct));
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf, ["idempotencyKey"] = "ui-parity-edit",
            ["expectedRevision"] = order.Revision.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = order.Version,
            ["number"] = order.Number, ["customerName"] = order.CustomerName, ["amount"] = "99.00", ["currency"] = "USD", ["notes"] = "Revised by web" };
        using (var edited = await browser.PostAsync(path + "/edit", fields)) Assert.Equal(HttpStatusCode.SeeOther, edited.StatusCode);
        using var apiRead = await ApiAsync(http, token, "/alpha/api/v1/orders/" + order.Id);
        var revised = (await apiRead.Content.ReadFromJsonAsync<OrderView>(Ct))!;
        Assert.Equal(2, revised.Revision); Assert.Equal(99, revised.Amount);
        var submit = new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf, ["idempotencyKey"] = "ui-parity-submit",
            ["expectedRevision"] = revised.Revision.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = revised.Version };
        using (var committed = await browser.PostAsync(path + "/submit", submit)) Assert.Equal(HttpStatusCode.SeeOther, committed.StatusCode);
        using var replay = await ApiAsync(http, token, "/alpha/api/v1/orders/" + order.Id + "/submit", "ui-parity-submit", Expected(revised));
        var result = await ResultAsync(replay, HttpStatusCode.OK);
        Assert.True(result.Replayed); Assert.Equal(OrderStatus.Confirmed, result.Order.Status); Assert.Equal(3, result.Order.Revision);
    }
    [Fact]
    public async Task Native_maximum_valid_Unicode_form_and_JSON_have_equivalent_validation()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        using var page = await browser.GetAsync("/alpha/orders/new");
        var customer = new string('\u754c', 160); var notes = new string('\u754c', 2000);
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = HttpBrowser.Csrf(await page.Content.ReadAsStringAsync(Ct)),
            ["idempotencyKey"] = "unicode-form", ["number"] = "UNICODE-1", ["customerName"] = customer, ["amount"] = "1.00", ["currency"] = "USD", ["notes"] = notes };
        using (var encoded = new FormUrlEncodedContent(fields)) Assert.True((await encoded.ReadAsByteArrayAsync(Ct)).Length > 16384);
        using var created = await browser.PostAsync("/alpha/orders/new", fields);
        Assert.Equal(HttpStatusCode.SeeOther, created.StatusCode);
        var id = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last());
        using var read = await ApiAsync(http, token, "/alpha/api/v1/orders/" + id);
        var order = (await read.Content.ReadFromJsonAsync<OrderView>(Ct))!;
        Assert.Equal(customer, order.CustomerName); Assert.Equal(notes, order.Notes);
        using var edited = await ApiAsync(http, token, "/alpha/api/v1/orders/" + id + "/edit", "unicode-json", new { expectedRevision = order.Revision, expectedVersion = order.Version, draft = new OrderDraft(order.Number, customer, 1m, "USD", notes) });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }
    [Fact]
    public async Task Private_install_never_downgrades_an_unknown_future_application_profile()
    {
        await using var setup = await HttpAsync();
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, "UPDATE [tenancy].[ApplicationProfile] SET [Version] = 3", Ct);
        try
        {
            var current = await CurrentRegistrationAsync();
            var error = await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).InstallAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, Ct));
            Assert.Equal("obligations_unknown", error.Code);
            Assert.False((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
            Assert.Equal(3, Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT [Version] FROM [tenancy].[ApplicationProfile]", Ct)));
        }
        finally { await SqlOperations.ExecuteAsync(sql, "UPDATE [tenancy].[ApplicationProfile] SET [Version] = 2", Ct); }
    }
    [Fact]
    public async Task Installed_Orders_with_a_missing_marker_stays_closed_instead_of_runtime_repair()
    {
        await using var setup = await HttpAsync();
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, "ALTER TABLE [orders].[Orders] DROP CONSTRAINT [CK_OrderState]; EXEC sp_rename 'orders.Orders.ApprovalRequired', 'HiddenMarker', 'COLUMN';", Ct);
        try
        {
            var current = await CurrentRegistrationAsync();
            var error = await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).InstallAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, Ct));
            Assert.Equal("obligations_unknown", error.Code);
            Assert.False((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
            Assert.Null(await SqlOperations.ScalarAsync(sql, "SELECT COL_LENGTH('orders.Orders','ApprovalRequired')", Ct) as int?);
        }
        finally
        {
            await SqlOperations.ExecuteAsync(sql, "EXEC sp_rename 'orders.Orders.HiddenMarker', 'ApprovalRequired', 'COLUMN'; ALTER TABLE [orders].[Orders] ADD CONSTRAINT [CK_OrderState] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [CreatedBy] <> '00000000-0000-0000-0000-000000000000' AND [Revision] > 0 AND [Amount] >= 0 AND [Status] IN (1,2,3,4) AND ([Status] NOT IN (2,3) OR [ApprovalRequired] = 1));", Ct);
        }
    }
    [Fact]
    public async Task Private_upgrade_from_verified_M3_installs_Orders_without_replacing_existing_ACL()
    {
        await using var setup = await HttpAsync();
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await using (var orders = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha))) Assert.Empty(await orders.Orders.ToArrayAsync(Ct));
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("FoundationM3:v1\nAccess.Manage:Core:Nondelegable\nWorkspace.View:Core\nWorkspace.Approval:Approval\nProfile.ReadOwn:Core:Owner\nStandard")));
        await SqlOperations.ExecuteAsync(sql, "DROP TABLE [orders].[Receipts]; DROP TABLE [orders].[Revisions]; DROP TABLE [orders].[Orders]; DROP TABLE [orders].[__EFMigrationsHistory]; UPDATE [tenancy].[ApplicationProfile] SET [Version] = 1, [ManifestHash] = @hash;", Ct, ("@hash", legacy));
        var current = await CurrentRegistrationAsync();
        await new AccessMaintenance(new()).InstallAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, Ct);
        await using var db = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha)); await TenancyConnections.VerifySchemaAsync(db, Ct);
        Assert.Equal(2, Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT [Version] FROM [tenancy].[ApplicationProfile]", Ct)));
        Assert.True((await TenantWriteGate.ReadAsync(sql, null, Ct)).IsOpen);
        using var browser = setup.Http.NewBrowser(); await LoginAsync(setup.Http, browser);
        using var visible = await ApiAsync(setup.Http, setup.Http.CapturedTokens["alpha"].AccessToken, "/alpha/api/v1/orders");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
    }
    [Fact]
    public async Task Private_downgrade_preserves_pending_rejected_rework_obligations_until_confirmation()
    {
        await using var setup = await HttpAsync(approval: true); var creator = setup.Http.UserId; var approver = Guid.NewGuid();
        await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            platform.IdentityAccounts.Add(new IdentityAccount { Id = approver, CreatedAt = DateTimeOffset.UtcNow });
            platform.IdentityMemberships.Add(new IdentityMembership { TenantId = Alpha.Id, GlobalUserId = approver, Active = true });
            await platform.SaveChangesAsync(Ct);
        }
        await SeedAsync(Alpha, SalesAcl(creator) with { Assignments = [new(creator, SalesRole), new(approver, SalesRole)] });
        var actor = creator;
        var service = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, ApplicationAccessProfile.Catalog, async (tx, ct) =>
        {
            var access = await TransactionAccessReader.ReadAsync(Alpha.Id, tx, async token =>
            {
                await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == Alpha.Id, token);
                return new(Alpha.Id, actor, current.AuthorizationGeneration, current.RegistryVersion, ApplicationAccessProfile.Entitlements(current.Plan));
            }, ct);
            return new(access.Current, access.Snapshot, "private-marker-fixture");
        }, new MarkerOnlyFixture());
        var draft = (await service.ExecuteAsync(new(OrderAction.Create, "plan-create", Draft: M4PolicyTests.Draft), Ct)).Order;
        var pending = (await service.ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
        async Task DemandPlanAsync(string expected)
        {
            var current = await CurrentRegistrationAsync();
            var result = await new AccessMaintenance(new()).Operations.ChangePlanAsync(Alpha.Id, Guid.NewGuid(), current.RegistryVersion, "Standard", Ct);
            Assert.Equal(expected, result.Plan); Assert.Equal(expected, (await CurrentRegistrationAsync()).Plan);
        }
        await DemandPlanAsync("Approval");
        actor = approver;
        var rejected = (await service.ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Reject, "plan-reject"), Ct)).Order;
        await DemandPlanAsync("Approval");
        actor = creator;
        var rework = (await service.ExecuteAsync(M4PolicyTests.Command(rejected, OrderAction.Edit, "plan-rework"), Ct)).Order;
        Assert.True(rework.ApprovalRequired); await DemandPlanAsync("Approval");
        var again = (await service.ExecuteAsync(M4PolicyTests.Command(rework, key: "plan-resubmit"), Ct)).Order;
        actor = approver;
        var confirmed = (await service.ExecuteAsync(M4PolicyTests.Command(again, OrderAction.Approve, "plan-approve"), Ct)).Order;
        Assert.True(confirmed.ApprovalRequired); await DemandPlanAsync("Standard");
        Assert.Equal(confirmed, await service.GetAsync(confirmed.Id, Ct));
    }
    private async Task<TenantRegistration> CurrentRegistrationAsync()
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        return await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == Alpha.Id, Ct);
    }
    // M4 only supplies the extension contract. This fixture records no M5-owned tables; durable hook atomicity is covered by M4OrderSqlTests.
    private sealed class MarkerOnlyFixture : IOrderApprovalHandler
    {
        public Task DemandReadyAsync(TenantTransaction transaction, CancellationToken ct) => Task.CompletedTask;
        public Task SubmittedAsync(OrderApprovalSubmission submission, TenantTransaction transaction, CancellationToken ct) => Task.CompletedTask;
        public Task DecidedAsync(OrderApprovalDecision decision, TenantTransaction transaction, CancellationToken ct) => Task.CompletedTask;
    }
}
