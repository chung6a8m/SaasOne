using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SaaS.Tenancy.Data;

// These factories generate provider-specific migrations. Creation never connects; controlled bundles receive their connection through child-only environment.
public sealed class PlatformDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer(SaaS.Abstractions.Tenancy.BundleConnection.ForDesignTime("Server=localhost;Database=saas_platform;Integrated Security=True"), sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "platform")).Options);
}
public sealed class TenantDesignTimeFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TenantDbContext>()
        .UseSqlServer(SaaS.Abstractions.Tenancy.BundleConnection.ForDesignTime("Server=localhost;Database=saas_design_only;Integrated Security=True"), sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);
}
