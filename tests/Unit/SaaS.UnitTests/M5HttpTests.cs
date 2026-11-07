using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl;
using SaaS.OrderApprovals;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using Xunit;
using static SaaS.UnitTests.M4HttpTests;
namespace SaaS.UnitTests;

[Collection("M5 HTTP SQL")]
[Trait("Category", "SqlM5")]
public sealed class M5HttpTests(M1SqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M1SqlEnvironment Environment => fixture.Environment;
    private TestTenant Alpha => Environment.Tenants.Single(x => x.Slug == "alpha");
    private M4HttpTests Setup => new(fixture);
    private async Task<OrderView> OtherPendingAsync()
    {
        var actor = Guid.NewGuid();
        var service = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
            (_, _) => Task.FromResult(M4PolicyTests.Access(actor, tenant: Alpha.Id)), new OrderApprovalHandler());
        var draft = (await service.ExecuteAsync(new(OrderAction.Create, "other-create", Draft: M4PolicyTests.Draft), Ct)).Order;
        return (await service.ExecuteAsync(M4PolicyTests.Command(draft), Ct)).Order;
    }
    [Fact]
    public async Task Native_approval_and_replay_use_real_requests_and_current_Deny_before_conflict_disclosure()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await OtherPendingAsync(); var path = "/alpha/api/v1/orders/" + order.Id + "/approve";
        using var first = await ApiAsync(http, token, path, "real-approve", Expected(order));
        var committed = await ResultAsync(first, HttpStatusCode.OK); Assert.Equal(OrderStatus.Confirmed, committed.Order.Status);
        using var replay = await ApiAsync(http, token, path, "real-approve", Expected(order));
        Assert.True((await ResultAsync(replay, HttpStatusCode.OK)).Replayed);
        await Setup.SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "OrderApprovals.Decide", PermissionEffect.Deny)] });
        using var denied = await ApiAsync(http, token, path, "real-approve", Expected(order)); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var hiddenConflict = await ApiAsync(http, token, path, "real-approve", new { expectedRevision = 99, expectedVersion = order.Version }); Assert.Equal(HttpStatusCode.Forbidden, hiddenConflict.StatusCode);
        await using var db = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Single(await db.Requests.ToArrayAsync(Ct)); Assert.Single(await db.Decisions.ToArrayAsync(Ct));
    }
    [Fact]
    public async Task Native_web_approve_requires_CSRF_and_replays_through_API_with_identical_result()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await OtherPendingAsync(); var path = "/alpha/orders/" + order.Id;
        using var page = await browser.GetAsync(path); var html = await page.Content.ReadAsStringAsync(Ct);
        Assert.Contains(path + "/approve", html); Assert.Contains(path + "/reject", html);
        var fields = new Dictionary<string, string> { ["idempotencyKey"] = "web-real-approve", ["expectedRevision"] = order.Revision.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = order.Version };
        using (var invalid = await browser.PostAsync(path + "/approve", fields)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        fields["__RequestVerificationToken"] = HttpBrowser.Csrf(html);
        using (var result = await browser.PostAsync(path + "/approve", fields)) Assert.Equal(HttpStatusCode.SeeOther, result.StatusCode);
        using var replay = await ApiAsync(http, token, "/alpha/api/v1/orders/" + order.Id + "/approve", "web-real-approve", Expected(order));
        var committed = await ResultAsync(replay, HttpStatusCode.OK); Assert.True(committed.Replayed); Assert.Equal(OrderStatus.Confirmed, committed.Order.Status);
    }
    [Fact]
    public async Task Native_web_reject_validates_reason_and_preserves_plain_text_in_history()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser);
        var order = await OtherPendingAsync(); var path = "/alpha/orders/" + order.Id;
        using var page = await browser.GetAsync(path);
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = HttpBrowser.Csrf(await page.Content.ReadAsStringAsync(Ct)),
            ["idempotencyKey"] = "web-real-reject", ["expectedRevision"] = order.Revision.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = order.Version, ["reason"] = "   " };
        using (var invalid = await browser.PostAsync(path + "/reject", fields)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        fields["reason"] = "  <script>plain text</script>  ";
        using (var rejected = await browser.PostAsync(path + "/reject", fields)) Assert.Equal(HttpStatusCode.SeeOther, rejected.StatusCode);
        await using var db = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal("<script>plain text</script>", (await db.Decisions.SingleAsync(Ct)).Reason);
        // The authenticated non-creator may read the normalized reason, without requiring Decide on GET.
        using var read = await ApiAsync(http, http.CapturedTokens["alpha"].AccessToken, "/alpha/api/v1/orders/" + order.Id);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var current = (await read.Content.ReadFromJsonAsync<OrderView>(Ct))!;
        Assert.Equal(OrderStatus.Rejected, current.Status);
        var decision = Assert.Single(current.ApprovalDecisions!);
        Assert.Equal(order.Revision, decision.SubmittedRevision);
        Assert.Equal(current.Revision, decision.ResultRevision);
        Assert.Equal("<script>plain text</script>", decision.Reason);
        using var detail = await browser.GetAsync(path);
        var html = await detail.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("&lt;script&gt;plain text&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>plain text</script>", html);
        await Setup.SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "Orders.ViewOthers", PermissionEffect.Deny)] });
        using var deniedApi = await ApiAsync(http, http.CapturedTokens["alpha"].AccessToken, "/alpha/api/v1/orders/" + order.Id);
        using var deniedWeb = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, deniedApi.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedWeb.StatusCode);
        Assert.DoesNotContain("plain text", await deniedApi.Content.ReadAsStringAsync(Ct));
    }
    [Fact]
    public async Task Native_creator_sees_rejection_reason_after_rework_but_revoked_read_cannot_see_it()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var draft = await CreateAsync(http, token);
        using var submit = await ApiAsync(http, token, "/alpha/api/v1/orders/" + draft.Id + "/submit", "creator-submit-reason", Expected(draft));
        var pending = (await ResultAsync(submit, HttpStatusCode.OK)).Order;
        var reviewer = Guid.NewGuid();
        var service = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
            (_, _) => Task.FromResult(M4PolicyTests.Access(reviewer, tenant: Alpha.Id)), new OrderApprovalHandler());
        var rejected = (await service.ExecuteAsync(M4PolicyTests.Command(pending, OrderAction.Reject, "creator-reject-reason")
            with { Reason = "  <script>alert(1)</script> & revise  " }, Ct)).Order;
        var apiPath = "/alpha/api/v1/orders/" + draft.Id; var webPath = "/alpha/orders/" + draft.Id;
        using (var read = await ApiAsync(http, token, apiPath))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var current = (await read.Content.ReadFromJsonAsync<OrderView>(Ct))!;
            Assert.Equal(OrderStatus.Rejected, current.Status);
            var decision = Assert.Single(current.ApprovalDecisions!);
            Assert.False(decision.Approved);
            Assert.Equal(pending.Revision, decision.SubmittedRevision);
            Assert.Equal(rejected.Revision, decision.ResultRevision);
            Assert.Equal("<script>alert(1)</script> & revise", decision.Reason);
        }
        using (var page = await browser.GetAsync(webPath))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(Ct);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; revise", html);
            Assert.DoesNotContain("<script>alert(1)</script> & revise", html);
        }
        using var edit = await ApiAsync(http, token, apiPath + "/edit", "creator-fix-reason",
            new { expectedRevision = rejected.Revision, expectedVersion = rejected.Version, draft = M4PolicyTests.Draft with { Notes = "fixed" } });
        var fixedDraft = (await ResultAsync(edit, HttpStatusCode.OK)).Order;
        using var resubmit = await ApiAsync(http, token, apiPath + "/submit", "creator-resubmit-reason", Expected(fixedDraft));
        var pendingAgain = (await ResultAsync(resubmit, HttpStatusCode.OK)).Order;
        using (var history = await ApiAsync(http, token, apiPath))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            var current = (await history.Content.ReadFromJsonAsync<OrderView>(Ct))!;
            Assert.Equal(pendingAgain.Revision, current.Revision);
            Assert.Equal(OrderStatus.PendingApproval, current.Status);
            Assert.Equal(rejected.Revision, Assert.Single(current.ApprovalDecisions!).ResultRevision);
        }
        await Setup.SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "Orders.ViewOwn", PermissionEffect.Deny)] });
        using var deniedApi = await ApiAsync(http, token, apiPath);
        using var deniedWeb = await browser.GetAsync(webPath);
        Assert.Equal(HttpStatusCode.Forbidden, deniedApi.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedWeb.StatusCode);
        Assert.DoesNotContain("revise", await deniedApi.Content.ReadAsStringAsync(Ct));
    }
    [Fact]
    public async Task Native_decisions_reject_forged_identity_marker_reason_and_cross_tenant_tokens()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await OtherPendingAsync(); var path = "/alpha/api/v1/orders/" + order.Id + "/approve";
        foreach (var name in new[] { "tenantId", "actorId", "approvalRequired", "reason", "clientId" })
        {
            var body = new Dictionary<string, object> { ["expectedRevision"] = order.Revision, ["expectedVersion"] = order.Version, [name] = "forged" };
            using var invalid = await ApiAsync(http, token, path, "forged-decision", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var cross = await ApiAsync(http, token, "/beta/api/v1/orders/" + order.Id + "/approve", "cross-decision", Expected(order));
        Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
        await using var db = ApprovalsDbContext.Open(Environment.CustomerAdmin(Alpha)); Assert.Empty(await db.Decisions.ToArrayAsync(Ct));
    }
    [Fact]
    public async Task Native_self_decision_is_forbidden_and_controls_disappear_after_current_permission_Deny()
    {
        await using var setup = await Setup.HttpAsync(approval: true); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var draft = await CreateAsync(http, token);
        using var submitted = await ApiAsync(http, token, "/alpha/api/v1/orders/" + draft.Id + "/submit", "self-submit", Expected(draft));
        var own = (await ResultAsync(submitted, HttpStatusCode.OK)).Order;
        foreach (var action in new[] { "approve", "reject" })
        {
            object body = action == "approve" ? Expected(own) : new { expectedRevision = own.Revision, expectedVersion = own.Version, reason = "self" };
            using var denied = await ApiAsync(http, token, "/alpha/api/v1/orders/" + own.Id + "/" + action, "self-decision", body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var ownPage = await browser.GetAsync("/alpha/orders/" + own.Id);
        Assert.DoesNotContain("/approve\"", await ownPage.Content.ReadAsStringAsync(Ct));
        // A distinct order avoids assuming that self-approval is the only reason controls are hidden.
        var actor = Guid.NewGuid(); var svc = new OrderService(Environment.CustomerAdmin(Alpha), Alpha.Id, M4PolicyTests.Catalog,
            (_, _) => Task.FromResult(M4PolicyTests.Access(actor, tenant: Alpha.Id)), new OrderApprovalHandler());
        var other = (await svc.ExecuteAsync(new(OrderAction.Create, "deny-other-create", Draft: M4PolicyTests.Draft with { Number = "M5-OTHER" }), Ct)).Order;
        other = (await svc.ExecuteAsync(M4PolicyTests.Command(other), Ct)).Order;
        await Setup.SeedAsync(Alpha, SalesAcl(http.UserId) with { Overrides = [new(http.UserId, "OrderApprovals.Decide", PermissionEffect.Deny)] });
        using var otherPage = await browser.GetAsync("/alpha/orders/" + other.Id);
        Assert.DoesNotContain("/approve\"", await otherPage.Content.ReadAsStringAsync(Ct));
    }
    [Fact]
    public async Task Native_Standard_has_no_decision_action_or_controls_and_runtime_cannot_rewrite_approval_history()
    {
        await using var setup = await Setup.HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await LoginAsync(http, browser); var token = http.CapturedTokens["alpha"].AccessToken;
        var order = await CreateAsync(http, token);
        foreach (var action in new[] { "approve", "reject" })
        {
            using var denied = await ApiAsync(http, token, "/alpha/api/v1/orders/" + order.Id + "/" + action, "standard-decision", Expected(order));
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            using var web = await browser.PostAsync("/alpha/orders/" + order.Id + "/" + action, new Dictionary<string, string>());
            Assert.Equal(HttpStatusCode.NotFound, web.StatusCode);
        }
        using var page = await browser.GetAsync("/alpha/orders/" + order.Id);
        Assert.DoesNotContain("/approve\"", await page.Content.ReadAsStringAsync(Ct));
        await using var sql = await SqlOperations.OpenAsync(Environment.Runtime(Alpha), Ct);
        foreach (var statement in new[] { "DELETE FROM approvals.Requests", "DELETE FROM approvals.Decisions", "UPDATE approvals.Requests SET SubmittedBy=SubmittedBy", "UPDATE approvals.Decisions SET Reason=Reason", "DELETE FROM approvals.__EFMigrationsHistory", "ALTER TABLE approvals.Requests ADD Unsafe int" })
            await Assert.ThrowsAsync<SqlException>(() => SqlOperations.ExecuteAsync(sql, statement, Ct));
    }
}
