using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Auditing;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Tenancy.Data;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M7 SQL")]
public sealed partial class M7SqlAcceptanceTests(M7SqlFixture fixture) : IClassFixture<M7SqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "SqlM7")]
    public async Task Four_active_tenants_isolate_same_number_data_and_all_cross_token_routes()
    {
        await fixture.DemandMatrixAsync(Ct);
        var number = "M7-MATRIX-" + Guid.NewGuid().ToString("N")[..16];
        var orders = new Dictionary<string, OrderView>(); var tokens = new Dictionary<string, string>();
        foreach (var tenant in fixture.Environment.Tenants)
        {
            var token = (await fixture.GrantAsync(fixture.Creators[tenant.Slug][0], tenant.Slug)).AccessToken;
            tokens[tenant.Slug] = token;
            orders[tenant.Slug] = await M7Http.CreateAsync(fixture, token, tenant.Slug,
                new(number, "Customer " + tenant.Slug, 42m, "USD", "Isolated " + tenant.Slug), Ct);
        }
        foreach (var source in fixture.Environment.Tenants)
        {
            // RED: every current ACL authority read must retain both fresh
            // platform/tenant and customer profile checks, but use one
            // application-stage login after platform validation.
            using var stageObserver = new M7StageProfile();
            using var sqlObserver = new M7SqlProfile();
            // The customer DB identity must be read by SQL before any Orchard
            // snapshot, but only ONCE per fresh routing decision. Retain the
            // same real signed-bearer, tenant isolation and ACL checks.
            using var customerIdentity = new M7RoutingIdentityCounter(source.Id);
            using var own = await M7Http.SendAsync(fixture, tokens[source.Slug], source.Slug, "/" + orders[source.Slug].Id, ct: Ct);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var actual = await own.Content.ReadFromJsonAsync<OrderView>(Ct);
            Assert.NotNull(actual); Assert.Equal("Customer " + source.Slug, actual.CustomerName);
            Assert.Equal(1, customerIdentity.Count);
            var aclStages = stageObserver.Snapshot().Stages.Single(x => x.Stage == "acl.authorize");
            var aclSql = sqlObserver.SnapshotStages().Single(x => x.Stage == "acl.authorize");
            Assert.True(aclStages.Count > 0, "A real authority source recheck is required.");
            // A fresh profile read still executes on the caller-owned Orders
            // transaction, not on a second physical customer login.
            Assert.Equal(0, aclSql.Open.Count);
            Assert.True(aclSql.Command.Count >= aclStages.Count * 3,
                "Five current migration histories, approval columns and plan profile must still be queried.");
            Assert.Equal(0, aclSql.Open.Errors);
            Assert.Equal(0, sqlObserver.Snapshot().Pending);
            foreach (var target in fixture.Environment.Tenants.Where(x => x.Id != source.Id))
            {
                using var wrongToken = await M7Http.SendAsync(fixture, tokens[source.Slug], target.Slug, "/" + orders[target.Slug].Id, ct: Ct);
                Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
                using var foreignId = await M7Http.SendAsync(fixture, tokens[target.Slug], target.Slug, "/" + orders[source.Slug].Id, ct: Ct);
                Assert.Equal(HttpStatusCode.NotFound, foreignId.StatusCode);
            }
            using var overpost = await M7Http.SendAsync(fixture, tokens[source.Slug], source.Slug, body: new
            {
                number = "forged", customerName = "forged", amount = 42, currency = "USD", notes = "forged", tenantId = fixture.Tenant("beta").Id
            }, ct: Ct);
            Assert.Equal(HttpStatusCode.BadRequest, overpost.StatusCode);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alpha/api/v1/orders/" + orders["alpha"].Id + "?tenantId=" + fixture.Tenant("beta").Id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens["alpha"]);
        request.Headers.Add("X-Tenant-Id", fixture.Tenant("beta").Id.ToString("D"));
        using var forgedHeader = await fixture.Api.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, forgedHeader.StatusCode);
        Assert.Equal(orders["alpha"].Id, (await forgedHeader.Content.ReadFromJsonAsync<OrderView>(Ct))!.Id);
        M7Evidence.Write("matrix.json", new
        {
            schemaVersion = 1, sourceSha = M7Evidence.SourceSha, status = "PASS", transport = "Kestrel HTTPS", database = "SQL Server",
            tenants = fixture.Environment.Tenants.Select(x => new { slug = x.Slug, plan = x.Slug is "beta" or "gamma" ? "Approval" : "Standard" }),
            crossTokenCases = 12, foreignResourceCases = 12, forgedBodyCases = 4, allActive = true
        });
    }

    [Fact]
    [Trait("Category", "SqlM7")]
    public async Task Shared_user_roles_deny_and_missing_memberships_remain_tenant_specific()
    {
        var alphaCreator = (await fixture.GrantAsync(fixture.Creators["alpha"][0], "alpha")).AccessToken;
        var betaCreator = (await fixture.GrantAsync(fixture.Creators["beta"][0], "beta")).AccessToken;
        var alphaOrder = await M7Http.CreateAsync(fixture, alphaCreator, "alpha", null, Ct);
        var betaOrder = await M7Http.CreateAsync(fixture, betaCreator, "beta", null, Ct);
        var alphaShared = (await fixture.GrantAsync(fixture.Shared, "alpha")).AccessToken;
        var betaShared = (await fixture.GrantAsync(fixture.Shared, "beta")).AccessToken;
        var gammaShared = (await fixture.GrantAsync(fixture.Shared, "gamma")).AccessToken;
        using (var allowed = await M7Http.SendAsync(fixture, alphaShared, "alpha", "/" + alphaOrder.Id, ct: Ct)) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using (var denied = await M7Http.SendAsync(fixture, betaShared, "beta", "/" + betaOrder.Id, ct: Ct)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await M7Http.SendAsync(fixture, gammaShared, "gamma", body: M7Http.Draft(), ct: Ct)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var browser = fixture.Browser())
        {
            await browser.LoginAsync(fixture.Shared);
            using (var login = await browser.FollowAsync("/gamma/account/login")) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var denied = await browser.GetAsync("/gamma/orders/new");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        foreach (var actor in new[] { fixture.AlphaOnly, fixture.NoMember })
        {
            using var browser = fixture.Browser(); await browser.LoginAsync(actor);
            using var denied = await browser.FollowAsync(actor == fixture.AlphaOnly ? "/beta/account/login" : "/alpha/account/login");
            Assert.True((int)denied.StatusCode is 400 or 401 or 403, "Missing membership must never create a tenant session or cause a server error.");
        }
        var standard = await M7Http.CreateAsync(fixture, alphaCreator, "alpha", null, Ct);
        using var disabled = await M7Http.SendAsync(fixture, alphaCreator, "alpha", "/" + standard.Id + "/approve", M7Http.Revision(standard), ct: Ct);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlM7")]
    public async Task Native_UI_two_tabs_keep_prefix_data_and_policy_consistent_with_API()
    {
        var alpha = (await fixture.GrantAsync(fixture.Shared, "alpha")).AccessToken;
        var beta = (await fixture.GrantAsync(fixture.Shared, "beta")).AccessToken;
        var a = await M7Http.CreateAsync(fixture, alpha, "alpha", M7Http.Draft("ALPHA-TAB"), Ct);
        var b = await M7Http.CreateAsync(fixture, beta, "beta", M7Http.Draft("BETA-TAB"), Ct);
        using var browser = fixture.Browser(); await browser.LoginAsync(fixture.Shared);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var login = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        for (var i = 0; i < 3; i++)
        {
            var pages = await Task.WhenAll(browser.GetAsync("/alpha/orders/" + a.Id), browser.GetAsync("/beta/orders/" + b.Id));
            try
            {
                Assert.All(pages, page => Assert.Equal(HttpStatusCode.OK, page.StatusCode));
                var htmlA = await pages[0].Content.ReadAsStringAsync(Ct); var htmlB = await pages[1].Content.ReadAsStringAsync(Ct);
                Assert.Contains(a.Number, htmlA); Assert.DoesNotContain(b.Number, htmlA);
                Assert.Contains(b.Number, htmlB); Assert.DoesNotContain(a.Number, htmlB);
                Assert.Contains("/alpha/orders", htmlA); Assert.Contains("/beta/orders", htmlB);
            }
            finally { foreach (var page in pages) page.Dispose(); }
        }
        using var foreign = await browser.GetAsync("/beta/orders/" + a.Id);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlM7")]
    public async Task Concurrent_updates_and_approvals_have_one_winner_and_replay_one_audit()
    {
        foreach (var slug in new[] { "alpha", "beta", "gamma", "delta" })
        {
            var token = (await fixture.GrantAsync(fixture.Creators[slug][0], slug)).AccessToken;
            var draft = M7Http.Draft(); var order = await M7Http.CreateAsync(fixture, token, slug, draft, Ct);
            var editBody = new { expectedRevision = order.Revision, expectedVersion = order.Version, draft = draft with { Amount = 43m } };
            var edits = await Task.WhenAll(M7Http.SendAsync(fixture, token, slug, "/" + order.Id + "/edit", editBody, ct: Ct),
                M7Http.SendAsync(fixture, token, slug, "/" + order.Id + "/edit", editBody, ct: Ct));
            try
            {
                Assert.Equal(new[] { 200, 409 }, edits.Select(x => (int)x.StatusCode).Order().ToArray());
                order = (await M7Http.ResultAsync(edits.Single(x => x.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK, Ct)).Order;
            }
            finally { foreach (var response in edits) response.Dispose(); }
            using (var submit = await M7Http.SendAsync(fixture, token, slug, "/" + order.Id + "/submit", M7Http.Revision(order), ct: Ct))
                order = (await M7Http.ResultAsync(submit, HttpStatusCode.OK, Ct)).Order;
            Assert.Equal(slug is "beta" or "gamma" ? OrderStatus.PendingApproval : OrderStatus.Confirmed, order.Status);
            if (slug is not ("beta" or "gamma")) continue;
            var reviewers = new[] { (await fixture.GrantAsync(fixture.Reviewers[0], slug)).AccessToken, (await fixture.GrantAsync(fixture.Reviewers[1], slug)).AccessToken };
            var keys = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
            var original = M7Http.Revision(order);
            var decisions = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => M7Http.SendAsync(fixture, reviewers[i], slug, "/" + order.Id + "/approve", original, keys[i], Ct)));
            int winner;
            try
            {
                Assert.Equal(new[] { 200, 409 }, decisions.Select(x => (int)x.StatusCode).Order().ToArray());
                winner = Array.FindIndex(decisions, x => x.StatusCode == HttpStatusCode.OK);
                Assert.Equal(OrderStatus.Confirmed, (await M7Http.ResultAsync(decisions[winner], HttpStatusCode.OK, Ct)).Order.Status);
            }
            finally { foreach (var response in decisions) response.Dispose(); }
            using var replay = await M7Http.SendAsync(fixture, reviewers[winner], slug, "/" + order.Id + "/approve", original, keys[winner], Ct);
            Assert.True((await M7Http.ResultAsync(replay, HttpStatusCode.OK, Ct)).Replayed);
            using var stale = await M7Http.SendAsync(fixture, reviewers[winner], slug, "/" + order.Id + "/approve", original, ct: Ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await using var approvals = ApprovalsDbContext.Open(fixture.Environment.CustomerAdmin(fixture.Tenant(slug)));
            await using var audit = AuditDbContext.Open(fixture.Environment.CustomerAdmin(fixture.Tenant(slug)));
            Assert.Equal(1, await approvals.Decisions.CountAsync(x => x.OrderId == order.Id, Ct));
            Assert.Equal(1, await audit.Events.CountAsync(x => x.ResourceId == order.Id && x.Action == "Orders.Approve", Ct));
            Assert.Equal(1, await audit.Events.CountAsync(x => x.ResourceId == order.Id && x.Action == "Orders.Edit", Ct));
        }
    }

    [Fact]
    [Trait("Category", "SqlM7")]
    public async Task Delta_lifecycle_is_separate_and_does_not_revoke_other_tenants()
    {
        var delta = fixture.Tenant("delta");
        var alpha = (await fixture.GrantAsync(fixture.Shared, "alpha")).AccessToken;
        var token = (await fixture.GrantAsync(fixture.Shared, "delta")).AccessToken;
        await using var db = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
        var original = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == delta.Id, Ct);
        try
        {
            await fixture.Operations.SetTenantAsync(delta.Id, SubscriptionStatus.Suspended, original.ExpiresAt, Ct);
            using var denied = await M7Http.SendAsync(fixture, token, "delta", ct: Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var unaffected = await M7Http.SendAsync(fixture, alpha, "alpha", ct: Ct);
            Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
        }
        finally { await fixture.Operations.SetTenantAsync(delta.Id, original.Subscription, original.ExpiresAt, CancellationToken.None); }
        using (var stale = await M7Http.SendAsync(fixture, token, "delta", ct: Ct)) Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        var fresh = (await fixture.GrantAsync(fixture.Shared, "delta")).AccessToken;
        using (var restored = await M7Http.SendAsync(fixture, fresh, "delta", ct: Ct)) Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.True(await db.Audit.AnyAsync(x => x.TenantId == delta.Id && x.Action == "IdentityTenantLifecycle" && x.Actor == "saas_platform_operator", Ct));
        await fixture.DemandMatrixAsync(Ct);
    }
}

internal static class M7Evidence
{
    internal static string SourceSha
    {
        get
        {
            var value = System.Environment.GetEnvironmentVariable("SAAS_M7_SOURCE_SHA");
            if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{40}$")) throw new InvalidOperationException("BLOCKED: exact M7 source SHA required.");
            return value;
        }
    }
    internal static void Write(string name, object report)
    {
        var directory = System.Environment.GetEnvironmentVariable("SAAS_M7_EVIDENCE_DIRECTORY") ?? throw new InvalidOperationException("BLOCKED: M7 evidence directory required.");
        if (!Path.IsPathFullyQualified(directory) || Path.GetFileName(name) != name) throw new InvalidDataException("Invalid evidence location.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
