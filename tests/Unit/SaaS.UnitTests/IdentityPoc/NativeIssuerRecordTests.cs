using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Native_committed_records_preserve_the_complete_authorization_binding()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        await poc.InShellAsync("Default", async provider =>
        {
            var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.FindByIdAsync(entry.TokenId);
            Assert.NotNull(token);
            Assert.Equal(OpenIddictConstants.Statuses.Valid, await tokens.GetStatusAsync(token));
            Assert.Equal(OpenIddictConstants.TokenTypeIdentifiers.AccessToken, await tokens.GetTypeAsync(token));
            Assert.Equal(entry.Subject, await tokens.GetSubjectAsync(token));
            Assert.Equal(entry.AuthorizationId, await tokens.GetAuthorizationIdAsync(token));
            Assert.True(await tokens.GetExpirationDateAsync(token) > DateTimeOffset.UtcNow, "Native token expiry must survive commit.");
            var authorizations = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await authorizations.FindByIdAsync(entry.AuthorizationId);
            Assert.NotNull(authorization);
            Assert.Equal(OpenIddictConstants.Statuses.Valid, await authorizations.GetStatusAsync(authorization));
            Assert.Equal(entry.Subject, await authorizations.GetSubjectAsync(authorization));
        });
        Assert.Equal(IssuerEntryDecision.Valid, await poc.Probe.ValidateAsync(entry, CancellationToken.None));
    }
}
