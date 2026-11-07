using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Data;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M3ModelTests
{
    [Fact]
    public void Operation_state_and_recovery_authority_remain_platform_owned_with_independent_acl_generation()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer().Options);
        var tenant = db.Model.FindEntityType(typeof(TenantRegistration))!;
        Assert.NotNull(tenant.FindProperty("AuthorizationGeneration"));
        Assert.NotNull(tenant.FindProperty("RetiredAt"));
        var operation = db.Model.GetEntityTypes().SingleOrDefault(x => x.ClrType.Name == "ResourceOperation");
        Assert.NotNull(operation);
        Assert.Equal("platform", operation.GetSchema());
        Assert.True(operation.FindProperty("ExpectedRegistryVersion")!.IsConcurrencyToken);
        Assert.Equal("platform", db.Model.GetEntityTypes().Single(x => x.ClrType.Name == "RecoveryAuthority").GetSchema());
        Assert.Equal("platform", db.Model.FindEntityType(typeof(OperationalAudit))!.GetSchema());
    }
    [Fact]
    public void Access_and_customer_audit_have_distinct_owners_not_platform_audit()
    {
        using var access = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseSqlServer().Options);
        using var audit = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer().Options);
        Assert.Equal("access", access.Model.GetDefaultSchema());
        Assert.Equal("audit", audit.Model.GetDefaultSchema());
        Assert.All(access.Model.GetEntityTypes(), x => Assert.Equal("access", x.GetSchema()));
        Assert.All(audit.Model.GetEntityTypes(), x => Assert.Equal("audit", x.GetSchema()));
        Assert.Null(access.Model.FindEntityType(typeof(OperationalAudit)));
        Assert.Null(audit.Model.FindEntityType(typeof(OperationalAudit)));
        Assert.True(access.Model.FindEntityType(typeof(AclState))!.FindProperty(nameof(AclState.Revision))!.IsConcurrencyToken);
        Assert.Equal(128, access.Model.FindEntityType(typeof(UserGrantRow))!.FindProperty(nameof(UserGrantRow.Permission))!.GetMaxLength());
    }
    [Fact]
    public void Tenant_audit_cannot_be_updated_or_deleted_even_through_the_context()
    {
        using var audit = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer().Options);
        var item = new TenantAuditEvent { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ActorId = Guid.NewGuid(), Action = "AclChanged" };
        audit.Attach(item);
        item.Action = "Altered";
        var error = Assert.Throws<InvalidOperationException>(() => audit.SaveChanges());
        Assert.Equal("tenant_audit_append_only", error.Message);
        audit.Entry(item).State = EntityState.Deleted;
        error = Assert.Throws<InvalidOperationException>(() => audit.SaveChanges());
        Assert.Equal("tenant_audit_append_only", error.Message);
    }
}
