using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
using static SaaS.UnitTests.M7TenantHarness;
namespace SaaS.UnitTests;

[Collection("M7 workload SQL")]
[Trait("Category", "SqlM7")]
[Trait("M7Suite", "Workload")]
public sealed partial class M7FourTenantTests(M1SqlFixture fixture) : IClassFixture<M1SqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Four_tenant_HTTPS_matrix_rejects_cross_tenant_data_and_permission_substitution()
    {
        await using var setup = await M7TenantHarness.CreateAsync(fixture); var http = setup.Http;
        using var shared = http.NewBrowser(); await shared.LoginAsync(http);
        var orders = new Dictionary<string, OrderView>(StringComparer.Ordinal);
        foreach (var slug in Slugs)
        {
            orders[slug] = await setup.CreateOrderAsync(slug, "OVERLAPPING-NUMBER");
            using var page = await shared.FollowAsync("/" + slug + "/account/login");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }
        Assert.Equal(4, orders.Values.Select(x => x.Id).Distinct().Count());
        foreach (var slug in Slugs)
        {
            var token = http.CapturedTokens[slug].AccessToken;
            var expected = slug == "gamma" ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            using var ownApi = await SendAsync(http, token, "/" + slug + "/api/v1/orders/" + orders[slug].Id);
            using var ownUi = await shared.GetAsync("/" + slug + "/orders/" + orders[slug].Id);
            Assert.Equal(expected, ownApi.StatusCode); Assert.Equal(expected, ownUi.StatusCode);
            foreach (var other in Slugs.Where(x => x != slug))
            {
                using var wrongToken = await SendAsync(http, setup.Creators[slug].Tokens.AccessToken, "/" + other + "/api/v1/orders");
                Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
                using var wrongId = await SendAsync(http, setup.Creators[slug].Tokens.AccessToken, "/" + slug + "/api/v1/orders/" + orders[other].Id);
                Assert.Equal(HttpStatusCode.NotFound, wrongId.StatusCode);
                using var wrongUi = await shared.GetAsync("/" + slug + "/orders/" + orders[other].Id);
                Assert.True(wrongUi.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
            }
            using var forged = await SendAsync(http, setup.Creators[slug].Tokens.AccessToken, "/" + slug + "/api/v1/orders", "forged",
                new { number = "FORGED", customerName = "x", amount = 1m, currency = "USD", notes = "", tenantId = http.Tenant("alpha").Id });
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            using var read = await SendAsync(http, setup.Creators[slug].Tokens.AccessToken, "/" + slug + "/api/v1/orders?tenantId=" + http.Tenant("alpha").Id);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var items = (await read.Content.ReadFromJsonAsync<OrderPage>(Ct))!.Items;
            Assert.Equal(orders[slug].Id, Assert.Single(items).Id);
        }
        using (var gammaCreate = await SendAsync(http, http.CapturedTokens["gamma"].AccessToken, "/gamma/api/v1/orders", "shared-create", Draft("SHARED")))
            Assert.Equal(HttpStatusCode.Forbidden, gammaCreate.StatusCode);
        using (var betaCreate = await SendAsync(http, http.CapturedTokens["beta"].AccessToken, "/beta/api/v1/orders", "shared-create", Draft("SHARED")))
            Assert.Equal(HttpStatusCode.Created, betaCreate.StatusCode);
        var alphaOnly = await setup.EnrollAsync("alpha");
        using (var alpha = await HttpBrowser.ProbeAsync(http, "alpha", alphaOnly.Tokens.AccessToken)) Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
        using (var absent = await alphaOnly.Browser.FollowAsync("/beta/account/login")) Assert.True((int)absent.StatusCode is 400 or 401 or 403);
        var noMembership = await setup.EnrollAsync("alpha");
        await http.Operations.SetMembershipAsync(noMembership.Id, http.Tenant("alpha").Id, false, Ct);
        using (var absent = await noMembership.Browser.FollowAsync("/alpha/account/login")) Assert.True((int)absent.StatusCode is 400 or 401 or 403);
        var operationConnection = new SqlConnectionStringBuilder(new TenancyConnections().Platform("operator"))
        { InitialCatalog = TenancyConnections.TenantDatabase(http.Tenant("alpha").Id) }.ConnectionString;
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var sql = await SqlOperations.OpenAsync(operationConnection, Ct);
            await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM [orders].[Orders]", Ct);
        });
    }

    [Fact]
    public async Task Concurrent_four_tenant_edits_and_decisions_preserve_single_effect_and_exact_audit()
    {
        await using var setup = await M7TenantHarness.CreateAsync(fixture); var http = setup.Http;
        await Task.WhenAll(Slugs.Select(async slug =>
        {
            var creator = setup.Creators[slug].Tokens.AccessToken; var reviewer = setup.Reviewers[slug].Tokens.AccessToken;
            var order = await setup.CreateOrderAsync(slug, "CONCURRENT"); var path = "/" + slug + "/api/v1/orders/" + order.Id;
            var edits = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => SendAsync(http, creator, path + "/edit", "edit-" + i,
                new { expectedRevision = order.Revision, expectedVersion = order.Version, draft = Draft("CONCURRENT") with { Amount = 100 + i } })));
            OrderView edited;
            try
            {
                Assert.Equal(1, edits.Count(x => x.StatusCode == HttpStatusCode.OK));
                Assert.Equal(1, edits.Count(x => x.StatusCode == HttpStatusCode.Conflict));
                var winner = Array.FindIndex(edits, x => x.StatusCode == HttpStatusCode.OK);
                edited = (await M4HttpTests.ResultAsync(edits[winner], HttpStatusCode.OK)).Order;
                Assert.Equal(2, edited.Revision); Assert.Equal(100 + winner, edited.Amount);
                using var replay = await SendAsync(http, creator, path + "/edit", "edit-" + winner,
                    new { expectedRevision = order.Revision, expectedVersion = order.Version, draft = Draft("CONCURRENT") with { Amount = 100 + winner } });
                Assert.True((await M4HttpTests.ResultAsync(replay, HttpStatusCode.OK)).Replayed);
            }
            finally { foreach (var response in edits) response.Dispose(); }
            using var submitted = await SendAsync(http, creator, path + "/submit", "submit-concurrent", Expected(edited));
            var current = (await M4HttpTests.ResultAsync(submitted, HttpStatusCode.OK)).Order;
            if (slug is "beta" or "gamma")
            {
                Assert.Equal(OrderStatus.PendingApproval, current.Status);
                var decisions = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => SendAsync(http, reviewer, path + "/approve", "decision-" + i, Expected(current))));
                try
                {
                    Assert.Equal(1, decisions.Count(x => x.StatusCode == HttpStatusCode.OK));
                    Assert.Equal(1, decisions.Count(x => x.StatusCode == HttpStatusCode.Conflict));
                    var winner = Array.FindIndex(decisions, x => x.StatusCode == HttpStatusCode.OK);
                    using var replay = await SendAsync(http, reviewer, path + "/approve", "decision-" + winner, Expected(current));
                    var replayed = await M4HttpTests.ResultAsync(replay, HttpStatusCode.OK);
                    Assert.True(replayed.Replayed); Assert.Equal(OrderStatus.Confirmed, replayed.Order.Status);
                }
                finally { foreach (var response in decisions) response.Dispose(); }
                var duplicate = await setup.CreateOrderAsync(slug, "SAME-KEY");
                var duplicatePath = "/" + slug + "/api/v1/orders/" + duplicate.Id;
                using var submit = await SendAsync(http, creator, duplicatePath + "/submit", "submit-duplicate", Expected(duplicate));
                var pending = (await M4HttpTests.ResultAsync(submit, HttpStatusCode.OK)).Order;
                var repeated = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(http, reviewer, duplicatePath + "/approve", "same-decision", Expected(pending))));
                try
                {
                    var results = await Task.WhenAll(repeated.Select(x => M4HttpTests.ResultAsync(x, HttpStatusCode.OK)));
                    Assert.Single(results, x => x.Replayed);
                    Assert.Equal(results[0].Order.Id, results[1].Order.Id);
                    Assert.Equal(results[0].Order.Revision, results[1].Order.Revision);
                }
                finally { foreach (var response in repeated) response.Dispose(); }
                await using var approvals = ApprovalsDbContext.Open(setup.Environment.CustomerAdmin(http.Tenant(slug)));
                Assert.Equal(2, await approvals.Decisions.CountAsync(Ct)); Assert.Equal(2, await approvals.Requests.CountAsync(Ct));
            }
            else Assert.Equal(OrderStatus.Confirmed, current.Status);
            await AssertHistoryAsync(setup, slug, slug is "beta" or "gamma" ? 2 : 1);
        }));
        await setup.DemandFourActiveAsync();
    }

    [Fact]
    public async Task Delta_lifecycle_and_committed_revocation_do_not_affect_other_tenants_or_revive_credentials()
    {
        await using var setup = await M7TenantHarness.CreateAsync(fixture); var http = setup.Http;
        var delta = setup.Creators["delta"]; var token = delta.Tokens.AccessToken;
        var expiry = DateTimeOffset.UtcNow.AddYears(1);
        await http.Operations.SetTenantAsync(http.Tenant("delta").Id, SubscriptionStatus.Suspended, expiry, Ct);
        using (var denied = await HttpBrowser.ProbeAsync(http, "delta", token)) Assert.True((int)denied.StatusCode is 401 or 403);
        using (var other = await SendAsync(http, setup.Creators["alpha"].Tokens.AccessToken, "/alpha/api/v1/orders")) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await http.Operations.SetTenantAsync(http.Tenant("delta").Id, SubscriptionStatus.Active, expiry, Ct);
        using (var old = await HttpBrowser.ProbeAsync(http, "delta", token)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "delta", delta.Tokens.RefreshToken)) await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        var fresh = await delta.Browser.ApiGrantAsync(http, "delta");
        using (var valid = await SendAsync(http, fresh.AccessToken, "/delta/api/v1/orders")) Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var document = setup.Acls["alpha"] with { Overrides = [new(setup.Creators["alpha"].Id, "Orders.ViewOwn", PermissionEffect.Deny), new(setup.Creators["alpha"].Id, "Orders.ViewOthers", PermissionEffect.Deny)] };
        await setup.Setup.SeedAsync(http.Tenant("alpha"), document);
        using (var currentDeny = await SendAsync(http, setup.Creators["alpha"].Tokens.AccessToken, "/alpha/api/v1/orders")) Assert.Equal(HttpStatusCode.Forbidden, currentDeny.StatusCode);
        await http.SetReadFailureAsync("membership", true);
        try
        {
            using var unavailable = await SendAsync(http, fresh.AccessToken, "/delta/api/v1/orders");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally { await http.SetReadFailureAsync("membership", false); }
        using (var healthy = await SendAsync(http, fresh.AccessToken, "/delta/api/v1/orders")) Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    internal static async Task AssertHistoryAsync(M7TenantHarness setup, string slug, int expectedOrders)
    {
        var connection = setup.Environment.CustomerAdmin(setup.Http.Tenant(slug));
        await using var database = OrdersDbContext.Open(connection);
        var rows = await database.Orders.AsNoTracking().ToArrayAsync(Ct);
        Assert.Equal(expectedOrders, rows.Length);
        Assert.All(rows, row => Assert.Equal(OrderStatus.Confirmed, row.Status));
        Assert.Equal(rows.Length, rows.Select(x => x.Number).Distinct(StringComparer.Ordinal).Count());
        var history = await database.Revisions.AsNoTracking().ToArrayAsync(Ct);
        foreach (var row in rows)
        {
            var versions = history.Where(x => x.OrderId == row.Id).OrderBy(x => x.Revision).ToArray();
            Assert.Equal(row.Revision, versions.LongLength);
            Assert.Equal(Enumerable.Range(1, versions.Length).Select(x => (long)x), versions.Select(x => x.Revision));
        }
        Assert.Equal(history.Length, await database.Receipts.CountAsync(Ct));
        await using var sql = await SqlOperations.OpenAsync(connection, Ct);
        Assert.Equal(history.Length, Convert.ToInt32(await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM [audit].[Events] WHERE [Action] LIKE 'Orders.%'", Ct)));
    }
}
[CollectionDefinition("M7 workload SQL", DisableParallelization = true)]
public sealed class M7WorkloadSqlCollection;
