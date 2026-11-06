using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Identity_runtime_cannot_manage_memberships_clients_registry_or_schema()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        var connections = new TenancyConnections();
        await SqlOperations.RequireRuntimeAsync(connections.Platform("runtime"), TestContext.Current.CancellationToken);
        await using var sql = await SqlOperations.OpenAsync(connections.Platform("runtime"), TestContext.Current.CancellationToken);
        foreach (var statement in new[]
        {
            "UPDATE [identity].[Memberships] SET [Active]=1 WHERE 1=0;",
            "UPDATE [identity].[Clients] SET [Enabled]=1 WHERE 1=0;",
            "UPDATE [identity].[Accounts] SET [Enabled]=1 WHERE 1=0;",
            "UPDATE [platform].[Tenants] SET [SecurityRevalidationRequired]=0 WHERE 1=0;",
            "UPDATE [platform].[OperationalAudit] SET [Actor]=N'changed' WHERE 1=0;",
            "DELETE FROM [platform].[OperationalAudit] WHERE 1=0;",
            "CREATE TABLE [identity].[MustNotExist] ([Id] int NOT NULL);"
        })
            await Assert.ThrowsAsync<SqlException>(() => SqlOperations.ExecuteAsync(sql, statement, TestContext.Current.CancellationToken));
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using var page = await browser.FollowAsync("/alpha/account/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        foreach (var path in new[] { "/admin", "/setup", "/OrchardCore.Users/Admin", "/alpha/admin", "/alpha/features", "/alpha/templates", "/alpha/api/roles" })
        {
            using var forbidden = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        }
    }

    [Fact]
    public async Task Native_access_token_revocation_also_invalidates_its_related_web_session()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var alpha = http.CapturedTokens["alpha"];
        var beta = http.CapturedTokens["beta"];
        var nativeId = new JsonWebToken(alpha.AccessToken).GetClaim(OpenIddictConstants.Claims.Private.TokenId).Value;
        await http.InDefaultAsync(async provider =>
        {
            var manager = provider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await manager.FindByIdAsync(nativeId);
            Assert.NotNull(entry);
            Assert.True(await manager.TryRevokeAsync(entry));
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
        using (var api = await HttpBrowser.ProbeAsync(http, "alpha", alpha.AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        using (var web = await browser.GetAsync("/alpha/account")) Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
        using (var other = await HttpBrowser.ProbeAsync(http, "beta", beta.AccessToken)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var otherWeb = await browser.GetAsync("/beta/account");
        Assert.Equal(HttpStatusCode.OK, otherWeb.StatusCode);
    }

    [Fact]
    public async Task Completed_identity_initialization_is_verify_only_when_native_history_is_missing()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        var installer = new IdentityInstaller(new TenancyConnections(), http.Deployment);
        // Positive retry must preserve native accounts and all existing credentials.
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        var bundle = await browser.ApiGrantAsync(http, "alpha");
        await installer.InitializeAsync(fixture.Environment.InstallationId, TestContext.Current.CancellationToken);
        using (var positive = await HttpBrowser.ProbeAsync(http, "alpha", bundle.AccessToken)) Assert.Equal(HttpStatusCode.OK, positive.StatusCode);
        await using var sql = await SqlOperations.OpenAsync(fixture.Environment.PlatformAdmin, TestContext.Current.CancellationToken);
        var table = "[dbo].[" + http.Deployment.NativePrefix + "_Document]";
        await SqlOperations.ExecuteAsync(sql, "UPDATE " + table + " SET [Content]=N'{}' WHERE [Type] LIKE N'%DataMigrationRecord%'", TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => installer.InitializeAsync(fixture.Environment.InstallationId, TestContext.Current.CancellationToken));
        var repaired = await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM " + table + " WHERE [Type] LIKE N'%DataMigrationRecord%' AND [Content]<>N'{}'", TestContext.Current.CancellationToken);
        Assert.Equal(0, Convert.ToInt32(repaired, System.Globalization.CultureInfo.InvariantCulture));
    }
}
