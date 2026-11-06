using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Native_token_exists_only_in_Default_not_customer_stores()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        await poc.InShellAsync("Default", async provider =>
            Assert.NotNull(await provider.GetRequiredService<IOpenIddictTokenManager>().FindByIdAsync(entry.TokenId)));
        foreach (var customer in new[] { "alpha", "beta" })
            await poc.InShellAsync(customer, async provider =>
                Assert.Null(await provider.GetRequiredService<IOpenIddictTokenManager>().FindByIdAsync(entry.TokenId)));
    }

    [Fact]
    public async Task Native_store_bridge_reads_Default_from_each_customer_scope()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("beta", entry));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", entry with { Subject = Guid.NewGuid().ToString("D") }));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", entry with { AuthorizationId = Guid.NewGuid().ToString("D") }));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", await poc.SeedAsync("alpha")));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", await poc.SeedAsync(type: OpenIddictConstants.TokenTypeHints.RefreshToken)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_committed_revocation_blocks_next_scope_without_invalidating_independent_grant(bool authorization)
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var alphaGrant = await poc.SeedAsync();
        var betaGrant = await poc.SeedAsync();
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", alphaGrant));
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("beta", betaGrant));
        await poc.RevokeAsync(alphaGrant, authorization);
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", alphaGrant));
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("beta", betaGrant));
        await poc.RestartAsync();
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", alphaGrant));
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("beta", betaGrant));
    }

    [Fact]
    public async Task Native_issuer_store_outage_is_unavailable_not_invalid_or_cached_allow()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
        await poc.SetDefaultReadFailureAsync(true);
        try
        {
            Assert.Equal(IssuerEntryDecision.Unavailable, await poc.ValidateFromAsync("alpha", entry));
            await poc.InShellAsync("beta", async provider =>
                Assert.Null(await provider.GetRequiredService<IOpenIddictTokenManager>().FindByIdAsync(entry.TokenId)));
        }
        finally { await poc.SetDefaultReadFailureAsync(false); }
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
    }
}
