using System.Net;
using Microsoft.IdentityModel.JsonWebTokens;
using SaaS.Identity;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Refresh_is_bound_to_its_tenant_and_rotates_once()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var alpha = await browser.ApiGrantAsync(http, "alpha");
        using (var wrongClient = await HttpBrowser.RefreshAsync(http, "beta", alpha.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(wrongClient, "invalid_grant");
        using (var wrongTenant = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken, http.Tenant("beta").Id))
            await HttpBrowser.AssertOAuthErrorAsync(wrongTenant, "invalid_grant");
        // Invalid client/tenant selection must not consume the genuine credential.
        using var response = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken);
        var rotated = await HttpBrowser.TokensAsync(response);
        Assert.True(rotated.RefreshToken != alpha.RefreshToken, "A successful refresh must rotate its credential.");
        Assert.True(new JsonWebToken(rotated.AccessToken).GetClaim(IdentityNames.Tenant).Value == http.Tenant("alpha").Id.ToString("D"));
        using (var own = await HttpBrowser.ProbeAsync(http, "alpha", rotated.AccessToken)) Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var other = await HttpBrowser.ProbeAsync(http, "beta", rotated.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task Refresh_reuse_revokes_only_its_family_and_related_session()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var page = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using (var page = await browser.FollowAsync("/beta/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        var independentAlpha = await browser.ApiGrantAsync(http, "alpha");
        using var response = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken);
        var rotated = await HttpBrowser.TokensAsync(response);
        using (var positive = await HttpBrowser.ProbeAsync(http, "alpha", rotated.AccessToken)) Assert.Equal(HttpStatusCode.OK, positive.StatusCode);
        using (var replay = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(replay, "invalid_grant");
        using (var child = await HttpBrowser.ProbeAsync(http, "alpha", rotated.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, child.StatusCode);
        using (var web = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
        using (var independent = await HttpBrowser.ProbeAsync(http, "alpha", independentAlpha.AccessToken)) Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        using (var otherApi = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, otherApi.StatusCode);
        using (var otherWeb = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
        using (var childRefresh = await HttpBrowser.RefreshAsync(http, "alpha", rotated.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(childRefresh, "invalid_grant");
    }

    [Fact]
    public async Task Concurrent_refresh_cannot_leave_two_valid_chains()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var alpha = await browser.ApiGrantAsync(http, "alpha");
        var beta = await browser.ApiGrantAsync(http, "beta");
        var responses = await Task.WhenAll(
            HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken),
            HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken));
        try
        {
            Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.BadRequest));
            var winner = await HttpBrowser.TokensAsync(responses.Single(x => x.StatusCode == HttpStatusCode.OK));
            await HttpBrowser.AssertOAuthErrorAsync(responses.Single(x => x.StatusCode == HttpStatusCode.BadRequest), "invalid_grant");
            // One successful response may precede reuse, but neither old nor child
            // may authorize after both requests have completed their committed work.
            using (var old = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            using (var child = await HttpBrowser.ProbeAsync(http, "alpha", winner.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, child.StatusCode);
            using (var followup = await HttpBrowser.RefreshAsync(http, "alpha", winner.RefreshToken))
                await HttpBrowser.AssertOAuthErrorAsync(followup, "invalid_grant");
            using (var other = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Lost_refresh_response_then_reuse_requires_new_login()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var page = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        // Deliberately discard the successful response without reading credentials.
        using (var lost = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken)) Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        using (var retry = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(retry, "invalid_grant");
        using (var old = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var oldWeb = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, oldWeb.StatusCode);
        using (var newLogin = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        var newCredentials = http.CapturedTokens["alpha"];
        Assert.True(newCredentials.RefreshToken != alpha.RefreshToken, "Recovery requires a fresh authorization, not replay.");
        using var fresh = await HttpBrowser.ProbeAsync(http, "alpha", newCredentials.AccessToken);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }
}
