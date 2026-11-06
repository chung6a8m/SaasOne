using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Core;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Fact]
    public async Task Native_store_bridge_rejects_expired_and_missing_entries()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
        var expired = new DefaultIssuerStoreProbe(poc.Shells, new FixedClock(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(IssuerEntryDecision.Invalid, await expired.ValidateAsync(entry, CancellationToken.None));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", entry with { TokenId = Guid.NewGuid().ToString("N") }));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", entry with { TokenId = "" }));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", entry with { AuthorizationId = "" }));
        Assert.Equal(IssuerEntryDecision.Invalid, await poc.ValidateFromAsync("alpha", entry with { Subject = "" }));
    }

    [Fact]
    public async Task Native_store_bridge_refuses_cache_enabled_configuration()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
        OpenIddictCoreOptions options = null!;
        await poc.InShellAsync("Default", provider =>
        {
            options = provider.GetRequiredService<IOptions<OpenIddictCoreOptions>>().Value;
            return Task.CompletedTask;
        });
        Assert.True(options.DisableEntityCaching);
        options.DisableEntityCaching = false;
        try { Assert.Equal(IssuerEntryDecision.Unavailable, await poc.ValidateFromAsync("alpha", entry)); }
        finally { options.DisableEntityCaching = true; }
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
    }

    [Fact]
    public async Task Native_store_bridge_propagates_request_cancellation()
    {
        await using var poc = await NativeIssuerFixture.CreateAsync(fixture.Environment);
        var entry = await poc.SeedAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poc.Probe.ValidateAsync(entry, cancellation.Token));
        Assert.Equal(IssuerEntryDecision.Valid, await poc.ValidateFromAsync("alpha", entry));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
