using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OrchardCore.Users;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Same_email_never_links_different_issuer_subjects()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        var email = http.Username + "@example.invalid";
        // External identity envelopes deliberately share email and subject; only the
        // exact issuer+subject contract is admitted to the authoritative mapper.
        var first = new { Issuer = http.Deployment.Issuer.AbsoluteUri, http.Subject, Email = email };
        var otherIssuer = new { Issuer = "https://other-issuer.example.invalid/", http.Subject, Email = email };
        var otherSubject = new { Issuer = first.Issuer, Subject = Guid.NewGuid().ToString("N"), Email = email };
        Assert.Equal(first.Email, otherIssuer.Email);
        Assert.Equal(first.Email, otherSubject.Email);
        var firstId = await http.Operations.EnrollSubjectAsync(first.Issuer, first.Subject, TestContext.Current.CancellationToken);
        var secondId = await http.Operations.EnrollSubjectAsync(otherIssuer.Issuer, otherIssuer.Subject, TestContext.Current.CancellationToken);
        var thirdId = await http.Operations.EnrollSubjectAsync(otherSubject.Issuer, otherSubject.Subject, TestContext.Current.CancellationToken);
        Assert.Equal(http.UserId, firstId);
        Assert.NotEqual(firstId, secondId);
        Assert.NotEqual(firstId, thirdId);
        Assert.NotEqual(secondId, thirdId);
        Assert.Equal(secondId, await http.Operations.EnrollSubjectAsync(otherIssuer.Issuer, otherIssuer.Subject, TestContext.Current.CancellationToken));
        await using var db = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
        Assert.False(await db.IdentityMemberships.AnyAsync(x => x.GlobalUserId == secondId || x.GlobalUserId == thirdId));
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using var page = await browser.FollowAsync("/alpha/account/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(firstId.ToString("D"), html);
        Assert.DoesNotContain(secondId.ToString("D"), html);
        Assert.DoesNotContain(thirdId.ToString("D"), html);
    }

    [Fact]
    public async Task Jwt_format_signature_issuer_audience_scope_and_expiry_are_validated()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var credentials = await browser.ApiGrantAsync(http, "alpha");
        Assert.Equal(3, credentials.AccessToken.Split('.').Length); // Explicit signed JWT, not opaque or JWE.
        var native = new JsonWebToken(credentials.AccessToken);
        Assert.Equal("at+jwt", native.Typ);
        Assert.Equal("RS256", native.Alg);
        Assert.Equal(http.Deployment.Issuer.AbsoluteUri, native.Issuer);
        Assert.Contains(IdentityDeployment.Audience(http.Tenant("alpha").Id), native.Audiences);
        Assert.InRange(native.ValidTo - native.IssuedAt, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        using (var positive = await HttpBrowser.ProbeAsync(http, "alpha", credentials.AccessToken)) Assert.Equal(HttpStatusCode.OK, positive.StatusCode);
        foreach (var change in new[] { "issuer", "audience", "expired", "signature", "tenant", "subject", "scope" })
        {
            var forged = Resign(http, native, change);
            using var rejected = await HttpBrowser.ProbeAsync(http, "alpha", forged);
            Assert.Equal(change == "scope" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
    }

    private static string Resign(IdentityHttpFixture http, JsonWebToken original, string change)
    {
        var claims = original.Claims.Where(x => x.Type is not ("iss" or "aud" or "exp" or "iat" or "nbf"))
            .GroupBy(x => x.Type, StringComparer.Ordinal).ToDictionary(group => group.Key,
                group => group.Count() == 1 ? (object)group.First().Value : group.Select(x => x.Value).ToArray(), StringComparer.Ordinal);
        if (change == "tenant") claims[IdentityNames.Tenant] = http.Tenant("beta").Id.ToString("D");
        if (change == "subject") claims["sub"] = Guid.NewGuid().ToString("N");
        if (change == "scope") claims["scope"] = "openid";
        using var untrusted = RSA.Create(2048);
        var signing = change == "signature" ? (SecurityKey)new RsaSecurityKey(untrusted) : new X509SecurityKey(http.Deployment.SigningCertificate);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = change == "issuer" ? "https://wrong-issuer.example.invalid/" : http.Deployment.Issuer.AbsoluteUri,
            Audience = IdentityDeployment.Audience(http.Tenant(change == "audience" ? "beta" : "alpha").Id),
            Claims = claims, TokenType = "at+jwt", SigningCredentials = new SigningCredentials(signing, SecurityAlgorithms.RsaSha256),
            IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = change == "expired" ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(5)
        });
    }

    [Fact]
    public async Task Callback_logout_and_return_urls_are_strictly_allowlisted()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        foreach (var target in new[] { "https://evil.example.invalid/", "//evil.example.invalid/", "/\\evil.example.invalid/", "/alpha/account", "/account/tenants#fragment", "/account/tenants\n" })
        {
            using var response = await browser.GetAsync("/account/login?returnUrl=" + Uri.EscapeDataString(target));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        await browser.LoginAsync(http);
        var alpha = http.Tenant("alpha");
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var request = new Dictionary<string, string?>
        {
            ["client_id"] = IdentityDeployment.ClientId(alpha.Id), ["redirect_uri"] = "https://evil.example.invalid/signin-oidc",
            ["response_type"] = "code", ["scope"] = "openid " + IdentityNames.Scope, ["state"] = "state",
            ["code_challenge_method"] = "S256", ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        using (var invalidCallback = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", request)))
        {
            Assert.True((int)invalidCallback.StatusCode >= 400);
            Assert.Null(invalidCallback.Headers.Location);
        }
        using (var invalidLogout = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/logout", new Dictionary<string, string?>
        {
            ["client_id"] = IdentityDeployment.ClientId(alpha.Id), ["post_logout_redirect_uri"] = "https://evil.example.invalid/"
        })))
        {
            Assert.True((int)invalidLogout.StatusCode >= 400);
            Assert.Null(invalidLogout.Headers.Location);
        }
        using var challenge = await browser.GetAsync("/alpha/account/login");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        using var wrongPrefix = await browser.GetAsync(QueryHelpers.AddQueryString("/beta/signin-oidc", new Dictionary<string, string?> { ["state"] = state, ["code"] = "not-a-code" }));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPrefix.StatusCode);
        using var unsolicited = await browser.GetAsync("/alpha/signin-oidc?code=not-a-code&state=not-a-state");
        Assert.Equal(HttpStatusCode.Unauthorized, unsolicited.StatusCode);
    }

    [Fact]
    public async Task Mutation_requires_antiforgery_and_two_tabs_keep_their_context()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            foreach (var slug in new[] { "beta", "alpha" })
            {
                using var page = await browser.GetAsync("/" + slug + "/account");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Contains(http.Tenant(slug).Id.ToString("D"), html);
                Assert.DoesNotContain(http.Tenant(slug == "alpha" ? "beta" : "alpha").Id.ToString("D"), html);
            }
        }
        using (var missing = await browser.PostAsync("/alpha/account/logout", new Dictionary<string, string>())) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var betaPage = await browser.GetAsync("/beta/account");
        var betaCsrf = HttpBrowser.Csrf(await betaPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using (var wrong = await browser.PostAsync("/alpha/account/logout", new Dictionary<string, string> { ["__RequestVerificationToken"] = betaCsrf })) Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using (var stillSignedIn = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        using (var logout = await browser.LogoutAsync("/alpha/account", "/alpha/account/logout")) Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using (var alphaAfter = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, alphaAfter.StatusCode);
        using (var betaAfter = await browser.GetAsync("/beta/account")) Assert.Equal(HttpStatusCode.OK, betaAfter.StatusCode);
    }

    [Fact]
    public async Task Native_password_lockout_then_unlock_does_not_revive_prior_grants()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var page = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var old = http.CapturedTokens["alpha"];
        var maxAttempts = 0;
        await http.InDefaultAsync(provider =>
        {
            maxAttempts = provider.GetRequiredService<UserManager<IUser>>().Options.Lockout.MaxFailedAccessAttempts;
            return Task.CompletedTask;
        });
        for (var i = 0; i < maxAttempts; i++)
        {
            using var form = await browser.GetAsync("/account/login");
            var csrf = HttpBrowser.Csrf(await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var failure = await browser.PostAsync("/account/login", new Dictionary<string, string>
            {
                ["username"] = http.Username, ["password"] = "incorrect-credential", ["returnUrl"] = "/account/tenants", ["__RequestVerificationToken"] = csrf
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }
        using (var blocked = await HttpBrowser.ProbeAsync(http, "alpha", old.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        await http.InDefaultAsync(async provider =>
        {
            var users = provider.GetRequiredService<UserManager<IUser>>();
            var user = await users.FindByIdAsync(http.Subject);
            Assert.NotNull(user);
            Assert.True(await users.IsLockedOutAsync(user));
            Assert.True((await users.SetLockoutEndDateAsync(user, null)).Succeeded);
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
        using (var oldApi = await HttpBrowser.ProbeAsync(http, "alpha", old.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, oldApi.StatusCode);
        using (var oldWeb = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, oldWeb.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "alpha", old.RefreshToken)) await HttpBrowser.AssertOAuthErrorAsync(refresh, "invalid_grant");
        await browser.LoginAsync(http);
        using var fresh = await browser.FollowAsync("/alpha/account/login");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }
}
