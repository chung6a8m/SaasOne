using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Authority_uses_one_owned_platform_connection_per_validation_and_never_reuses_authority()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var response = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var familyId = Guid.Parse(new JsonWebToken(http.CapturedTokens["alpha"].AccessToken).GetClaim(IdentityNames.Family).Value);
        var tenantId = http.Tenant("alpha").Id;
        await http.InDefaultAsync(async provider =>
        {
            var authority = provider.GetRequiredService<IdentityAuthority>();
            var native = provider.GetRequiredService<DefaultIssuerStore>();
            IdentityFamily family; IdentitySession session;
            await using (var db = authority.Open())
            {
                family = await db.IdentityFamilies.AsNoTracking().SingleAsync(x => x.Id == familyId, ct);
                session = await db.IdentitySessions.AsNoTracking().SingleAsync(x => x.Id == family.SessionId, ct);
            }
            int nativeOpens;
            using (var reference = new M7SqlProfile())
            {
                await native.ValidateAccountAndAuthorizationAsync(session.Subject, session.NativeSecurityStamp, family.AuthorizationId, ct);
                nativeOpens = reference.Snapshot().Open.Count;
            }
            Assert.Equal(1, nativeOpens);
            using var stageObserver = new M7StageProfile();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var measured = new M7SqlProfile();
                Assert.Equal(familyId, (await authority.ValidateAsync(familyId, tenantId, false, ct)).Id);
                Assert.Equal(nativeOpens, measured.Snapshot().Open.Count);
                // RED: the seven authoritative platform tables still require
                // a fresh SELECT, but that EF read borrows the new native-owned
                // login for this ONE validation, not an HTTP-wide session.
                var fresh = Assert.Single(measured.SnapshotStages(),
                    x => x.Stage == "identity.fresh");
                var nativeStage = Assert.Single(measured.SnapshotStages(),
                    x => x.Stage == "identity.native");
                Assert.Equal(0, fresh.Open.Count);
                Assert.Equal(1, nativeStage.Open.Count);
                Assert.True(nativeStage.Command.Count > 0);
                Assert.Equal(1, fresh.Command.Count);
                Assert.Equal(0, fresh.Open.Errors);
                Assert.Equal(0, fresh.Command.Errors);
                Assert.Equal(0, measured.Snapshot().Pending);
            }
            Assert.False(new SqlConnectionStringBuilder(new TenancyConnections().Platform("runtime")).Pooling);
            // A subsequent call in the SAME service scope must establish a new login.
            await using (var admin = await SqlOperations.OpenAsync(http.Environment.PlatformAdmin, ct))
            {
                await SqlOperations.ExecuteAsync(admin, "ALTER LOGIN [saas_platform_runtime] DISABLE", ct);
                try { await Assert.ThrowsAsync<SqlException>(() => authority.ValidateAsync(familyId, tenantId, false, ct)); }
                finally { await SqlOperations.ExecuteAsync(admin, "ALTER LOGIN [saas_platform_runtime] ENABLE", CancellationToken.None); }
            }
            await http.Operations.SetMembershipAsync(http.UserId, tenantId, false, ct);
            var denied = await Assert.ThrowsAsync<IdentityDeniedException>(() => authority.ValidateAsync(familyId, tenantId, false, ct));
            Assert.True(denied.StatusCode is 401 or 403);
        });
    }
}
