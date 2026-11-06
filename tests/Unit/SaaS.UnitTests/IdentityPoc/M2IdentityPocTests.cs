using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM2")]
public sealed class M2IdentityPocTests(M1SqlFixture fixture) : IClassFixture<M1SqlFixture>
{
    [Fact]
    public async Task Authorization_code_entrypoint_challenges_Default_with_Pkce()
    {
        using var response = await fixture.Client.GetAsync("/alpha/account/login", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var destination = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("/connect/authorize", destination.AbsolutePath);
        var query = QueryHelpers.ParseQuery(destination.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"].ToString()));
        Assert.False(string.IsNullOrWhiteSpace(query["state"].ToString()));
        Assert.Equal("/alpha/signin-oidc", new Uri(query["redirect_uri"].ToString()).AbsolutePath);
        Assert.Contains("openid", query["scope"].ToString().Split(' '));
    }
}
