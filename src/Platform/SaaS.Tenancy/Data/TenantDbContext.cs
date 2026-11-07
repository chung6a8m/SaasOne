using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Data;

public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options) : DbContext(options)
{
    public DbSet<TenantDatabaseIdentity> Identity => Set<TenantDatabaseIdentity>();
    public DbSet<TenantBaseline> Baseline => Set<TenantBaseline>();

    public DbSet<TenantApplicationProfile> ApplicationProfile => Set<TenantApplicationProfile>();

    public DbSet<TenantWriteGateRecord> WriteGate => Set<TenantWriteGateRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("tenancy");
        var profile = model.Entity<TenantApplicationProfile>();
        profile.ToTable("ApplicationProfile", table => table.HasCheckConstraint("CK_ApplicationProfile", "[Id] = 1 AND [Version] > 0 AND [TenantId] <> '00000000-0000-0000-0000-000000000000'"));
        profile.HasKey(x => x.Id); profile.Property(x => x.Id).ValueGeneratedNever();
        profile.Property(x => x.Plan).HasMaxLength(64).IsUnicode(false);
        profile.Property(x => x.ManifestHash).HasMaxLength(64).IsUnicode(false);
        var gate = model.Entity<TenantWriteGateRecord>();
        gate.ToTable("WriteGate", table => table.HasCheckConstraint("CK_TenantWriteGate", "[Id] = 1 AND [Version] > 0 AND [Fence] > 0 AND [TenantId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000'"));
        gate.HasKey(x => x.Id); gate.Property(x => x.Id).ValueGeneratedNever();
        gate.Property(x => x.Version).IsConcurrencyToken();
        var identity = model.Entity<TenantDatabaseIdentity>();
        identity.ToTable("Identity", table =>
        {
            table.HasCheckConstraint("CK_TenantIdentity_Singleton", "[Id] = 1");
            table.HasCheckConstraint("CK_TenantIdentity_Nonempty", "[TenantId] <> '00000000-0000-0000-0000-000000000000' AND [DatabaseNonce] <> '00000000-0000-0000-0000-000000000000'");
            table.HasTrigger("ImmutableTenantDatabaseIdentity");
            table.UseSqlOutputClause(false);
        });
        identity.HasKey(x => x.Id);
        identity.Property(x => x.Id).ValueGeneratedNever();
        identity.Property(x => x.TenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        identity.Property(x => x.DatabaseNonce).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        identity.Property(x => x.InitialOperationId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        identity.Property(x => x.CreatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        var baseline = model.Entity<TenantBaseline>();
        baseline.ToTable("Baseline", table => table.HasCheckConstraint("CK_TenantBaseline_Singleton", "[Id] = 1"));
        baseline.HasKey(x => x.Id);
        baseline.Property(x => x.Id).ValueGeneratedNever();
        baseline.Property(x => x.Recipe).HasMaxLength(128).IsUnicode(false);
        baseline.Property(x => x.FeatureHash).HasMaxLength(64).IsUnicode(false);
        baseline.Property(x => x.OrchardMigrationHash).HasMaxLength(64).IsUnicode(false);
    }

    private void GuardIdentity()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<TenantDatabaseIdentity>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new TenantContractException("tenant_identity_immutable");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardIdentity();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardIdentity();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
