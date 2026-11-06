// Retained snapshot of the EF-generated 20261106135648_PlatformBaselineV1 target model.
// Keep this frozen historical model independent from the live DbContext configuration.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SaaS.Tenancy.Data;

#nullable disable

namespace SaaS.Tenancy.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
partial class PlatformDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasDefaultSchema("platform").HasAnnotation("ProductVersion", "10.0.14")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
        modelBuilder.Entity("SaaS.Tenancy.Data.OperationalAudit", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<long>("Id"));
            b.Property<string>("Action").IsRequired().HasMaxLength(64).IsUnicode(false).HasColumnType("varchar(64)");
            b.Property<string>("Actor").IsRequired().HasMaxLength(128).HasColumnType("nvarchar(128)");
            b.Property<DateTimeOffset>("OccurredAt").HasColumnType("datetimeoffset");
            b.Property<Guid>("OperationId").HasColumnType("uniqueidentifier");
            b.Property<long>("RegistryVersion").HasColumnType("bigint");
            b.Property<Guid?>("TenantId").HasColumnType("uniqueidentifier");
            b.HasKey("Id");
            b.HasIndex("TenantId", "OperationId", "OccurredAt");
            b.ToTable("OperationalAudit", "platform");
        });
        modelBuilder.Entity("SaaS.Tenancy.Data.PlatformBootstrap", b =>
        {
            b.Property<int>("Id").HasColumnType("int");
            b.Property<int>("BaselineVersion").HasColumnType("int");
            b.Property<bool>("Completed").HasColumnType("bit");
            b.Property<Guid>("InstallationId").HasColumnType("uniqueidentifier");
            b.Property<string>("OperationHost").IsRequired().HasMaxLength(64).IsUnicode(false).HasColumnType("varchar(64)");
            b.HasKey("Id");
            b.ToTable("Bootstrap", "platform", t => t.HasCheckConstraint("CK_Bootstrap_Singleton", "[Id] = 1"));
        });
        modelBuilder.Entity("SaaS.Tenancy.Data.ProvisioningOperation", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uniqueidentifier");
            b.Property<int>("Checkpoint").HasColumnType("int");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("datetimeoffset");
            b.Property<string>("ErrorCode").HasMaxLength(64).IsUnicode(false).HasColumnType("varchar(64)");
            b.Property<bool>("Failed").HasColumnType("bit");
            b.Property<long>("Fence").IsConcurrencyToken().HasColumnType("bigint");
            b.Property<string>("OwnerHost").HasMaxLength(64).IsUnicode(false).HasColumnType("varchar(64)");
            b.Property<Guid?>("OwnerId").HasColumnType("uniqueidentifier");
            b.Property<string>("RequestHash").IsRequired().HasMaxLength(64).IsUnicode(false).HasColumnType("varchar(64)");
            b.Property<DateTimeOffset>("StartedAt").HasColumnType("datetimeoffset");
            b.Property<Guid>("TenantId").HasColumnType("uniqueidentifier");
            b.HasKey("Id");
            b.HasIndex("TenantId");
            b.ToTable("Operations", "platform");
        });
        modelBuilder.Entity("SaaS.Tenancy.Data.TenantRegistration", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uniqueidentifier");
            b.Property<string>("DatabaseName").IsRequired().HasMaxLength(128).IsUnicode(false).HasColumnType("varchar(128)");
            b.Property<Guid>("DatabaseNonce").HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("ExpiresAt").HasColumnType("datetimeoffset");
            b.Property<long>("Fence").HasColumnType("bigint");
            b.Property<string>("MigratorSecret").IsRequired().HasMaxLength(128).IsUnicode(false).HasColumnType("varchar(128)");
            b.Property<int>("Operation").HasColumnType("int");
            b.Property<Guid?>("OperationId").HasColumnType("uniqueidentifier");
            b.Property<Guid?>("OwnerId").HasColumnType("uniqueidentifier");
            b.Property<string>("Plan").IsRequired().HasMaxLength(48).IsUnicode(false).HasColumnType("varchar(48)");
            b.Property<long>("RegistryVersion").IsConcurrencyToken().HasColumnType("bigint");
            b.Property<string>("RuntimeSecret").IsRequired().HasMaxLength(128).IsUnicode(false).HasColumnType("varchar(128)");
            b.Property<bool>("SecurityRevalidationRequired").HasColumnType("bit");
            b.Property<string>("Slug").IsRequired().HasMaxLength(48).IsUnicode(false).HasColumnType("varchar(48)").UseCollation("Latin1_General_100_BIN2");
            b.Property<int>("Subscription").HasColumnType("int");
            b.HasKey("Id");
            b.HasIndex("DatabaseName").IsUnique();
            b.HasIndex("Slug").IsUnique();
            b.ToTable("Tenants", "platform", t =>
            {
                t.HasCheckConstraint("CK_Tenants_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                t.HasCheckConstraint("CK_Tenants_Operation", "([Operation] = 0 AND [OperationId] IS NULL AND [OwnerId] IS NULL) OR ([Operation] BETWEEN 1 AND 6 AND [OperationId] IS NOT NULL)");
                t.HasCheckConstraint("CK_Tenants_Subscription", "[Subscription] IN (1,2,3)");
                t.HasCheckConstraint("CK_Tenants_Version", "[RegistryVersion] > 0 AND [Fence] > 0");
            });
        });
        modelBuilder.Entity("SaaS.Tenancy.Data.ProvisioningOperation", b =>
        {
            b.HasOne("SaaS.Tenancy.Data.TenantRegistration", null).WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
#pragma warning restore 612, 618
    }
}
