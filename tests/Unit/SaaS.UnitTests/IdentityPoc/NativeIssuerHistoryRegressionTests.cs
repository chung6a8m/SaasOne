using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OrchardCore.OpenId.YesSql.Models;
using SaaS.Identity;
using YesSql;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Rotated_native_history_detects_old_access_revocation_without_cross_family_effect()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var alphaLogin = await browser.FollowAsync("/alpha/account/login"))
            Assert.Equal(HttpStatusCode.OK, alphaLogin.StatusCode);
        using (var betaLogin = await browser.FollowAsync("/beta/account/login"))
            Assert.Equal(HttpStatusCode.OK, betaLogin.StatusCode);

        var first = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        var jwt = new JsonWebToken(first.AccessToken);
        var authorizationId = jwt.GetClaim(IdentityNames.Authorization).Value;
        var oldestAccessId = jwt.GetClaim(OpenIddictConstants.Claims.Private.TokenId).Value;
        var latest = first;
        const int rotations = 24;

        for (var count = 0; count < rotations; count++)
        {
            using var response = await HttpBrowser.RefreshAsync(http, "alpha", latest.RefreshToken);
            latest = await HttpBrowser.TokensAsync(response);
        }

        // Test-only history enumeration: demonstrate the large native family that
        // production authorization must NOT enumerate on each web/bearer/refresh call.
        await http.InDefaultAsync(async provider =>
        {
            var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
            var accesses = 0;
            var refreshes = 0;
            await foreach (var token in tokens.FindByAuthorizationIdAsync(authorizationId))
            {
                var type = await tokens.GetTypeAsync(token);
                if (type == OpenIddictConstants.TokenTypeIdentifiers.AccessToken) accesses++;
                if (type == OpenIddictConstants.TokenTypeIdentifiers.RefreshToken) refreshes++;
            }
            Assert.True(accesses >= rotations + 1, $"Expected accumulated access history, found {accesses}.");
            Assert.True(refreshes >= rotations + 1, $"Expected accumulated refresh history, found {refreshes}.");
        });

        using (var web = await browser.GetAsync("/alpha/account"))
            Assert.Equal(HttpStatusCode.OK, web.StatusCode);
        using (var api = await HttpBrowser.ProbeAsync(http, "alpha", latest.AccessToken))
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        await http.InDefaultAsync(async provider =>
        {
            var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
            var oldest = await tokens.FindByIdAsync(oldestAccessId);
            Assert.NotNull(oldest);
            Assert.True(await tokens.TryRevokeAsync(oldest));
            await provider.GetRequiredService<ISession>().SaveChangesAsync();
        });

        // Web must be the FIRST request after the old native access revocation;
        // it may not rely on an API call or a per-request cached Allow.
        using (var web = await browser.GetAsync("/alpha/account"))
            Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
        using (var api = await HttpBrowser.ProbeAsync(http, "alpha", latest.AccessToken))
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "alpha", latest.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        using (var otherWeb = await browser.GetAsync("/beta/account"))
            Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
        using (var otherApi = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken))
            Assert.Equal(HttpStatusCode.OK, otherApi.StatusCode);

        await http.RestartAsync();
        browser.Rebind(http.NewClient());
        using (var api = await HttpBrowser.ProbeAsync(http, "alpha", latest.AccessToken))
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        using (var otherWeb = await browser.GetAsync("/beta/account"))
            Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
    }

    [Fact]
    public async Task Native_access_binding_violations_are_rejected_by_index_only_query()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var wrongSubject = await browser.ApiGrantAsync(http, "alpha");
        var wrongApplication = await browser.ApiGrantAsync(http, "alpha");
        var independent = await browser.ApiGrantAsync(http, "beta");

        static (string Authorization, string TokenId) Binding(OAuthTokens tokens)
        {
            var jwt = new JsonWebToken(tokens.AccessToken);
            return (jwt.GetClaim(IdentityNames.Authorization).Value,
                jwt.GetClaim(OpenIddictConstants.Claims.Private.TokenId).Value);
        }

        var subject = Binding(wrongSubject);
        var application = Binding(wrongApplication);
        var beta = Binding(independent);
        var store = new DefaultIssuerStore(http.Shells, http.Clock);
        var ct = TestContext.Current.CancellationToken;
        await store.ValidateAuthorizationAsync(subject.Authorization, http.Subject, ct);
        await store.ValidateAuthorizationAsync(application.Authorization, http.Subject, ct);
        await store.ValidateAuthorizationAsync(beta.Authorization, http.Subject, ct);

        await http.InDefaultAsync(async provider =>
        {
            var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
            var session = provider.GetRequiredService<ISession>();
            var subjectToken = Assert.IsAssignableFrom<OpenIdToken>(await tokens.FindByIdAsync(subject.TokenId));
            var applicationToken = Assert.IsAssignableFrom<OpenIdToken>(await tokens.FindByIdAsync(application.TokenId));
            subjectToken.Subject = Guid.NewGuid().ToString("D");
            applicationToken.ApplicationId = "not-the-bound-client";
            await session.SaveAsync(subjectToken, collection: OpenIdToken.OpenIdCollection);
            await session.SaveAsync(applicationToken, collection: OpenIdToken.OpenIdCollection);
            await session.SaveChangesAsync();
        });

        var subjectDenied = await Assert.ThrowsAsync<IdentityDeniedException>(
            () => store.ValidateAuthorizationAsync(subject.Authorization, http.Subject, ct));
        var applicationDenied = await Assert.ThrowsAsync<IdentityDeniedException>(
            () => store.ValidateAuthorizationAsync(application.Authorization, http.Subject, ct));
        Assert.Equal("grant_invalid", subjectDenied.Code);
        Assert.Equal("grant_invalid", applicationDenied.Code);
        await store.ValidateAuthorizationAsync(beta.Authorization, http.Subject, ct);
    }
}
