using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
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
}
