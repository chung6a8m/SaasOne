using System.Net;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
using static SaaS.UnitTests.M4HttpTests;

namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public async Task Physical_restore_rejects_old_refresh_cookie_and_JWT_after_recovery_without_revoking_Beta()
    {
        var setupService = new M4HttpTests(fixture);
        await using var setup = await setupService.HttpAsync();
        await AuthorityAsync();
        var http = setup.Http;
        using var browser = http.NewBrowser();
        await LoginAsync(http, browser);
        var alpha = http.CapturedTokens["alpha"];
        // A separate family exercises refusal while pending. Never consume/revoke alpha's
        // untouched refresh token before the post-recovery assertion: that would mask a bug.
        var pendingAlpha = await browser.ApiGrantAsync(http, "alpha");
        using (var login = await browser.FollowAsync("/beta/account/login"))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var beta = http.CapturedTokens["beta"];
        using (var own = await ApiAsync(http, alpha.AccessToken, "/alpha/api/v1/orders"))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var other = await ApiAsync(http, beta.AccessToken, "/beta/api/v1/orders"))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var betaBefore = await CurrentAsync(Environment.Tenants.Single(x => x.Slug == "beta"));

        using var store = new SqlBackupStore();
        var backup = await BackupAsync(store);
        var currentAcl = SalesAcl(http.UserId) with { Assignments = [] };
        await setupService.SeedAsync(Alpha, currentAcl);
        var maintenance = new AccessMaintenance(new());
        var operation = Guid.NewGuid();
        var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation,
            (await CurrentAsync()).RegistryVersion, backup.BackupId, store, false, Ct);
        var closed = await CurrentAsync();
        Assert.Equal(OperationStatus.Restoring, closed.Operation);
        Assert.True(closed.SecurityRevalidationRequired);
        using (var refused = await HttpBrowser.RefreshAsync(http, "alpha", pendingAlpha.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(refused, "invalid_grant");
        using (var other = await ApiAsync(http, beta.AccessToken, "/beta/api/v1/orders"))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using (var otherCookie = await browser.GetAsync("/beta/account"))
            Assert.Equal(HttpStatusCode.OK, otherCookie.StatusCode);

        var approved = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion,
            currentAcl, "Issue 10: retain active membership, remove historical role assignment", false, Ct);
        await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct);
        using (var oldRefresh = await HttpBrowser.RefreshAsync(http, "alpha", alpha.RefreshToken))
            await HttpBrowser.AssertOAuthErrorAsync(oldRefresh, "invalid_grant");
        using (var oldJwt = await ApiAsync(http, alpha.AccessToken, "/alpha/api/v1/orders"))
            Assert.Equal(HttpStatusCode.Unauthorized, oldJwt.StatusCode);
        using (var oldCookie = await browser.GetAsync("/alpha/account"))
            Assert.Equal(HttpStatusCode.Unauthorized, oldCookie.StatusCode);

        using var freshBrowser = http.NewBrowser();
        await LoginAsync(http, freshBrowser);
        var fresh = http.CapturedTokens["alpha"];
        using (var denied = await ApiAsync(http, fresh.AccessToken, "/alpha/api/v1/orders"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "alpha", fresh.RefreshToken))
        {
            var rotated = await HttpBrowser.TokensAsync(refresh);
            using var denied = await ApiAsync(http, rotated.AccessToken, "/alpha/api/v1/orders");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var otherCookie = await browser.GetAsync("/beta/account"))
            Assert.Equal(HttpStatusCode.OK, otherCookie.StatusCode);
        using (var other = await ApiAsync(http, beta.AccessToken, "/beta/api/v1/orders"))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using (var refresh = await HttpBrowser.RefreshAsync(http, "beta", beta.RefreshToken))
        {
            var rotated = await HttpBrowser.TokensAsync(refresh);
            using var other = await ApiAsync(http, rotated.AccessToken, "/beta/api/v1/orders");
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        var betaAfter = await CurrentAsync(Environment.Tenants.Single(x => x.Slug == "beta"));
        Assert.Equal(betaBefore.RegistryVersion, betaAfter.RegistryVersion);
        Assert.Equal(betaBefore.AuthorizationGeneration, betaAfter.AuthorizationGeneration);
        await using var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.True(await platform.IdentityMemberships.AnyAsync(x => x.TenantId == Alpha.Id
            && x.GlobalUserId == http.UserId && x.Active, Ct));
    }
}
