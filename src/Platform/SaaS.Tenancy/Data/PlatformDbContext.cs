using Microsoft.EntityFrameworkCore;
namespace SaaS.Tenancy.Data;

public sealed partial class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<TenantRegistration> Tenants => Set<TenantRegistration>();
    public DbSet<ProvisioningOperation> Operations => Set<ProvisioningOperation>();
    public DbSet<OperationalAudit> Audit => Set<OperationalAudit>();
    public DbSet<PlatformBootstrap> Bootstrap => Set<PlatformBootstrap>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("platform");
        IdentityModel.Configure(model);
        var tenant = model.Entity<TenantRegistration>();
        tenant.ToTable("Tenants", table =>
        {
            table.HasCheckConstraint("CK_Tenants_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Tenants_Version", "[RegistryVersion] > 0 AND [Fence] > 0");
            table.HasCheckConstraint("CK_Tenants_Subscription", "[Subscription] IN (1,2,3)");
            table.HasCheckConstraint("CK_Tenants_Operation", "([Operation] = 0 AND [OperationId] IS NULL AND [OwnerId] IS NULL) OR ([Operation] BETWEEN 1 AND 6 AND [OperationId] IS NOT NULL)");
        });
        tenant.HasKey(x => x.Id);
        tenant.Property(x => x.Id).ValueGeneratedNever();
        tenant.Property(x => x.Slug).HasMaxLength(48).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        tenant.Property(x => x.DatabaseName).HasMaxLength(128).IsUnicode(false);
        tenant.Property(x => x.RuntimeSecret).HasMaxLength(128).IsUnicode(false);
        tenant.Property(x => x.MigratorSecret).HasMaxLength(128).IsUnicode(false);
        tenant.Property(x => x.Plan).HasMaxLength(48).IsUnicode(false);
        tenant.Property(x => x.RegistryVersion).IsConcurrencyToken();
        tenant.HasIndex(x => x.Slug).IsUnique();
        tenant.HasIndex(x => x.DatabaseName).IsUnique();

        var operation = model.Entity<ProvisioningOperation>();
        operation.ToTable("Operations");
        operation.HasKey(x => x.Id);
        operation.Property(x => x.Id).ValueGeneratedNever();
        operation.Property(x => x.RequestHash).HasMaxLength(64).IsUnicode(false);
        operation.Property(x => x.ErrorCode).HasMaxLength(64).IsUnicode(false);
        operation.Property(x => x.OwnerHost).HasMaxLength(64).IsUnicode(false);
        operation.Property(x => x.Fence).IsConcurrencyToken();
        operation.HasOne<TenantRegistration>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        var audit = model.Entity<OperationalAudit>();
        audit.ToTable("OperationalAudit");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Actor).HasMaxLength(128);
        audit.Property(x => x.Action).HasMaxLength(64).IsUnicode(false);
        audit.HasIndex(x => new { x.TenantId, x.OperationId, x.OccurredAt });

        var bootstrap = model.Entity<PlatformBootstrap>();
        bootstrap.ToTable("Bootstrap", table => table.HasCheckConstraint("CK_Bootstrap_Singleton", "[Id] = 1"));
        bootstrap.HasKey(x => x.Id);
        bootstrap.Property(x => x.Id).ValueGeneratedNever();
        bootstrap.Property(x => x.OperationHost).HasMaxLength(64).IsUnicode(false);
    }
}
