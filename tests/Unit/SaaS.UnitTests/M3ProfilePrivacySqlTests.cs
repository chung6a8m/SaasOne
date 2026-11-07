using System.Net;
using System.Text.Json;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Theory]
    [InlineData("anonymous", "api")]
    [InlineData("anonymous", "web")]
    [InlineData("wrong-tenant", "api")]
    [InlineData("wrong-tenant", "web")]
    [InlineData("other-user", "api")]
    [InlineData("other-user", "web")]
    public async Task Profile_denial_does_not_disclose_target_membership(string caller, string transport)
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser();
        if (caller != "anonymous")
        {
            await browser.LoginAsync(http);
            using var beta = await browser.FollowAsync("/beta/account/login");
            Assert.Equal(HttpStatusCode.OK, beta.StatusCode);
            if (caller == "other-user")
            {
                using var alpha = await browser.FollowAsync("/alpha/account/login");
                Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
            }
        }
        var existing = await HttpTargetMemberAsync();
        var absent = Guid.NewGuid();
        var prefix = transport == "api" ? "/alpha/api/v1/profile/" : "/alpha/profile/";
        async Task<(HttpStatusCode Status, string? Error)> ProbeAsync(Guid target)
        {
            var path = prefix + target.ToString("D") + "?ownerId=" + http.UserId;
            using var client = http.NewClient();
            using var response = transport == "web" ? await browser.GetAsync(path)
                : caller == "anonymous" ? await client.GetAsync(path, Ct)
                : await HttpProbeAsync(http, http.CapturedTokens[caller == "wrong-tenant" ? "beta" : "alpha"].AccessToken, path);
            Assert.Null(response.Headers.Location);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return (response.StatusCode, body.RootElement.GetProperty("error").GetString());
        }
        var member = await ProbeAsync(existing);
        var nonmember = await ProbeAsync(absent);
        Assert.Equal(caller == "other-user" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, member.Status);
        Assert.Equal(member, nonmember);
    }
}
