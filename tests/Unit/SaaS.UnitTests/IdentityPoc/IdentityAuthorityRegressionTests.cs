using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OrchardCore.Environment.Shell;
using OrchardCore.Users;
using SaaS.Identity;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Explicit_clock_is_shared_by_the_authority_in_every_shell()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        // Orchard initializes routing lazily on the first HTTP request; this direct
        // DI contract test must explicitly load settings before requesting named scopes.
        await http.Shells.InitializeAsync();
        foreach (var name in new[] { "Default", "alpha", "beta", "gamma", "delta" })
        {
            var scope = await http.Shells.GetScopeAsync(name);
            await scope.UsingServiceScopeAsync(current =>
            {
                Assert.Same(http.Clock, current.ServiceProvider.GetRequiredService<TimeProvider>());
                if (name == "Default")
                    Assert.IsType<NativeLockoutSignInManager>(current.ServiceProvider.GetRequiredService<SignInManager<IUser>>());
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task Normal_access_expiry_does_not_shorten_the_independent_web_session()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var page = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var bundle = http.CapturedTokens["alpha"];
        http.Clock.Advance(TimeSpan.FromMinutes(6));
        using (var expired = await HttpBrowser.ProbeAsync(http, "alpha", bundle.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var web = await browser.GetAsync("/alpha/account");
        Assert.Equal(HttpStatusCode.OK, web.StatusCode);
    }

    [Fact]
    public async Task Native_access_revocation_reaches_web_first_and_survives_restart()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var id = new JsonWebToken(alpha.AccessToken).GetClaim(OpenIddictConstants.Claims.Private.TokenId).Value;
        await http.InDefaultAsync(async provider =>
        {
            var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await tokens.FindByIdAsync(id);
            Assert.NotNull(entry);
            Assert.True(await tokens.TryRevokeAsync(entry));
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
        // No API probe is allowed to be the mechanism that invalidates the web session.
        using (var web = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
        using (var other = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await http.RestartAsync();
        browser.Rebind(http.NewClient());
        using (var web = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
        using (var other = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using (var api = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        using var refresh = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken);
        await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
    }
}
