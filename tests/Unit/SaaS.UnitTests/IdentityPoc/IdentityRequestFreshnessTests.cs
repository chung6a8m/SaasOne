using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using SaaS.Identity;
using SaaS.Tenancy.Operations;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task One_http_context_must_not_reuse_bearer_authority_across_login_revoke_or_tenant_switch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var alpha = await browser.FollowAsync("/alpha/account/login"))
            Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
        using (var beta = await browser.FollowAsync("/beta/account/login"))
            Assert.Equal(HttpStatusCode.OK, beta.StatusCode);

        var alphaId = http.Tenant("alpha").Id;
        var betaId = http.Tenant("beta").Id;
        var alphaToken = http.CapturedTokens["alpha"].AccessToken;
        var betaToken = http.CapturedTokens["beta"].AccessToken;
        // One actual HTTP context and one native service provider are deliberately
        // retained across independent bearer validations. A future request-local
        // transport MUST NOT cache the first source, issuer entity or Allow.
        await http.InDefaultAsync(async provider =>
        {
            var request = new DefaultHttpContext { RequestServices = provider };
            request.Request.Headers.Authorization = "Bearer " + alphaToken;
            Task<ValidatedBearerTenant> ReadAsync(Guid routedTenant) =>
                IdentityProtocol.AuthenticateBearerForApplicationAsync(request, routedTenant);

            using var stageListener = new M7StageProfile();
            for (var i = 0; i < 2; i++)
            {
                using var measured = new M7SqlProfile();
                var verified = await ReadAsync(alphaId);
                Assert.Equal(alphaId, verified.Family.TenantId);
                Assert.Equal(http.UserId, verified.Family.GlobalUserId);
                var fresh = Assert.Single(measured.SnapshotStages(), x => x.Stage == "identity.fresh");
                var native = Assert.Single(measured.SnapshotStages(), x => x.Stage == "identity.native");
                // Every invocation must open ONE NEW unpooled SQL login owned
                // by the native YesSQL scope. Platform EF borrows it, never
                // reuses that SQL session for the next validation.
                Assert.Equal(0, fresh.Open.Count);
                Assert.Equal(1, native.Open.Count);
                Assert.True(fresh.Command.Count >= 2, "Initial and post-native current tenant reads cannot be cached.");
                Assert.True(native.Command.Count > 0, "Native account/grant/token source must be read.");
                Assert.Equal(0, fresh.Open.Errors);
                Assert.Equal(0, native.Open.Errors);
                Assert.Equal(0, measured.Snapshot().Pending);
            }

            var wrongAudience = await Assert.ThrowsAsync<IdentityDeniedException>(() => ReadAsync(betaId));
            Assert.Equal(401, wrongAudience.StatusCode);
            request.Request.Headers.Authorization = "Bearer " + betaToken;
            Assert.Equal(betaId, (await ReadAsync(betaId)).Family.TenantId);
            // Switching back to alpha within the same context cannot return a
            // prior beta tenant, registered client, membership or current plan.
            request.Request.Headers.Authorization = "Bearer " + alphaToken;
            Assert.Equal(alphaId, (await ReadAsync(alphaId)).Family.TenantId);

            await using (var admin = await SqlOperations.OpenAsync(http.Environment.PlatformAdmin, ct))
            {
                await SqlOperations.ExecuteAsync(admin, "ALTER LOGIN [saas_platform_runtime] DISABLE", ct);
                try
                {
                    // Existing SQL Server sessions can survive DISABLE LOGIN. This
                    // MUST fail on the next independent validation, not silently
                    // succeed by reusing a request-local authenticated session.
                    await Assert.ThrowsAsync<SqlException>(() => ReadAsync(alphaId));
                }
                finally
                {
                    await SqlOperations.ExecuteAsync(admin, "ALTER LOGIN [saas_platform_runtime] ENABLE", CancellationToken.None);
                }
            }

            Assert.Equal(alphaId, (await ReadAsync(alphaId)).Family.TenantId);
            await http.Operations.SetMembershipAsync(http.UserId, alphaId, false, ct);
            var revoked = await Assert.ThrowsAsync<IdentityDeniedException>(() => ReadAsync(alphaId));
            Assert.True(revoked.StatusCode is 401 or 403);
        });
    }
}
