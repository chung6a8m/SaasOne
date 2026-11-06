using System.Net;
using Microsoft.EntityFrameworkCore;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Product_host_supports_Sso_Api_and_refresh_with_only_runtime_credentials()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment, productHost: true);
        var keys = new[] { "SAAS_PROVISIONER_CONNECTION", "SAAS_PLATFORM_OPERATOR_CONNECTION", "SAAS_PLATFORM_MIGRATOR_CONNECTION" }
            .Concat(fixture.Environment.Tenants.Select(x => TenancyConnections.TenantSecret(x.Id, "migrator"))).ToArray();
        var saved = keys.ToDictionary(x => x, System.Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in keys) System.Environment.SetEnvironmentVariable(key, null);
            await http.RestartAsync();
            using var browser = http.NewBrowser();
            using (var ready = await browser.GetAsync("/health/ready")) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            await browser.LoginAsync(http);
            foreach (var slug in new[] { "alpha", "beta" })
            {
                using (var page = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                using var api = await HttpBrowser.ProbeAsync(http, slug, http.CapturedTokens[slug].AccessToken);
                Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            }
            Assert.Equal(1, browser.PasswordSubmissions);
            using (var gamma = await browser.FollowAsync("/gamma/account/login")) Assert.True((int)gamma.StatusCode is 400 or 401 or 403);
            using (var cross = await HttpBrowser.ProbeAsync(http, "beta", http.CapturedTokens["alpha"].AccessToken)) Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
            using (var refresh = await HttpBrowser.RefreshAsync(http, "alpha", http.CapturedTokens["alpha"].RefreshToken)) Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            foreach (var path in new[] { "/admin", "/setup", "/OrchardCore.Users/Admin", "/alpha/admin", "/alpha/features", "/alpha/templates", "/alpha/api/roles" })
            {
                using var closed = await browser.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);
            }
        }
        finally
        {
            foreach (var (key, value) in saved) System.Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public async Task Product_readiness_and_login_close_when_native_history_is_corrupt()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment, productHost: true);
        using var browser = http.NewBrowser();
        using (var ready = await browser.GetAsync("/health/ready")) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using var sql = await SqlOperations.OpenAsync(fixture.Environment.PlatformAdmin, TestContext.Current.CancellationToken);
        var table = "[dbo].[" + http.Deployment.NativePrefix + "_Document]";
        await SqlOperations.ExecuteAsync(sql, "UPDATE " + table + " SET [Content]=N'{}' WHERE [Type] LIKE N'%DataMigrationRecord%'", TestContext.Current.CancellationToken);
        using (var ready = await browser.GetAsync("/health/ready")) Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using (var login = await browser.GetAsync("/account/login")) Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        using (var live = await browser.GetAsync("/health/live")) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var repaired = await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM " + table + " WHERE [Type] LIKE N'%DataMigrationRecord%' AND [Content]<>N'{}'", TestContext.Current.CancellationToken);
        Assert.Equal(0, Convert.ToInt32(repaired, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Completed_identity_retry_is_read_only_and_does_not_resolve_client_secrets()
    {
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        var current = http.Deployment;
        var verificationOnly = new IdentityDeployment(current.Issuer, current.SigningCertificate, current.EncryptionCertificate,
            current.DataProtectionDirectory, current.NativePrefix, _ => throw new InvalidOperationException("Retry must not resolve a client credential."));
        await new IdentityInstaller(new TenancyConnections(), verificationOnly)
            .InitializeAsync(fixture.Environment.InstallationId, TestContext.Current.CancellationToken);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using var page = await browser.FollowAsync("/alpha/account/login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}
