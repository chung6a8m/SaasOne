using System.Net;
using System.Text.Json;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Native_identity_discovery_advertises_code_Pkce_and_no_password_grant()
    {
        using var response = await fixture.Client.GetAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Contains(root.GetProperty("response_types_supported").EnumerateArray(), value => value.GetString() == "code");
        Assert.Contains(root.GetProperty("code_challenge_methods_supported").EnumerateArray(), value => value.GetString() == "S256");
        Assert.DoesNotContain(root.GetProperty("grant_types_supported").EnumerateArray(), value => value.GetString() == "password");
    }

    [Fact]
    public async Task Central_login_rejects_a_post_without_antiforgery()
    {
        using var response = await fixture.Client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "not-a-user", ["password"] = "not-a-credential", ["returnUrl"] = "/account/tenants"
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Protected_identity_probe_requires_a_bearer_credential()
    {
        using var response = await fixture.Client.GetAsync("/alpha/api/v1/identity", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }
}
