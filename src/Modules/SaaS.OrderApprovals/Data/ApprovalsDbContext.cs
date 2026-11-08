using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SaaS.OrderApprovals.Data;

public sealed class ApprovalsDbContext(DbContextOptions<ApprovalsDbContext> options) : DbContext(options)
{
    public DbSet<ApprovalRequest> Requests => Set<ApprovalRequest>();
    public DbSet<ApprovalDecision> Decisions => Set<ApprovalDecision>();
    public static ApprovalsDbContext Open(string connection) => new(new DbContextOptionsBuilder<ApprovalsDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "approvals")).Options);
    public static ApprovalsDbContext Open(DbConnection connection) => new(new DbContextOptionsBuilder<ApprovalsDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "approvals")).Options);
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("approvals");
        var request = model.Entity<ApprovalRequest>();
        request.ToTable("Requests", table => table.HasCheckConstraint("CK_ApprovalRequest",
            "[OrderId] <> '00000000-0000-0000-0000-000000000000' AND [SubmittedRevision] > 1 AND [CreatedBy] <> '00000000-0000-0000-0000-000000000000' AND [SubmittedBy] <> '00000000-0000-0000-0000-000000000000' AND DATALENGTH([SubmittedVersion]) = 12"));
        request.HasKey(x => new { x.OrderId, x.SubmittedRevision });
        request.Property(x => x.SubmittedVersion).HasMaxLength(12).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        var decision = model.Entity<ApprovalDecision>();
        decision.ToTable("Decisions", table => table.HasCheckConstraint("CK_ApprovalDecision",
            "[SubmittedRevision] > 1 AND [ResultRevision] > [SubmittedRevision] AND [ResultRevision] - [SubmittedRevision] = 1 AND [DecidedBy] <> '00000000-0000-0000-0000-000000000000' AND (([Approved] = 1 AND [Reason] IS NULL) OR ([Approved] = 0 AND [Reason] IS NOT NULL AND LEN(LTRIM(RTRIM([Reason]))) > 0))"));
        decision.HasKey(x => new { x.OrderId, x.SubmittedRevision });
        decision.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => new { x.OrderId, x.SubmittedRevision }).OnDelete(DeleteBehavior.Restrict);
        decision.Property(x => x.Reason).HasMaxLength(1000);
    }
    private void Guard()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("approval_history_append_only");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { Guard(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { Guard(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}

public sealed class ApprovalsDesignTimeFactory : IDesignTimeDbContextFactory<ApprovalsDbContext>
{
    public ApprovalsDbContext CreateDbContext(string[] args) => ApprovalsDbContext.Open(SaaS.Abstractions.Tenancy.BundleConnection.ForDesignTime("Server=localhost;Database=saas_design_only;Integrated Security=True"));
}
