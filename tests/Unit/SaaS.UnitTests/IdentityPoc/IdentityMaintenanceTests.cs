using System.Net;
using SaaS.Web.Maintenance;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Private_identity_commands_revoke_membership_and_account_without_restoring_old_grants()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        var user = http.UserId.ToString("D");
        var tenant = http.Tenant("alpha").Id.ToString("D");
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["identity-membership", user, tenant, "inactive"], TestContext.Current.CancellationToken));
        using (var denied = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var other = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["identity-membership", user, tenant, "active"], TestContext.Current.CancellationToken));
        using (var old = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["identity-account", user, "disabled"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["identity-account", user, "enabled"], TestContext.Current.CancellationToken));
        using (var old = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["identity-account", user, "maybe"], TestContext.Current.CancellationToken));
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["identity-membership", "not-a-uuid", tenant, "active"], TestContext.Current.CancellationToken));
    }
}
