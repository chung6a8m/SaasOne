using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace SaaS.AccessControl.Data;

public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public DbSet<AclState> State => Set<AclState>();
    public DbSet<RoleRow> Roles => Set<RoleRow>();
    public DbSet<RoleGrantRow> RoleGrants => Set<RoleGrantRow>();
    public DbSet<UserRoleRow> Assignments => Set<UserRoleRow>();
    public DbSet<UserGrantRow> UserGrants => Set<UserGrantRow>();
    public DbSet<AclHistory> History => Set<AclHistory>();
    public static AccessDbContext Open(string connection) => new(new DbContextOptionsBuilder<AccessDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "access")).Options);
    public static AccessDbContext Open(DbConnection connection) => new(new DbContextOptionsBuilder<AccessDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "access")).Options);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("access");
        var state = model.Entity<AclState>();
        state.ToTable("State", table => table.HasCheckConstraint("CK_AccessState", "[Id] = 1 AND [Revision] > 0 AND [SecurityGeneration] > 0 AND [TenantId] <> '00000000-0000-0000-0000-000000000000' AND [AppliedOperationId] <> '00000000-0000-0000-0000-000000000000'"));
        state.HasKey(x => x.Id);
        state.Property(x => x.Id).ValueGeneratedNever();
        state.Property(x => x.Revision).IsConcurrencyToken();
        state.Property(x => x.AclHash).HasMaxLength(64).IsUnicode(false);
        var role = model.Entity<RoleRow>();
        role.ToTable("Roles"); role.HasKey(x => x.Id); role.Property(x => x.Id).ValueGeneratedNever();
        role.Property(x => x.Name).HasMaxLength(96).UseCollation("Latin1_General_100_BIN2");
        role.HasIndex(x => x.Name).IsUnique();
        var grants = model.Entity<RoleGrantRow>();
        grants.ToTable("RoleGrants", table => table.HasCheckConstraint("CK_RoleGrant_Effect", "[Effect] IN (0,1,2)"));
        grants.HasKey(x => new { x.RoleId, x.Permission });
        grants.Property(x => x.Permission).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        grants.HasOne<RoleRow>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        var assignments = model.Entity<UserRoleRow>();
        assignments.ToTable("Assignments"); assignments.HasKey(x => new { x.UserId, x.RoleId });
        assignments.HasOne<RoleRow>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        var users = model.Entity<UserGrantRow>();
        users.ToTable("UserGrants", table => table.HasCheckConstraint("CK_UserGrant_Effect", "[Effect] IN (0,1,2)"));
        users.HasKey(x => new { x.UserId, x.Permission });
        users.Property(x => x.Permission).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        var history = model.Entity<AclHistory>();
        history.ToTable("History"); history.HasKey(x => x.Id); history.Property(x => x.Id).ValueGeneratedNever();
        history.Property(x => x.AclHash).HasMaxLength(64).IsUnicode(false);
        history.HasIndex(x => new { x.TenantId, x.SecurityGeneration, x.Revision });
    }
}

public sealed class AccessDesignTimeFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args) => AccessDbContext.Open(SaaS.Abstractions.Tenancy.BundleConnection.ForDesignTime("Server=localhost;Database=saas_design_only;Integrated Security=True"));
}
