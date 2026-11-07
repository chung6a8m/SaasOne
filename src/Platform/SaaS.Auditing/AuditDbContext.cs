using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Auditing;

public sealed class TenantAuditEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string? OperatorActor { get; set; }
    public string Action { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public long SecurityGeneration { get; set; }
    public long ResultRevision { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<TenantAuditEvent> Events => Set<TenantAuditEvent>();
    public static AuditDbContext Open(string connection) => new(new DbContextOptionsBuilder<AuditDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "audit")).Options);
    public static AuditDbContext Open(DbConnection connection) => new(new DbContextOptionsBuilder<AuditDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "audit")).Options);
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("audit");
        var audit = model.Entity<TenantAuditEvent>();
        audit.ToTable("Events", table => table.HasCheckConstraint("CK_AuditEvent", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [TenantId] <> '00000000-0000-0000-0000-000000000000' AND [SecurityGeneration] > 0 AND [ResultRevision] > 0"));
        audit.HasKey(x => x.Id); audit.Property(x => x.Id).ValueGeneratedNever();
        audit.Property(x => x.Action).HasMaxLength(64).IsUnicode(false);
        audit.Property(x => x.OperatorActor).HasMaxLength(128);
        audit.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
        audit.HasIndex(x => new { x.TenantId, x.OccurredAt });
    }
    private void Guard()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<TenantAuditEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("tenant_audit_append_only");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { Guard(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { Guard(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}

public sealed class AuditDesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) => AuditDbContext.Open("Server=localhost;Database=saas_design_only;Integrated Security=True");
}

/// <summary>Never owns or commits a separate connection/transaction. The caller commits the complete unit.</summary>
public static class TenantAuditWriter
{
    public static async Task AppendAsync(TenantTransaction transaction, TenantAuditEvent entry, CancellationToken ct)
    {
        await using var database = AuditDbContext.Open(transaction.Connection);
        await database.Database.UseTransactionAsync(transaction.Transaction, ct);
        database.Events.Add(entry);
        await database.SaveChangesAsync(ct);
    }
}
