using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Fact]
    public async Task Purged_tenant_cannot_register_a_new_native_client_when_none_existed_before_deletion()
    {
        await using var setup = await M3HttpAsync();
        var tenant = await PurgeTargetAsync();
        var current = await PurgeRegistrationAsync(tenant);
        var adapter = new PlanFixture(Environment.CustomerAdmin(tenant));
        await new TenantOperations(new(), adapter, adapter).PurgeAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct);
        var installer = new IdentityInstaller(new(), setup.Http.Deployment);
        await Assert.ThrowsAsync<TenantContractException>(() => installer.RegisterTenantAsync(Environment.InstallationId, tenant.Id, Ct));
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.False(await db.IdentityClients.AnyAsync(x => x.TenantId == tenant.Id && x.Enabled, Ct));
    }

    [Fact]
    public async Task Client_registration_rechecks_deletion_before_publishing_platform_authority()
    {
        await using var setup = await M3HttpAsync();
        var tenant = await PurgeTargetAsync(pending: false);
        var original = setup.Http.Deployment;
        var deletion = 0;
        var deployment = new IdentityDeployment(original.Issuer, original.SigningCertificate, original.EncryptionCertificate,
            original.DataProtectionDirectory, original.NativePrefix, id =>
            {
                // This deployment boundary occurs after the installer initially reads the tenant.
                // A newer lifecycle decision must win even if native creation has already begun.
                if (id == tenant.Id && Interlocked.Exchange(ref deletion, 1) == 0)
                    new IdentityOperations(new()).SetTenantAsync(tenant.Id, SubscriptionStatus.PendingDeletion,
                        DateTimeOffset.UtcNow.AddDays(1), Ct).GetAwaiter().GetResult();
                return setup.Http.Secret(id);
            });
        await Assert.ThrowsAsync<TenantContractException>(() =>
            new IdentityInstaller(new(), deployment).RegisterTenantAsync(Environment.InstallationId, tenant.Id, Ct));
        Assert.Equal(1, deletion);
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        Assert.Equal(SubscriptionStatus.PendingDeletion, (await db.Tenants.SingleAsync(x => x.Id == tenant.Id, Ct)).Subscription);
        Assert.False(await db.IdentityClients.AnyAsync(x => x.TenantId == tenant.Id && x.Enabled, Ct));
    }

}
