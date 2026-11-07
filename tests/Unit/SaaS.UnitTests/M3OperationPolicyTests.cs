using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Domain;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M3OperationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static TenantStatus Current() => new(Guid.NewGuid(), SubscriptionStatus.Active, Now.AddDays(1), OperationStatus.PlanChanging, 20, Guid.NewGuid(), Guid.NewGuid(), 3);
    private static OperationStamp Stamp(TenantStatus status) => new(status.OperationId!.Value, status.OwnerId!.Value, status.Fence, status.RegistryVersion);
    [Fact]
    public void Completion_ends_only_its_own_gate_without_altering_subscription_or_expiry()
    {
        var current = Current();
        var result = OperationPolicy.Complete(current, Stamp(current), OperationStatus.PlanChanging, Now, true);
        Assert.Equal(current.Subscription, result.Subscription);
        Assert.Equal(current.ExpiresAt, result.ExpiresAt);
        Assert.Equal(current.RegistryVersion + 1, result.RegistryVersion);
        Assert.Equal(OperationStatus.None, result.Operation);
        Assert.Null(result.OperationId);
        Assert.Null(result.OwnerId);
    }
    [Theory]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.PendingDeletion)]
    public void Lifecycle_decision_wins_over_completion_and_retry(SubscriptionStatus subscription)
    {
        var current = Current();
        var changed = current with { Subscription = subscription, RegistryVersion = current.RegistryVersion + 1 };
        Assert.Throws<TenantContractException>(() => OperationPolicy.Complete(changed, Stamp(current), OperationStatus.PlanChanging, Now, true));
        Assert.Throws<TenantContractException>(() => OperationPolicy.Complete(changed, Stamp(changed), OperationStatus.PlanChanging, Now, true));
    }
    [Fact]
    public void Expiry_recovery_flag_readiness_and_stale_ownership_never_open()
    {
        var current = Current();
        foreach (var changed in new[] { current with { ExpiresAt = Now }, current with { SecurityRevalidationRequired = true }, current with { RegistryVersion = 21 }, current with { OwnerId = Guid.NewGuid() }, current with { Fence = 4 }, current with { OperationId = Guid.NewGuid() } })
            Assert.Throws<TenantContractException>(() => OperationPolicy.Complete(changed, Stamp(current), OperationStatus.PlanChanging, Now, true));
        Assert.Throws<TenantContractException>(() => OperationPolicy.Complete(current, Stamp(current), OperationStatus.PlanChanging, Now, false));
        Assert.Throws<TenantContractException>(() => OperationPolicy.Complete(current, Stamp(current), OperationStatus.Restoring, Now, true));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_unfinished_obligation_rejects_downgrade_without_changing_old_plan(int count)
    {
        Assert.Equal("Approval", OperationPolicy.SelectPlan("Approval", "Standard", new(ObligationState.Outstanding, count)));
        Assert.Equal("Standard", OperationPolicy.SelectPlan("Approval", "Standard", new(ObligationState.Clear, 0)));
        Assert.Equal("Approval", OperationPolicy.SelectPlan("Standard", "Approval", new(ObligationState.Outstanding, count)));
    }
    [Fact]
    public void Unknown_or_inconsistent_reader_result_is_not_an_empty_obligation_set()
    {
        foreach (var result in new[] { new ObligationResult(ObligationState.Unknown, 0), new(ObligationState.Clear, 2), new(ObligationState.Outstanding, 0), new((ObligationState)99, 0) })
            Assert.Throws<TenantContractException>(() => OperationPolicy.SelectPlan("Approval", "Standard", result));
    }
}
