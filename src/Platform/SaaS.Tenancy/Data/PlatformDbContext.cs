using Microsoft.EntityFrameworkCore;
namespace SaaS.Tenancy.Data;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<TenantRegistration> Tenants => Set<TenantRegistration>();
    public DbSet<ProvisioningOperation> Operations => Set<ProvisioningOperation>();
    public DbSet<OperationalAudit> Audit => Set<OperationalAudit>();
    public DbSet<PlatformBootstrap> Bootstrap => Set<PlatformBootstrap>();
}
