using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Operations;
namespace SaaS.Tenancy.Data;

public sealed class ResourceOperation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public OperationStatus Kind { get; set; }
    public string SourcePlan { get; set; } = "";
    public string RequestedPlan { get; set; } = "";
    public string SelectedPlan { get; set; } = "";
    public ResourceCheckpoint Checkpoint { get; set; }
    public Guid OwnerId { get; set; }
    public string OwnerHost { get; set; } = "";
    public long Fence { get; set; }
    public long ExpectedRegistryVersion { get; set; }
    public long GateVersion { get; set; }
    public long AuthorizationGeneration { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedAcl { get; set; }
    public string? ApprovedHash { get; set; }
    public bool AllowApprovalUpgrade { get; set; }
    public string? ApprovalReason { get; set; }
    public OperationStamp Stamp() => new(Id, OwnerId, Fence, ExpectedRegistryVersion);
}

/// <summary>Provisioned out of band by platform administration, never inferred from tenant ACL.</summary>
public sealed class RecoveryAuthority
{
    public string Actor { get; set; } = "";
    public bool Enabled { get; set; }
    public bool CanChangePlan { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public string RegisteredBy { get; set; } = "";
}

public sealed partial class PlatformDbContext
{
    public DbSet<ResourceOperation> ResourceOperations => Set<ResourceOperation>();
    public DbSet<RecoveryAuthority> RecoveryAuthorities => Set<RecoveryAuthority>();
}

internal static class ResourceOperationsModel
{
    internal static void Configure(ModelBuilder model)
    {
        var operation = model.Entity<ResourceOperation>();
        operation.ToTable("ResourceOperations", table => table.HasCheckConstraint("CK_ResourceOperation", "[Kind] IN (3,4,5,7) AND [Fence] > 0 AND [ExpectedRegistryVersion] > 0 AND [AuthorizationGeneration] > 0 AND [Checkpoint] BETWEEN 0 AND 8"));
        operation.HasKey(x => x.Id);
        operation.Property(x => x.Id).ValueGeneratedNever();
        operation.Property(x => x.Fence).IsConcurrencyToken();
        operation.Property(x => x.ExpectedRegistryVersion).IsConcurrencyToken();
        operation.Property(x => x.OwnerHost).HasMaxLength(64).IsUnicode(false);
        operation.Property(x => x.SourcePlan).HasMaxLength(48).IsUnicode(false);
        operation.Property(x => x.RequestedPlan).HasMaxLength(48).IsUnicode(false);
        operation.Property(x => x.SelectedPlan).HasMaxLength(48).IsUnicode(false);
        operation.Property(x => x.RequestedBy).HasMaxLength(128);
        operation.Property(x => x.ApprovedBy).HasMaxLength(128);
        operation.Property(x => x.ApprovedHash).HasMaxLength(64).IsUnicode(false);
        operation.Property(x => x.ApprovalReason).HasMaxLength(1000);
        operation.HasIndex(x => new { x.TenantId, x.StartedAt });
        operation.HasOne<TenantRegistration>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        var authority = model.Entity<RecoveryAuthority>();
        authority.ToTable("RecoveryAuthorities");
        authority.HasKey(x => x.Actor);
        authority.Property(x => x.Actor).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        authority.Property(x => x.RegisteredBy).HasMaxLength(128);
    }
}
