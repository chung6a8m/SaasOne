using Microsoft.EntityFrameworkCore;
namespace SaaS.Tenancy.Data;

public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options) : DbContext(options)
{
    public DbSet<TenantDatabaseIdentity> Identity => Set<TenantDatabaseIdentity>();
    public DbSet<TenantBaseline> Baseline => Set<TenantBaseline>();
}
