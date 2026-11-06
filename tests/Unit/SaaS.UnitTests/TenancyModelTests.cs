using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SaaS.Tenancy.Data;
using Xunit;
namespace SaaS.UnitTests;

public sealed class TenancyModelTests
{
    // Metadata-only provider model inspection, never an alternative to the real SQL gate.
    private static PlatformDbContext Platform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer().Options);
    private static TenantDbContext Tenant() => new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer().Options);

    [Fact]
    public void Platform_and_customer_streams_have_exclusive_non_Orchard_schemas()
    {
        using var platform = Platform();
        using var tenant = Tenant();
        Assert.Equal("platform", platform.Model.GetDefaultSchema());
        Assert.Equal("tenancy", tenant.Model.GetDefaultSchema());
        Assert.All(platform.Model.GetEntityTypes(), entity => Assert.Equal("platform", entity.GetSchema()));
        Assert.All(tenant.Model.GetEntityTypes(), entity => Assert.Equal("tenancy", entity.GetSchema()));
        Assert.Null(platform.Model.FindEntityType(typeof(TenantDatabaseIdentity)));
        Assert.Null(tenant.Model.FindEntityType(typeof(TenantRegistration)));
    }

    [Fact]
    public void Registry_enforces_unique_slug_target_and_common_concurrency_version()
    {
        using var context = Platform();
        var model = context.Model.FindEntityType(typeof(TenantRegistration))!;
        Assert.True(model.FindProperty(nameof(TenantRegistration.RegistryVersion))!.IsConcurrencyToken);
        Assert.Equal(48, model.FindProperty(nameof(TenantRegistration.Slug))!.GetMaxLength());
        Assert.Equal(128, model.FindProperty(nameof(TenantRegistration.DatabaseName))!.GetMaxLength());
        Assert.Contains(model.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(TenantRegistration.Slug));
        Assert.Contains(model.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(TenantRegistration.DatabaseName));
        Assert.False(model.FindProperty(nameof(TenantRegistration.Slug))!.IsNullable);
    }

    [Fact]
    public void Identity_is_singleton_and_immutable_in_the_owned_model()
    {
        using var context = Tenant();
        var model = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TenantDatabaseIdentity))!;
        Assert.Contains(model.GetCheckConstraints(), constraint => constraint.Name == "CK_TenantIdentity_Singleton");
        Assert.Contains(model.GetCheckConstraints(), constraint => constraint.Name == "CK_TenantIdentity_Nonempty");
        Assert.Equal(PropertySaveBehavior.Throw, model.FindProperty(nameof(TenantDatabaseIdentity.TenantId))!.GetAfterSaveBehavior());
        Assert.Equal(PropertySaveBehavior.Throw, model.FindProperty(nameof(TenantDatabaseIdentity.DatabaseNonce))!.GetAfterSaveBehavior());
        Assert.Contains(model.GetDeclaredTriggers(), trigger => trigger.ModelName == "ImmutableTenantDatabaseIdentity");
    }

    [Fact]
    public void Audit_and_checkpoints_remain_in_the_platform_migration_stream()
    {
        using var context = Platform();
        var audit = context.Model.FindEntityType(typeof(OperationalAudit))!;
        var operation = context.Model.FindEntityType(typeof(ProvisioningOperation))!;
        Assert.Equal("platform", audit.GetSchema());
        Assert.Equal("platform", operation.GetSchema());
        Assert.Equal(128, audit.FindProperty(nameof(OperationalAudit.Actor))!.GetMaxLength());
        Assert.Equal(64, operation.FindProperty(nameof(ProvisioningOperation.RequestHash))!.GetMaxLength());
        Assert.True(operation.FindProperty(nameof(ProvisioningOperation.Fence))!.IsConcurrencyToken);
    }
}
