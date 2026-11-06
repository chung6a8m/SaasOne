using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using SaaS.Identity;
using Xunit;

namespace SaaS.UnitTests;

internal sealed class HttpBrowser(HttpClient client) : IDisposable
{
    private HttpClient _client = client;
    public CookieContainer Cookies { get; } = new();
    public List<string> SetCookies { get; } = [];
    public int PasswordSubmissions { get; private set; }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public void Rebind(HttpClient replacement) { _client.Dispose(); _client = replacement; }
    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(IdentityHttpFixture.Origin, path)));
    public Task<HttpResponseMessage> PostAsync(string path, IReadOnlyDictionary<string, string> fields) => SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(IdentityHttpFixture.Origin, path)) { Content = new FormUrlEncodedContent(fields) });

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing browser target.");
        if (!uri.IsAbsoluteUri) request.RequestUri = uri = new Uri(IdentityHttpFixture.Origin, uri);
        if (uri.Scheme != "https" || uri.Authority != IdentityHttpFixture.Origin.Authority) throw new InvalidOperationException("Unexpected browser origin.");
        var cookie = Cookies.GetCookieHeader(uri);
        if (cookie.Length != 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        var response = await _client.SendAsync(request, Ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) { Cookies.SetCookies(uri, value); SetCookies.Add(value); }
        return response;
    }

    public async Task<HttpResponseMessage> FollowAsync(string path)
    {
        var response = await GetAsync(path);
        for (var count = 0; (int)response.StatusCode is 301 or 302 or 303; count++)
        {
            Assert.True(count < 12, "Identity redirect loop.");
            var location = response.Headers.Location ?? throw new InvalidOperationException("Missing redirect target.");
            var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
            response.Dispose();
            response = await GetAsync(target.AbsoluteUri);
        }
        return response;
    }

    public async Task LoginAsync(IdentityHttpFixture fixture)
    {
        using var form = await GetAsync("/account/login");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var csrf = Csrf(await form.Content.ReadAsStringAsync(Ct));
        PasswordSubmissions++;
        using var result = await PostAsync("/account/login", new Dictionary<string, string>
        {
            ["username"] = fixture.Username, ["password"] = fixture.Credential,
            ["returnUrl"] = "/account/tenants", ["__RequestVerificationToken"] = csrf
        });
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.True(result.Headers.Location?.OriginalString == "/account/tenants", "Login must use an allowlisted local return target.");
    }

    public async Task<OAuthTokens> ApiGrantAsync(IdentityHttpFixture fixture, string slug)
    {
        var tenant = fixture.Tenant(slug);
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var uri = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = IdentityDeployment.ClientId(tenant.Id), ["redirect_uri"] = fixture.Deployment.Callback(slug).AbsoluteUri,
            ["response_type"] = "code", ["scope"] = "openid offline_access " + IdentityNames.Scope,
            ["code_challenge_method"] = "S256", ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["state"] = state, ["nonce"] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)), [IdentityNames.Tenant] = tenant.Id.ToString("D")
        });
        using var authorization = await GetAsync(uri);
        Assert.Equal(HttpStatusCode.Redirect, authorization.StatusCode);
        var location = authorization.Headers.Location;
        Assert.NotNull(location);
        Assert.True(location.IsAbsoluteUri && location.AbsolutePath == "/" + slug + "/signin-oidc", "Authorization callback must be tenant-prefixed.");
        var parameters = QueryHelpers.ParseQuery(location.Query);
        Assert.True(parameters["state"].ToString() == state && parameters.ContainsKey("code") && !parameters.ContainsKey("error"), "Native authorization must issue a code bound to state.");
        using var tokenClient = fixture.NewClient();
        using var response = await tokenClient.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = IdentityDeployment.ClientId(tenant.Id), ["client_secret"] = fixture.Secret(tenant.Id),
            ["redirect_uri"] = fixture.Deployment.Callback(slug).AbsoluteUri, ["code"] = parameters["code"].ToString(), ["code_verifier"] = verifier
        }), Ct);
        return await TokensAsync(response);
    }

    public async Task<HttpResponseMessage> LogoutAsync(string page, string endpoint)
    {
        using var response = await GetAsync(page);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PostAsync(endpoint, new Dictionary<string, string> { ["__RequestVerificationToken"] = Csrf(await response.Content.ReadAsStringAsync(Ct)) });
    }

    public static async Task<HttpResponseMessage> ProbeAsync(IdentityHttpFixture fixture, string slug, string token)
    {
        using var client = fixture.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + slug + "/api/v1/identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Ct);
    }
    public static async Task<HttpResponseMessage> RefreshAsync(IdentityHttpFixture fixture, string slug, string token, Guid? requestedTenant = null)
    {
        var tenant = fixture.Tenant(slug);
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = IdentityDeployment.ClientId(tenant.Id),
            ["client_secret"] = fixture.Secret(tenant.Id), ["refresh_token"] = token
        };
        if (requestedTenant is not null) fields[IdentityNames.Tenant] = requestedTenant.Value.ToString("D");
        using var client = fixture.NewClient();
        return await client.PostAsync("/connect/token", new FormUrlEncodedContent(fields), Ct);
    }
    public static async Task<OAuthTokens> TokensAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;
        Assert.True(root.TryGetProperty("access_token", out var access) && root.TryGetProperty("refresh_token", out _), "Native token endpoint must issue both credentials.");
        return new OAuthTokens(access.GetString()!, root.GetProperty("refresh_token").GetString()!, root.TryGetProperty("id_token", out var id) ? id.GetString()! : "");
    }
    public static async Task AssertOAuthErrorAsync(HttpResponseMessage response, string expected)
    {
        Assert.True((int)response.StatusCode >= 400, "OAuth error must not report success.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.TryGetProperty("error", out var error) && error.GetString() == expected, "Unexpected OAuth error category.");
        Assert.False(json.RootElement.TryGetProperty("access_token", out _) || json.RootElement.TryGetProperty("refresh_token", out _));
    }
    public static string Csrf(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "An antiforgery token is required in the form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    public void Dispose() => _client.Dispose();
}
