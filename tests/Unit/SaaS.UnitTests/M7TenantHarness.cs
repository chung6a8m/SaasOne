using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.Orders;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Web.Access;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

// Test-only orchestration. Maintenance and native credentials are real; fixture admin
// seeds reviewed ACLs but never substitutes an HTTP authorization result.
internal sealed class M7TenantHarness : IAsyncDisposable
{
    public M1SqlEnvironment Environment { get; }
    public M4HttpTests Setup { get; }
    private readonly M4HttpTests.HttpHarness _host;
    private readonly List<M7Actor> _actors = [];
    public IdentityHttpFixture Http => _host.Http;
    public Dictionary<string, M7Actor> Creators { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, M7Actor> Reviewers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AclDocument> Acls { get; } = new(StringComparer.Ordinal);
    public static readonly string[] Slugs = ["alpha", "beta", "gamma", "delta"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private M7TenantHarness(M1SqlFixture fixture, M4HttpTests setup, M4HttpTests.HttpHarness host)
    { Environment = fixture.Environment; Setup = setup; _host = host; }

    public static async Task<M7TenantHarness> CreateAsync(M1SqlFixture fixture)
    {
        var setup = new M4HttpTests(fixture);
        var result = new M7TenantHarness(fixture, setup, await setup.HttpAsync(grant: false, network: true));
        try
        {
            Assert.True(result.Http.NetworkPort > 0, "Load must traverse a bound Kestrel HTTPS listener.");
            foreach (var slug in new[] { "gamma", "delta" })
                await result.Http.Operations.SetMembershipAsync(result.Http.UserId, result.Http.Tenant(slug).Id, true, Ct);
            foreach (var slug in new[] { "beta", "gamma" })
            {
                await using var db = TenancyConnections.PlatformContext(result.Environment.PlatformAdmin);
                var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == result.Http.Tenant(slug).Id, Ct);
                await new AccessMaintenance(new()).Operations.ChangePlanAsync(tenant.Id, Guid.NewGuid(), tenant.RegistryVersion, "Approval", Ct);
            }
            foreach (var slug in Slugs)
            {
                var creator = await result.EnrollAsync(slug);
                var reviewer = await result.EnrollAsync(slug);
                result.Creators.Add(slug, creator); result.Reviewers.Add(slug, reviewer);
                var full = Guid.NewGuid(); var reader = Guid.NewGuid();
                var document = new AclDocument(
                    [new(full, "Tenant sales and reviewer", OrderPermissions.Definitions.Select(x => new PermissionGrant(x.Code, PermissionEffect.Allow)).ToArray()),
                     new(reader, "Tenant reader", [new("Orders.ViewOwn", PermissionEffect.Allow), new("Orders.ViewOthers", PermissionEffect.Allow)])],
                    [new(creator.Id, full), new(reviewer.Id, full), new(result.Http.UserId, slug == "gamma" ? reader : full)],
                    slug == "gamma" ? [new(result.Http.UserId, "Orders.ViewOthers", PermissionEffect.Deny)] : []);
                await setup.SeedAsync(result.Http.Tenant(slug), document);
                result.Acls.Add(slug, document);
            }
            await result.DemandFourActiveAsync();
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    public async Task<M7Actor> EnrollAsync(string slug)
    {
        var name = "m7" + Guid.NewGuid().ToString("N");
        var credential = "Aa1!" + Guid.NewGuid().ToString("N");
        var id = await new IdentityInstaller(new TenancyConnections(), Http.Deployment).EnrollUserAsync(
            Environment.InstallationId, name, name + "@example.invalid", credential, [Http.Tenant(slug).Id], Ct);
        var browser = Http.NewBrowser();
        try
        {
            using var form = await browser.GetAsync("/account/login");
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            using var login = await browser.PostAsync("/account/login", new Dictionary<string, string>
            {
                ["username"] = name, ["password"] = credential, ["returnUrl"] = "/account/tenants",
                ["__RequestVerificationToken"] = HttpBrowser.Csrf(await form.Content.ReadAsStringAsync(Ct))
            });
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var actor = new M7Actor(id, browser, await browser.ApiGrantAsync(Http, slug));
            _actors.Add(actor);
            return actor;
        }
        catch { browser.Dispose(); throw; }
    }
    public async Task DemandFourActiveAsync()
    {
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var ids = Environment.Tenants.Select(x => x.Id).ToArray();
        var tenants = await db.Tenants.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(Ct);
        Assert.Equal(4, tenants.Length);
        foreach (var tenant in tenants)
        {
            Assert.True(TenantPolicy.IsOperational(tenant.Status(), DateTimeOffset.UtcNow), "All four tenants must remain operational during baseline.");
            Assert.Equal(tenant.Slug is "beta" or "gamma" ? "Approval" : "Standard", tenant.Plan);
        }
    }
    public static OrderDraft Draft(string number) => new(number, "M7 customer", 25m, "USD", "M7 structured test data");
    public static object Expected(OrderView order) => new { expectedRevision = order.Revision, expectedVersion = order.Version };
    public async Task<OrderView> CreateOrderAsync(string slug, string number)
    {
        using var response = await SendAsync(Http, Creators[slug].Tokens.AccessToken, "/" + slug + "/api/v1/orders", "create-" + number, Draft(number));
        return (await M4HttpTests.ResultAsync(response, HttpStatusCode.Created)).Order;
    }
    public static async Task<HttpResponseMessage> SendAsync(IdentityHttpFixture http, string token, string path, string? key = null, object? body = null)
    {
        using var client = http.NewClient();
        return await SendAsync(client, token, path, key, body);
    }
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path, string? key = null, object? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, Ct);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var actor in _actors) actor.Browser.Dispose();
        await _host.DisposeAsync();
    }
}
internal sealed class M7Actor(Guid id, HttpBrowser browser, OAuthTokens tokens)
{
    public Guid Id { get; } = id;
    public HttpBrowser Browser { get; } = browser;
    public OAuthTokens Tokens { get; } = tokens;
    public override string ToString() => "M7 native actor (credentials redacted)";
}
