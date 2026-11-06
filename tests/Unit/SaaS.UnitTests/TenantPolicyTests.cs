using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Domain;
using Xunit;
namespace SaaS.UnitTests;

public sealed class TenantPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static TenantStatus Ready() => new(Guid.NewGuid(), SubscriptionStatus.Active, Now.AddDays(1), OperationStatus.None, 4, null, null, 1);

    [Theory]
    [InlineData("alpha")]
    [InlineData("a")]
    [InlineData("team-42")]
    public void Slugs_preserve_canonical_values(string value) => Assert.Equal(value, TenantSlug.Parse(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("Alpha")]
    [InlineData("alpha/")]
    [InlineData("../alpha")]
    [InlineData("a--b")]
    [InlineData("a-")]
    [InlineData("alpha%2fbeta")]
    [InlineData("аlpha")]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("connect")]
    [InlineData("default")]
    [InlineData("health")]
    [InlineData("setup")]
    [InlineData("media")]
    public void Invalid_or_reserved_slugs_are_rejected(string value) => Assert.Throws<TenantContractException>(() => TenantSlug.Parse(value));

    [Fact]
    public void Readiness_requires_current_lifecycle_and_all_operation_gates()
    {
        var status = Ready();
        Assert.True(TenantPolicy.IsOperational(status, Now));
        Assert.False(TenantPolicy.IsOperational(status with { ExpiresAt = Now }, Now));
        Assert.False(TenantPolicy.IsOperational(status with { Subscription = (SubscriptionStatus)999 }, Now));
        Assert.False(TenantPolicy.IsOperational(status with { Operation = (OperationStatus)999 }, Now));
        Assert.False(TenantPolicy.IsOperational(status with { Subscription = SubscriptionStatus.Suspended }, Now));
        Assert.False(TenantPolicy.IsOperational(status with { Subscription = SubscriptionStatus.PendingDeletion }, Now));
        Assert.False(TenantPolicy.IsOperational(status with { OperationId = Guid.NewGuid() }, Now));
        Assert.False(TenantPolicy.IsOperational(status with { SecurityRevalidationRequired = true }, Now));
        foreach (var operation in Enum.GetValues<OperationStatus>().Where(x => x != OperationStatus.None))
            Assert.False(TenantPolicy.IsOperational(status with { Operation = operation }, Now));
    }

    [Fact]
    public void Provision_completion_clears_only_its_own_operation_and_increments_common_version()
    {
        var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 2, 4);
        var status = Ready() with { Operation = OperationStatus.Provisioning, OperationId = stamp.OperationId, OwnerId = stamp.OwnerId, Fence = stamp.Fence };
        var completed = TenantPolicy.CompleteProvisioning(status, stamp, Now);
        Assert.Equal(OperationStatus.None, completed.Operation);
        Assert.Null(completed.OperationId);
        Assert.Null(completed.OwnerId);
        Assert.Equal(5, completed.RegistryVersion);
        Assert.Equal(status.Subscription, completed.Subscription);
        Assert.Equal(status.ExpiresAt, completed.ExpiresAt);
        Assert.Equal(status.TenantId, completed.TenantId);
    }

    [Fact]
    public void Stale_worker_or_new_lifecycle_cannot_open_registry()
    {
        var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 2, 4);
        var status = Ready() with { Operation = OperationStatus.Provisioning, OperationId = stamp.OperationId, OwnerId = stamp.OwnerId, Fence = 2 };
        TenantStatus[] changed = [status with { RegistryVersion = 5 }, status with { Fence = 3 }, status with { OwnerId = Guid.NewGuid() }, status with { OperationId = Guid.NewGuid() }, status with { Subscription = SubscriptionStatus.Suspended }, status with { Subscription = SubscriptionStatus.PendingDeletion }, status with { ExpiresAt = Now }, status with { SecurityRevalidationRequired = true }];
        foreach (var other in changed)
            Assert.Throws<TenantContractException>(() => TenantPolicy.CompleteProvisioning(other, stamp, Now));
    }

    [Fact]
    public void Identity_must_be_exactly_one_nonempty_matching_record()
    {
        var expected = Guid.NewGuid();
        TenantIdentity.Verify(expected, expected, 1);
        Assert.Throws<TenantContractException>(() => TenantIdentity.Verify(expected, Guid.NewGuid(), 1));
        Assert.Throws<TenantContractException>(() => TenantIdentity.Verify(expected, null, 0));
        Assert.Throws<TenantContractException>(() => TenantIdentity.Verify(expected, expected, 2));
        Assert.Throws<TenantContractException>(() => TenantIdentity.Verify(Guid.Empty, Guid.Empty, 1));
    }

    [Fact]
    public void Links_and_cache_partitions_are_tenant_scoped_and_unambiguous()
    {
        var alpha = new TenantContext(Guid.NewGuid(), "alpha", "alpha");
        var beta = new TenantContext(Guid.NewGuid(), "beta", "beta");
        Assert.Equal("/alpha/health/ready", TenantAddress.Link(alpha, "health/ready"));
        Assert.Equal("/beta/health/ready", TenantAddress.Link(beta, "health/ready"));
        Assert.NotEqual(TenantAddress.CacheKey(alpha, "page", "home"), TenantAddress.CacheKey(beta, "page", "home"));
        Assert.NotEqual(TenantAddress.CacheKey(alpha, "a:b", "c"), TenantAddress.CacheKey(alpha, "a", "b:c"));
    }

    [Theory]
    [InlineData("//outside")]
    [InlineData("../beta")]
    [InlineData("a/../beta")]
    [InlineData("\\beta")]
    [InlineData("https://example.test")]
    [InlineData("%2e%2e/beta")]
    public void Links_cannot_escape_tenant_prefix(string path) => Assert.Throws<TenantContractException>(() => TenantAddress.Link(new TenantContext(Guid.NewGuid(), "alpha", "alpha"), path));
}
