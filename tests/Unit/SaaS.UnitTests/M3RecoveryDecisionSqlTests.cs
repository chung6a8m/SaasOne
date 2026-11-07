using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Fact]
    public async Task Recovery_approved_document_cannot_change_after_apply_even_when_retry_resets_the_checkpoint()
    {
        var setup = await RecoveryReadyAsync();
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var current = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Decision(AclDocument.Empty), Ct);
        foreach (var crash in new[] { ResourceCheckpoint.AclApplied, ResourceCheckpoint.GateClosed })
        {
            current = await RegistrationAsync();
            var interrupted = new TenantOperations(new(), setup.Adapter, setup.Adapter, progress: (stage, _) =>
                stage == crash ? throw new InvalidOperationException("recovery_interrupted") : Task.CompletedTask);
            await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.ResumeRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, RecoveryApplier(), Ct));
        }
        var changed = new AclDocument([new(Guid.NewGuid(), "Unapproved replacement", [new("Sample.Read", PermissionEffect.Allow)])], [], []);
        current = await RegistrationAsync();
        Assert.Equal("recovery_decision_already_applied", (await Assert.ThrowsAsync<TenantContractException>(() =>
            service.ApproveRecoveryAsync(Alpha.Id, operation, current.RegistryVersion, Decision(changed), Ct))).Code);
        Assert.True((await RegistrationAsync()).SecurityRevalidationRequired);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("account")]
    [InlineData("authority")]
    [InlineData("expiry")]
    public async Task Recovery_finalization_rechecks_current_security_sources_after_customer_prepare(string fault)
    {
        var setup = await RecoveryReadyAsync();
        Guid user;
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            user = (await db.IdentitySessions.SingleAsync(x => x.Id == setup.Session, Ct)).GlobalUserId;
        var role = Guid.NewGuid();
        var document = new AclDocument([new(role, "Explicit current reader", [new("Sample.Read", PermissionEffect.Allow)])], [new(user, role)], []);
        var operation = Guid.NewGuid();
        var service = new TenantOperations(new(), setup.Adapter, setup.Adapter);
        await service.BeginRecoveryAsync(Alpha.Id, operation, setup.Tenant.RegistryVersion, Ct);
        var tenant = await RegistrationAsync();
        await service.ApproveRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, Decision(document), Ct);
        tenant = await RegistrationAsync();
        var clock = new TestClock();
        var resume = new TenantOperations(new(), setup.Adapter, setup.Adapter, clock, progress: async (stage, ct) =>
        {
            if (stage != ResourceCheckpoint.GatePrepared) return;
            if (fault == "membership") await new IdentityOperations(new()).SetMembershipAsync(user, Alpha.Id, false, ct);
            if (fault == "account") await new IdentityOperations(new()).SetAccountEnabledAsync(user, false, ct);
            if (fault == "authority")
            {
                await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
                await db.RecoveryAuthorities.ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, false), ct);
            }
            if (fault == "expiry") clock.Advance(TimeSpan.FromDays(370));
        });
        await Assert.ThrowsAnyAsync<Exception>(() => resume.ResumeRecoveryAsync(Alpha.Id, operation, tenant.RegistryVersion, RecoveryApplier(), Ct));
        var current = await RegistrationAsync();
        Assert.True(current.SecurityRevalidationRequired);
        Assert.Equal(OperationStatus.Restoring, current.Operation);
        Assert.False(TenantPolicy.IsOperational(current.Status(), clock.GetUtcNow()));
    }
}
