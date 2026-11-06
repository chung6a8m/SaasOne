using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using SaaS.Identity;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task One_central_login_signs_into_Alpha_and_Beta_with_independent_cookies()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
        {
            using var account = await browser.FollowAsync("/" + slug + "/account/login");
            Assert.Equal(HttpStatusCode.OK, account.StatusCode);
            var html = await account.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(http.UserId.ToString("D"), html);
            Assert.Contains(http.Tenant(slug).Id.ToString("D"), html);
            Assert.True(http.CapturedTokens.ContainsKey(slug), "Tenant client must redeem a real authorization code.");
        }
        Assert.Equal(1, browser.PasswordSubmissions);
        foreach (var slug in new[] { "alpha", "beta" })
        {
            var id = http.Tenant(slug).Id.ToString("D");
            Assert.Contains(browser.SetCookies, value => value.StartsWith("__Secure-SaaS." + id + "=", StringComparison.Ordinal)
                && value.Contains("path=/" + slug + "/", StringComparison.OrdinalIgnoreCase)
                && value.Contains("secure", StringComparison.OrdinalIgnoreCase) && value.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains(browser.SetCookies, value => value.StartsWith("__Secure-SaaS.Issuer=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_membership_cannot_authorize_Gamma()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using var choices = await browser.GetAsync("/account/tenants");
        var html = await choices.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("/alpha/account/login", html);
        Assert.Contains("/beta/account/login", html);
        Assert.DoesNotContain("/gamma/account/login", html);
        using var response = await browser.FollowAsync("/gamma/account/login");
        Assert.True((int)response.StatusCode is 400 or 401 or 403, "Missing membership must not create a tenant session.");
        Assert.False(http.CapturedTokens.ContainsKey("gamma"));
    }

    [Fact]
    public async Task Alpha_access_token_cannot_call_Beta_even_with_both_memberships()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var alpha = await browser.ApiGrantAsync(http, "alpha");
        var beta = await browser.ApiGrantAsync(http, "beta");
        using var alphaOk = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken);
        using var betaOk = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken);
        Assert.Equal(HttpStatusCode.OK, alphaOk.StatusCode);
        Assert.Equal(HttpStatusCode.OK, betaOk.StatusCode);
        using var swapped = await HttpBrowser.ProbeAsync(http, "beta", alpha.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, swapped.StatusCode);
        using var document = JsonDocument.Parse(await alphaOk.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(http.Tenant("alpha").Id, document.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(http.UserId, document.RootElement.GetProperty("globalUserId").GetGuid());
    }

    [Fact]
    public async Task Issuer_store_revocation_blocks_the_next_API_and_web_request()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var alphaPage = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, alphaPage.StatusCode);
        using (var betaPage = await browser.FollowAsync("/beta/account/login")) Assert.Equal(HttpStatusCode.OK, betaPage.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        using (var before = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var authorizationId = new JsonWebToken(alpha.AccessToken).GetClaim(IdentityNames.Authorization).Value;
        await http.InDefaultAsync(async provider =>
        {
            var manager = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await manager.FindByIdAsync(authorizationId);
            Assert.NotNull(authorization);
            Assert.True(await manager.TryRevokeAsync(authorization));
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
        using (var after = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using (var webAfter = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, webAfter.StatusCode);
        using (var otherApi = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, otherApi.StatusCode);
        using (var otherWeb = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
    }
}
