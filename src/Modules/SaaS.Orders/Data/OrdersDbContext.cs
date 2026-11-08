using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SaaS.Orders.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<OrderRevision> Revisions => Set<OrderRevision>();
    public DbSet<OrderReceipt> Receipts => Set<OrderReceipt>();
    public static OrdersDbContext Open(string connection) => new(new DbContextOptionsBuilder<OrdersDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "orders")).Options);
    public static OrdersDbContext Open(DbConnection connection) => new(new DbContextOptionsBuilder<OrdersDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "orders")).Options);
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("orders");
        var order = model.Entity<OrderRow>();
        order.ToTable("Orders", table => table.HasCheckConstraint("CK_OrderState",
            "[Id] <> '00000000-0000-0000-0000-000000000000' AND [CreatedBy] <> '00000000-0000-0000-0000-000000000000' AND [Revision] > 0 AND [Amount] >= 0 AND [Status] IN (1,2,3,4) AND ([Status] NOT IN (2,3) OR [ApprovalRequired] = 1)"));
        order.HasKey(x => x.Id); order.Property(x => x.Id).ValueGeneratedNever();
        order.Property(x => x.Number).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        order.HasIndex(x => x.Number).IsUnique();
        order.Property(x => x.CustomerName).HasMaxLength(160);
        order.Property(x => x.Amount).HasPrecision(18, 2);
        order.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        order.Property(x => x.Notes).HasMaxLength(2000);
        order.Property(x => x.Revision).IsConcurrencyToken();
        order.Property(x => x.Version).IsRowVersion();
        order.HasIndex(x => new { x.CreatedBy, x.CreatedAt, x.Id });
        order.HasIndex(x => new { x.CreatedAt, x.Id });
        var revision = model.Entity<OrderRevision>();
        revision.ToTable("Revisions", table => table.HasCheckConstraint("CK_OrderRevision",
            "[Revision] > 0 AND [ActorId] <> '00000000-0000-0000-0000-000000000000' AND [Action] IN (1,2,3,4,5) AND [Status] IN (1,2,3,4) AND ([Status] NOT IN (2,3) OR [ApprovalRequired] = 1)"));
        revision.HasKey(x => new { x.OrderId, x.Revision });
        revision.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        var receipt = model.Entity<OrderReceipt>();
        receipt.ToTable("Receipts", table => table.HasCheckConstraint("CK_OrderReceipt",
            "[TenantId] <> '00000000-0000-0000-0000-000000000000' AND [ActorId] <> '00000000-0000-0000-0000-000000000000' AND [Action] IN (1,2,3,4,5) AND [ResultRevision] > 0 AND [ExpiresAt] > [CompletedAt]"));
        receipt.HasKey(x => x.PartitionHash);
        receipt.Property(x => x.PartitionHash).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        receipt.Property(x => x.RequestHash).HasMaxLength(64).IsUnicode(false);
        receipt.Property(x => x.ClientId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        receipt.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        receipt.HasIndex(x => x.ExpiresAt);
    }
    private void Guard()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<OrderRevision>().Any(x => x.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<OrderReceipt>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("order_history_append_only");
        foreach (var entry in ChangeTracker.Entries<OrderRow>())
        {
            if (entry.State == EntityState.Deleted) throw new InvalidOperationException("order_delete_forbidden");
            if (entry.State == EntityState.Added && (entry.Entity.Status != OrderStatus.Draft || entry.Entity.Revision != 1 || entry.Entity.ApprovalRequired))
                throw new InvalidOperationException("order_initial_state_invalid");
            if (entry.State == EntityState.Modified && (entry.Property(x => x.CreatedBy).IsModified || entry.Property(x => x.CreatedAt).IsModified
                || entry.Property(x => x.Revision).OriginalValue == long.MaxValue || entry.Entity.Revision != entry.Property(x => x.Revision).OriginalValue + 1
                || entry.Property(x => x.ApprovalRequired).OriginalValue && !entry.Entity.ApprovalRequired))
                throw new InvalidOperationException("order_invariant_violation");
        }
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { Guard(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { Guard(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}

public sealed class OrdersDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) => OrdersDbContext.Open(SaaS.Abstractions.Tenancy.BundleConnection.ForDesignTime("Server=localhost;Database=saas_design_only;Integrated Security=True"));
}
