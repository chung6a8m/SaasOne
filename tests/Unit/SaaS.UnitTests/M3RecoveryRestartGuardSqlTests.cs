using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Fact]
    public async Task Recovery_restart_platform_audit_failure_rolls_back_supersession_and_generations()
    {
        var setup = await RecoveryReadyAsync();
        var previous = Guid.NewGuid(); var replacement = Guid.NewGuid();
        await new TenantOperations(new(), setup.Adapter, setup.Adapter).BeginRecoveryAsync(Alpha.Id, previous, setup.Tenant.RegistryVersion, Ct);
        var before = await RegistrationAsync();
        await using var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin);
        var security = (await db.IdentityTenantSecurity.AsNoTracking().SingleAsync(x => x.TenantId == Alpha.Id, Ct)).Generation;
        var beta = Environment.Tenants[1].Id;
        var other = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == beta, Ct);
        await using var sql = await SqlOperations.OpenAsync(Environment.PlatformAdmin, Ct);
        await SqlOperations.ExecuteAsync(sql, "DENY INSERT ON platform.OperationalAudit TO saas_operator", Ct);
        try { Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, before.RegistryVersion)); }
        finally { await SqlOperations.ExecuteAsync(sql, "REVOKE INSERT ON platform.OperationalAudit FROM saas_operator", Ct); }
        var blocked = await RegistrationAsync();
        Assert.Equal(before.RegistryVersion, blocked.RegistryVersion); Assert.Equal(before.Fence, blocked.Fence);
        Assert.Equal(before.AuthorizationGeneration, blocked.AuthorizationGeneration); Assert.Equal(previous, blocked.OperationId);
        Assert.Equal(security, (await db.IdentityTenantSecurity.AsNoTracking().SingleAsync(x => x.TenantId == Alpha.Id, Ct)).Generation);
        Assert.Null((await db.ResourceOperations.AsNoTracking().SingleAsync(x => x.Id == previous, Ct)).CompletedAt);
        Assert.False(await db.ResourceOperations.AnyAsync(x => x.Id == replacement, Ct));
        Assert.False(await db.Audit.AnyAsync(x => x.OperationId == previous && x.Action == "RecoverySuperseded", Ct));
        Assert.Equal(0, await RestartRecoveryCommandAsync(previous, replacement, before.RegistryVersion));
        var unchanged = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == beta, Ct);
        Assert.Equal(other.RegistryVersion, unchanged.RegistryVersion); Assert.Equal(other.AuthorizationGeneration, unchanged.AuthorizationGeneration);
        Assert.Equal(other.OperationId, unchanged.OperationId); Assert.Equal(other.Subscription, unchanged.Subscription);
    }

    [Fact]
    public async Task Recovery_restart_replacement_operator_cannot_self_register_or_claim_another_login()
    {
        var setup = await RecoveryReadyAsync();
        var previous = Guid.NewGuid(); var replacement = Guid.NewGuid();
        await new TenantOperations(new(), setup.Adapter, setup.Adapter).BeginRecoveryAsync(Alpha.Id, previous, setup.Tenant.RegistryVersion, Ct);
        var before = await RegistrationAsync();
        using var successor = await UseSuccessorRecoveryOperatorAsync();
        var login = System.Environment.GetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_LOGIN")!;
        var connection = new TenancyConnections().Platform("operator");
        await using (var sql = await SqlOperations.OpenAsync(connection, Ct))
        {
            Assert.Equal(login, await SqlOperations.ScalarAsync(sql, "SELECT ORIGINAL_LOGIN()", Ct));
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => SqlOperations.ExecuteAsync(sql,
                "UPDATE platform.RecoveryAuthorities SET Enabled=1 WHERE Actor=@actor", Ct, ("@actor", "saas_platform_operator")))).Number);
        }
        System.Environment.SetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_LOGIN", "saas_platform_operator");
        try { Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, before.RegistryVersion)); }
        finally { System.Environment.SetEnvironmentVariable("SAAS_PLATFORM_OPERATOR_LOGIN", login); }
        await using (var db = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
        {
            await db.RecoveryAuthorities.Where(x => x.Actor == login).ExecuteUpdateAsync(set => set.SetProperty(x => x.RegisteredAt, DateTimeOffset.UtcNow.AddDays(1)), Ct);
            Assert.Equal(1, await RestartRecoveryCommandAsync(previous, replacement, before.RegistryVersion));
            Assert.False(await db.ResourceOperations.AnyAsync(x => x.Id == replacement, Ct));
        }
        Assert.Equal(before.RegistryVersion, (await RegistrationAsync()).RegistryVersion);
    }
}
