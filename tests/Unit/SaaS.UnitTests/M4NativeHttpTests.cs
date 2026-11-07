using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl;
using SaaS.Auditing;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M4HttpTests
{
    [Fact]
    public async Task Native_UI_API_default_deny_and_current_overrides_have_the_same_outcome()
    {
        await using var setup = await HttpAsync(grant: false); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        await PairAsync(http, browser, token, HttpStatusCode.Forbidden);
        await SeedAsync(Alpha, SalesAcl(http.UserId));
        await PairAsync(http, browser, token, HttpStatusCode.OK);
        await SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "Orders.ViewOwn", PermissionEffect.Deny), new(http.UserId, "Orders.ViewOthers", PermissionEffect.Deny)] });
        await PairAsync(http, browser, token, HttpStatusCode.Forbidden);
    }
    [Fact]
    public async Task Native_API_creates_edits_reads_and_submits_with_atomic_audit_history()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var created = await CreateAsync(http, token);
        Assert.Equal(http.UserId, created.CreatedBy); Assert.Equal(OrderStatus.Draft, created.Status);
        using var editedResponse = await ApiAsync(http, token, "/alpha/api/v1/orders/" + created.Id + "/edit", "edit-http", new { expectedRevision = created.Revision, expectedVersion = created.Version, draft = M4PolicyTests.Draft with { Amount = 72 } });
        var edited = (await ResultAsync(editedResponse, HttpStatusCode.OK)).Order;
        Assert.Equal(72, edited.Amount); Assert.Equal(2, edited.Revision);
        using var get = await ApiAsync(http, token, "/alpha/api/v1/orders/" + created.Id);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode); Assert.Equal(edited, await get.Content.ReadFromJsonAsync<OrderView>(Ct));
        using var submittedResponse = await ApiAsync(http, token, "/alpha/api/v1/orders/" + created.Id + "/submit", "submit-http", Expected(edited));
        var submitted = (await ResultAsync(submittedResponse, HttpStatusCode.OK)).Order;
        Assert.Equal(OrderStatus.Confirmed, submitted.Status); Assert.Equal(3, submitted.Revision); Assert.False(submitted.ApprovalRequired);
        await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
        var entries = await audit.Events.Where(x => x.ResourceId == created.Id).OrderBy(x => x.ResultRevision).ToArrayAsync(Ct);
        Assert.Equal(3, entries.Length);
        Assert.All(entries, x => { Assert.Equal(http.UserId, x.ActorId); Assert.False(string.IsNullOrWhiteSpace(x.CorrelationId)); Assert.Contains("After", x.ChangesJson!); });
        await using var db = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal(3, await db.Revisions.CountAsync(x => x.OrderId == created.Id, Ct));
        Assert.Equal(3, await db.Receipts.CountAsync(x => x.OrderId == created.Id, Ct));
    }
    [Fact]
    public async Task Native_web_create_requires_CSRF_encodes_content_and_uses_API_concurrency()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var fields = new Dictionary<string, string> { ["idempotencyKey"] = "web-create", ["number"] = "WEB-1", ["customerName"] = "<script>alert(1)</script>", ["amount"] = "12.50", ["currency"] = "USD", ["notes"] = "<img src=x onerror=alert(1)>" };
        using (var rejected = await browser.PostAsync("/alpha/orders/new", fields)) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var form = await browser.GetAsync("/alpha/orders/new"))
        { Assert.Equal(HttpStatusCode.OK, form.StatusCode); fields["__RequestVerificationToken"] = HttpBrowser.Csrf(await form.Content.ReadAsStringAsync(Ct)); }
        string location;
        using (var created = await browser.PostAsync("/alpha/orders/new", fields))
        { Assert.Equal(HttpStatusCode.SeeOther, created.StatusCode); location = created.Headers.Location!.OriginalString; }
        using var detail = await browser.GetAsync(location);
        var html = await detail.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode); Assert.DoesNotContain("<script>alert(1)</script>", html); Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<img src=x", html); Assert.Contains("&lt;img", html);
        var id = Guid.Parse(location.Split('/').Last());
        using var read = await ApiAsync(http, token, "/alpha/api/v1/orders/" + id);
        var original = (await read.Content.ReadFromJsonAsync<OrderView>(Ct))!;
        using (var changed = await ApiAsync(http, token, "/alpha/api/v1/orders/" + id + "/edit", "api-edit-web", new { expectedRevision = original.Revision, expectedVersion = original.Version, draft = M4PolicyTests.Draft }))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var staleFields = new Dictionary<string, string> { ["idempotencyKey"] = "web-submit", ["expectedRevision"] = original.Revision.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = original.Version, ["__RequestVerificationToken"] = HttpBrowser.Csrf(html) };
        using var stale = await browser.PostAsync(location + "/submit", staleFields);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }
    [Fact]
    public async Task Native_replay_survives_commit_but_not_revoked_action_read_or_membership()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var created = await CreateAsync(http, token); var path = "/alpha/api/v1/orders/" + created.Id + "/submit";
        using var first = await ApiAsync(http, token, path, "lost-submit", Expected(created));
        var committed = await ResultAsync(first, HttpStatusCode.OK);
        using var retry = await ApiAsync(http, token, path, "lost-submit", Expected(created));
        var replay = await ResultAsync(retry, HttpStatusCode.OK); Assert.True(replay.Replayed); Assert.Equal(committed.Order, replay.Order);
        foreach (var permission in new[] { "Orders.SubmitOwn", "Orders.ViewOwn" })
        {
            await SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, permission, PermissionEffect.Deny)] });
            using var denied = await ApiAsync(http, token, path, "lost-submit", Expected(created)); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var hiddenConflict = await ApiAsync(http, token, path, "lost-submit", new { expectedRevision = 99, expectedVersion = created.Version }); Assert.Equal(HttpStatusCode.Forbidden, hiddenConflict.StatusCode);
        }
        await SeedAsync(Alpha, SalesAcl(http.UserId));
        await using var platform = SaaS.Tenancy.Data.TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var membership = await platform.IdentityMemberships.SingleAsync(x => x.TenantId == Alpha.Id && x.GlobalUserId == http.UserId, Ct);
        membership.Active = false; await platform.SaveChangesAsync(Ct);
        using var revoked = await ApiAsync(http, token, path, "lost-submit", Expected(created));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }
    [Fact]
    public async Task Native_own_others_permissions_use_persisted_creator_and_filter_the_list()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var own = await CreateAsync(http, token);
        // Arrange another actor's persisted resource through the real application service, not SQL mutation of ownership/history.
        var actor = Guid.NewGuid();
        var service = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
            (_, _) => Task.FromResult(M4PolicyTests.Access(actor, tenant: Alpha.Id) with { Current = M4PolicyTests.Access(actor, tenant: Alpha.Id).Current with { Entitlements = new HashSet<string> { "Orders" } } }));
        var other = (await service.ExecuteAsync(new(OrderAction.Create, "other-owner", Draft: M4PolicyTests.Draft with { Number = "OTHER-1" }), Ct)).Order;
        await SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "Orders.ViewOthers", PermissionEffect.Deny)] });
        using (var denied = await ApiAsync(http, token, "/alpha/api/v1/orders/" + other.Id + "?ownerId=" + http.UserId)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await browser.GetAsync("/alpha/orders/" + other.Id)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var list = await ApiAsync(http, token, "/alpha/api/v1/orders"))
        { Assert.Equal(HttpStatusCode.OK, list.StatusCode); Assert.Equal(own, Assert.Single((await list.Content.ReadFromJsonAsync<OrderPage>(Ct))!.Items)); }
        await SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "Orders.ViewOwn", PermissionEffect.Deny)] });
        using var ownDenied = await ApiAsync(http, token, "/alpha/api/v1/orders/" + own.Id);
        Assert.Equal(HttpStatusCode.Forbidden, ownDenied.StatusCode);
    }
    [Fact]
    public async Task Native_shell_routing_and_token_binding_prevent_cross_tenant_orders()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await CreateAsync(http, token);
        using (var wrongToken = await ApiAsync(http, token, "/beta/api/v1/orders/" + order.Id)) Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        using (var absent = await ApiAsync(http, http.CapturedTokens["beta"].AccessToken, "/beta/api/v1/orders/" + order.Id)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var client = http.NewClient(); using var request = new HttpRequestMessage(HttpMethod.Get, "/alpha/api/v1/orders/" + order.Id + "?tenantId=" + http.Tenant("beta").Id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Headers.Add("X-Tenant-Id", http.Tenant("beta").Id.ToString());
        using var routed = await client.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        Assert.Equal(order, await routed.Content.ReadFromJsonAsync<OrderView>(Ct));
    }
    [Fact]
    public async Task Native_strict_commands_reject_overposting_duplicates_invalid_shapes_and_oversized_bodies()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var valid = JsonSerializer.Serialize(M4PolicyTests.Draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var invalid = new[] { valid[..^1] + ",\"approvalRequired\":false}", valid[..^1] + ",\"tenantId\":\"" + http.Tenant("beta").Id + "\"}",
            valid[..^1] + ",\"createdBy\":\"" + Guid.NewGuid() + "\"}", valid[..^1] + ",\"status\":4}", valid[..^1] + ",\"amount\":99}", "{}", new string(' ', 17000) + valid };
        foreach (var body in invalid)
        {
            using var response = await RawAsync(http, token, body, "strict-key");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var missingKey = await RawAsync(http, token, valid, null)) Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        await using var db = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha)); Assert.Empty(await db.Orders.ToArrayAsync(Ct));
    }
    [Fact]
    public async Task Native_required_authority_sources_fail_closed_before_returning_a_stored_result()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        await CreateAsync(http, token);
        foreach (var source in new[] { "issuer", "platform", "account", "membership", "acl" })
        {
            if (source == "acl") await AclReadAsync(false); else await http.SetReadFailureAsync(source, true);
            try
            {
                using var replay = await ApiAsync(http, token, "/alpha/api/v1/orders", "create-http", M4PolicyTests.Draft);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
                using var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct));
                Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("correlationId").GetString()));
                using var ui = await browser.GetAsync("/alpha/orders"); Assert.Equal(HttpStatusCode.ServiceUnavailable, ui.StatusCode); Assert.Null(ui.Headers.Location);
            }
            finally { if (source == "acl") await AclReadAsync(true); else await http.SetReadFailureAsync(source, false); }
        }
        using var healthy = await ApiAsync(http, token, "/alpha/api/v1/orders", "create-http", M4PolicyTests.Draft);
        Assert.True((await ResultAsync(healthy, HttpStatusCode.Created)).Replayed);
    }
    [Fact]
    public async Task Native_audit_failure_rolls_back_and_retry_commits_once()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, $"DENY INSERT ON [audit].[Events] TO [saas_rt_{Alpha.Id:N}]", Ct);
        try
        {
            using var failed = await ApiAsync(http, token, "/alpha/api/v1/orders", "create-http", M4PolicyTests.Draft); Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            await using var db = OrdersDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.Empty(await db.Orders.ToArrayAsync(Ct)); Assert.Empty(await db.Revisions.ToArrayAsync(Ct)); Assert.Empty(await db.Receipts.ToArrayAsync(Ct));
        }
        finally { await SqlOperations.ExecuteAsync(sql, $"GRANT INSERT ON [audit].[Events] TO [saas_rt_{Alpha.Id:N}]", Ct); }
        await CreateAsync(http, token);
        using var retry = await ApiAsync(http, token, "/alpha/api/v1/orders", "create-http", M4PolicyTests.Draft); Assert.True((await ResultAsync(retry, HttpStatusCode.Created)).Replayed);
    }
    [Fact]
    public async Task Native_approval_plan_without_handler_is_unavailable_not_standard_confirmation()
    {
        await using var setup = await HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await CreateAsync(http, token);
        using var submit = await ApiAsync(http, token, "/alpha/api/v1/orders/" + order.Id + "/submit", "approval-submit", Expected(order));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, submit.StatusCode);
        using var read = await ApiAsync(http, token, "/alpha/api/v1/orders/" + order.Id); Assert.Equal(order, await read.Content.ReadFromJsonAsync<OrderView>(Ct));
    }
    [Fact]
    public async Task Native_runtime_cannot_delete_history_rewrite_receipts_administer_SQL_or_open_approval_routes()
    {
        await using var setup = await HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await CreateAsync(http, token);
        foreach (var path in new[] { "/alpha/api/v1/orders/" + order.Id + "/approve", "/alpha/api/v1/orders/" + order.Id + "/reject", "/alpha/admin", "/alpha/api/features", "/alpha/Setup" })
        { using var response = await ApiAsync(http, token, path, "forbidden-action", Expected(order)); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); }
        await using var sql = await SqlOperations.OpenAsync(Environment.Runtime(Alpha), Ct);
        foreach (var statement in new[] { "DELETE FROM orders.Orders", "DELETE FROM orders.Revisions", "UPDATE orders.Revisions SET Revision=Revision", "UPDATE orders.Receipts SET RequestHash=RequestHash", "DELETE FROM orders.Receipts", "DELETE FROM orders.__EFMigrationsHistory", "DELETE FROM audit.Events", "CREATE TABLE dbo.M4Escalation(Id int)", "UPDATE tenancy.WriteGate SET IsOpen=1" })
            await Assert.ThrowsAsync<SqlException>(() => SqlOperations.ExecuteAsync(sql, statement, Ct));
    }
    private async Task AclReadAsync(bool allow)
    {
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await SqlOperations.ExecuteAsync(sql, $"{(allow ? "GRANT" : "DENY")} SELECT ON [access].[Roles] TO [saas_rt_{Alpha.Id:N}]", Ct);
    }
    private static object Expected(OrderView order) => new { expectedRevision = order.Revision, expectedVersion = order.Version };
    private static async Task LoginAsync(IdentityHttpFixture http, HttpBrowser browser)
    {
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var response = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static async Task PairAsync(IdentityHttpFixture http, HttpBrowser browser, string token, HttpStatusCode status)
    {
        using var api = await ApiAsync(http, token, "/alpha/api/v1/orders"); Assert.Equal(status, api.StatusCode);
        using var web = await browser.GetAsync("/alpha/orders"); Assert.Equal(status, web.StatusCode);
    }
    private static async Task<OrderView> CreateAsync(IdentityHttpFixture http, string token)
    {
        using var response = await ApiAsync(http, token, "/alpha/api/v1/orders", "create-http", M4PolicyTests.Draft);
        return (await ResultAsync(response, HttpStatusCode.Created)).Order;
    }
    private static async Task<OrderCommandResult> ResultAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderCommandResult>(Ct))!;
    }
    private static async Task<HttpResponseMessage> ApiAsync(IdentityHttpFixture http, string token, string path, string? key = null, object? body = null)
    {
        using var client = http.NewClient(); using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, Ct);
    }
    private static async Task<HttpResponseMessage> RawAsync(IdentityHttpFixture http, string token, string body, string? key)
    {
        using var client = http.NewClient(); using var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/api/v1/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, Ct);
    }
}
