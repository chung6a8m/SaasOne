using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Membership_remove_readd_never_revives_old_credentials()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        await http.Operations.SetMembershipAsync(http.UserId, http.Tenant("alpha").Id, false, TestContext.Current.CancellationToken);
        using (var removed = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.True((int)removed.StatusCode is 401 or 403);
        using (var removedWeb = await browser.GetAsync("/alpha/account")) Assert.True((int)removedWeb.StatusCode is 401 or 403);
        using (var independent = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        await http.Operations.SetMembershipAsync(http.UserId, http.Tenant("alpha").Id, true, TestContext.Current.CancellationToken);
        using (var old = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var oldWeb = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, oldWeb.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken)) await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        using (var freshPage = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, freshPage.StatusCode);
        using var fresh = await HttpBrowser.ProbeAsync(http, "alpha", http.CapturedTokens["alpha"].AccessToken);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task Suspend_reopen_and_expiry_extension_never_revive_old_credentials()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        var expiry = DateTimeOffset.UtcNow.AddYears(1);
        await http.Operations.SetTenantAsync(http.Tenant("beta").Id, SubscriptionStatus.Suspended, expiry, TestContext.Current.CancellationToken);
        using (var suspended = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.True((int)suspended.StatusCode is 401 or 403);
        using (var other = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await http.Operations.SetTenantAsync(http.Tenant("beta").Id, SubscriptionStatus.Active, expiry, TestContext.Current.CancellationToken);
        using (var old = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var oldWeb = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.Unauthorized, oldWeb.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "beta", beta.RefreshToken)) await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        using (var fresh = await browser.FollowAsync("/beta/account/login")) Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        await http.Operations.SetMembershipAsync(http.UserId, http.Tenant("delta").Id, true, TestContext.Current.CancellationToken);
        await http.Operations.SetTenantAsync(http.Tenant("delta").Id, SubscriptionStatus.Active, http.Clock.GetUtcNow().AddMinutes(2), TestContext.Current.CancellationToken);
        using (var page = await browser.FollowAsync("/delta/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var delta = http.CapturedTokens["delta"];
        using (var positive = await HttpBrowser.ProbeAsync(http, "delta", delta.AccessToken)) Assert.Equal(HttpStatusCode.OK, positive.StatusCode);
        http.Clock.Advance(TimeSpan.FromMinutes(3));
        using (var expired = await HttpBrowser.ProbeAsync(http, "delta", delta.AccessToken)) Assert.True((int)expired.StatusCode is 401 or 403);
        await http.Operations.SetTenantAsync(http.Tenant("delta").Id, SubscriptionStatus.Active, http.Clock.GetUtcNow().AddDays(10), TestContext.Current.CancellationToken);
        using (var renewedOld = await HttpBrowser.ProbeAsync(http, "delta", delta.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, renewedOld.StatusCode);
        using (var renewedWeb = await browser.GetAsync("/delta/account")) Assert.Equal(HttpStatusCode.Unauthorized, renewedWeb.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "delta", delta.RefreshToken)) await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        using var renewedLogin = await browser.FollowAsync("/delta/account/login");
        Assert.Equal(HttpStatusCode.OK, renewedLogin.StatusCode);
    }

    [Fact]
    public async Task Tenant_logout_preserves_other_tenants_and_global_logout_revokes_all()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        using (var logout = await browser.LogoutAsync("/alpha/account", "/alpha/account/logout")) Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using (var old = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var oldWeb = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, oldWeb.StatusCode);
        using (var other = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using (var otherWeb = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
        using (var issuer = await browser.GetAsync("/account/tenants")) Assert.Equal(HttpStatusCode.OK, issuer.StatusCode);
        using (var logout = await browser.LogoutAsync("/account/tenants", "/account/logout")) Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using (var other = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        using (var otherWeb = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.Unauthorized, otherWeb.StatusCode);
        using (var issuer = await browser.GetAsync("/account/tenants")) Assert.Equal(HttpStatusCode.Unauthorized, issuer.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "beta", beta.RefreshToken)) await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        await browser.LoginAsync(http);
        using var newSession = await browser.FollowAsync("/beta/account/login");
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
    }

    [Fact]
    public async Task Required_source_failure_returns_dependency_unavailable_without_cached_allow()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var page = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        foreach (var source in new[] { "issuer", "platform", "account", "membership" })
        {
            using (var before = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            await http.SetReadFailureAsync(source, true);
            try
            {
                using var api = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
                using var json = JsonDocument.Parse(await api.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.True(json.RootElement.GetProperty("correlationId").GetString()?.Length > 0);
                using var page = await browser.GetAsync("/alpha/account");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
                Assert.Null(page.Headers.Location);
                using var refresh = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
                await HttpBrowser.AssertOAuthErrorAsync(refresh, "temporarily_unavailable");
            }
            finally { await http.SetReadFailureAsync(source, false); }
            using var after = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken);
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
    }

    [Fact]
    public async Task Restart_preserves_revocation_and_persisted_key_validation()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        var authorizationId = new JsonWebToken(alpha.AccessToken).GetClaim(IdentityNames.Authorization).Value;
        await http.InDefaultAsync(async provider =>
        {
            var manager = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await manager.FindByIdAsync(authorizationId);
            Assert.NotNull(authorization);
            Assert.True(await manager.TryRevokeAsync(authorization));
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
        await http.RestartAsync();
        browser.Rebind(http.NewClient());
        using (var revoked = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using (var revokedWeb = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, revokedWeb.StatusCode);
        using (var surviving = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
        using (var survivingWeb = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, survivingWeb.StatusCode);
        using var refresh = await HttpBrowser.RefreshAsync(http, "beta", beta.RefreshToken);
        var next = await HttpBrowser.TokensAsync(refresh);
        using var nextProbe = await HttpBrowser.ProbeAsync(http, "beta", next.AccessToken);
        Assert.Equal(HttpStatusCode.OK, nextProbe.StatusCode);
    }
}
