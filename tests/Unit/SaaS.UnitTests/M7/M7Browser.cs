using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using SaaS.Identity;
using Xunit;
namespace SaaS.UnitTests;

internal sealed class M7Browser(M7SqlFixture fixture, HttpClient client) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal Task<HttpResponseMessage> GetAsync(string path)
    {
        var uri = new Uri(fixture.Origin, path);
        if (uri.Scheme != "https" || uri.Authority != fixture.Origin.Authority) throw new InvalidOperationException("Unexpected browser origin.");
        return client.GetAsync(uri, Ct);
    }
    internal async Task<HttpResponseMessage> FollowAsync(string path)
    {
        var result = await GetAsync(path);
        for (var i = 0; (int)result.StatusCode is 301 or 302 or 303; i++)
        {
            Assert.True(i < 12, "Native OIDC redirect loop.");
            var location = result.Headers.Location ?? throw new InvalidOperationException("Missing redirect.");
            var uri = location.IsAbsoluteUri ? location : new Uri(result.RequestMessage!.RequestUri!, location);
            result.Dispose(); result = await GetAsync(uri.AbsoluteUri);
        }
        return result;
    }
    internal async Task LoginAsync(M7Actor actor)
    {
        using var form = await GetAsync("/account/login");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var csrf = HttpBrowser.Csrf(await form.Content.ReadAsStringAsync(Ct));
        using var result = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = actor.Username, ["password"] = actor.Password, ["returnUrl"] = "/account/tenants", ["__RequestVerificationToken"] = csrf
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.True(result.Headers.Location?.OriginalString == "/account/tenants", "Login must retain the local return target.");
    }
    internal async Task<OAuthTokens> GrantAsync(string slug)
    {
        var tenant = fixture.Tenant(slug);
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var path = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = IdentityDeployment.ClientId(tenant.Id), ["redirect_uri"] = fixture.Deployment.Callback(slug).AbsoluteUri,
            ["response_type"] = "code", ["scope"] = "openid offline_access " + IdentityNames.Scope,
            ["code_challenge_method"] = "S256", ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["state"] = state, ["nonce"] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)), [IdentityNames.Tenant] = tenant.Id.ToString("D")
        });
        using var authorization = await GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, authorization.StatusCode);
        var location = authorization.Headers.Location;
        Assert.True(location is { IsAbsoluteUri: true } && location.Authority == fixture.Origin.Authority
            && location.AbsolutePath == "/" + slug + "/signin-oidc", "Authorization must retain the tenant callback and origin.");
        var query = QueryHelpers.ParseQuery(location!.Query);
        Assert.True(query["state"].ToString() == state && query.ContainsKey("code") && !query.ContainsKey("error"), "Native authorization must return a bound code.");
        using var response = await fixture.Api.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = IdentityDeployment.ClientId(tenant.Id), ["client_secret"] = fixture.Secret(tenant.Id),
            ["redirect_uri"] = fixture.Deployment.Callback(slug).AbsoluteUri, ["code"] = query["code"].ToString(), ["code_verifier"] = verifier
        }), Ct);
        return await HttpBrowser.TokensAsync(response);
    }
    public void Dispose() => client.Dispose();
}
