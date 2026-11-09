using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Combined_native_validation_keeps_all_queries_one_fresh_scope_and_revocation_order()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var response = await browser.FollowAsync("/alpha/account/login")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var familyId = Guid.Parse(new JsonWebToken(http.CapturedTokens["alpha"].AccessToken).GetClaim(IdentityNames.Family).Value);
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
            int referenceCommands;
            using (var reference = new M7SqlProfile())
            {
                await native.ValidateAccountAsync(session.Subject, session.NativeSecurityStamp, ct);
                await native.ValidateAuthorizationAsync(family.AuthorizationId, family.Subject, ct);
                referenceCommands = reference.Snapshot().Command.Count;
                Assert.True(referenceCommands > 0);
                Assert.Equal(2, reference.Snapshot().Open.Count);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var profile = new M7SqlProfile();
                await native.ValidateAccountAndAuthorizationAsync(session.Subject, session.NativeSecurityStamp, family.AuthorizationId, ct);
                var observed = profile.Snapshot();
                Assert.Equal(referenceCommands, observed.Command.Count);
                Assert.Equal(1, observed.Open.Count);
                Assert.Equal(0, observed.Open.Errors);
                Assert.Equal(0, observed.Command.Errors);
                Assert.Equal(0, observed.Pending);
            }
            var accountDenied = await Assert.ThrowsAsync<IdentityDeniedException>(() =>
                native.ValidateAccountAndAuthorizationAsync(session.Subject, "incorrect-test-stamp", "absent-test-authorization", ct));
            Assert.Equal("account_invalid", accountDenied.Code);
            await http.InDefaultAsync(async issuer =>
            {
                var manager = issuer.GetRequiredService<IOpenIddictAuthorizationManager>();
                var authorization = await manager.FindByIdAsync(family.AuthorizationId, ct);
                Assert.NotNull(authorization);
                Assert.True(await manager.TryRevokeAsync(authorization, ct));
                await issuer.GetRequiredService<YesSql.ISession>().SaveChangesAsync(ct);
            });
            var grantDenied = await Assert.ThrowsAsync<IdentityDeniedException>(() =>
                native.ValidateAccountAndAuthorizationAsync(session.Subject, session.NativeSecurityStamp, family.AuthorizationId, ct));
            Assert.Equal("grant_invalid", grantDenied.Code);
        });
    }
}
