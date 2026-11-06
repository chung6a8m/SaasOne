using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace SaaS.Tenancy.Data;

public sealed partial class PlatformDbContext
{
    public DbSet<IdentityAccount> IdentityAccounts => Set<IdentityAccount>();
    public DbSet<IdentitySubject> IdentitySubjects => Set<IdentitySubject>();
    public DbSet<IdentityMembership> IdentityMemberships => Set<IdentityMembership>();
    public DbSet<IdentityTenantSecurity> IdentityTenantSecurity => Set<IdentityTenantSecurity>();
    public DbSet<IdentityClient> IdentityClients => Set<IdentityClient>();
    public DbSet<IdentitySession> IdentitySessions => Set<IdentitySession>();
    public DbSet<IdentityFamily> IdentityFamilies => Set<IdentityFamily>();
    public DbSet<IdentityRefresh> IdentityRefreshes => Set<IdentityRefresh>();
    public DbSet<IdentityInstallation> IdentityInstallations => Set<IdentityInstallation>();
}

internal static class IdentityModel
{
    private static PropertyBuilder<string> Exact(PropertyBuilder<string> property, int length) =>
        property.HasMaxLength(length).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");

    public static void Configure(ModelBuilder model)
    {
        var account = model.Entity<IdentityAccount>();
        account.ToTable("Accounts", "identity", table => table.HasCheckConstraint("CK_IdentityAccount_Generation", "[Generation] > 0"));
        account.HasKey(x => x.Id);
        account.Property(x => x.Id).ValueGeneratedNever();
        account.Property(x => x.Version).IsRowVersion();

        var subject = model.Entity<IdentitySubject>();
        subject.ToTable("Subjects", "identity");
        subject.HasKey(x => new { x.Issuer, x.Subject });
        Exact(subject.Property(x => x.Issuer), 256);
        Exact(subject.Property(x => x.Subject), 256);
        subject.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.GlobalUserId).OnDelete(DeleteBehavior.Restrict);

        var member = model.Entity<IdentityMembership>();
        member.ToTable("Memberships", "identity", table => table.HasCheckConstraint("CK_IdentityMembership_Generation", "[Generation] > 0"));
        member.HasKey(x => new { x.GlobalUserId, x.TenantId });
        member.Property(x => x.Version).IsRowVersion();
        member.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.GlobalUserId).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<TenantRegistration>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        var security = model.Entity<IdentityTenantSecurity>();
        security.ToTable("TenantSecurity", "identity", table => table.HasCheckConstraint("CK_IdentityTenant_Generation", "[Generation] > 0"));
        security.HasKey(x => x.TenantId);
        security.Property(x => x.TenantId).ValueGeneratedNever();
        security.Property(x => x.Version).IsRowVersion();
        security.HasOne<TenantRegistration>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        var client = model.Entity<IdentityClient>();
        client.ToTable("Clients", "identity");
        client.HasKey(x => x.ClientId);
        Exact(client.Property(x => x.ClientId), 96);
        Exact(client.Property(x => x.ShellName), 48);
        Exact(client.Property(x => x.RedirectUri), 512);
        Exact(client.Property(x => x.LogoutRedirectUri), 512);
        client.HasIndex(x => x.TenantId).IsUnique();
        client.HasOne<TenantRegistration>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        var session = model.Entity<IdentitySession>();
        session.ToTable("Sessions", "identity");
        session.HasKey(x => x.Id);
        session.Property(x => x.Id).ValueGeneratedNever();
        Exact(session.Property(x => x.Subject), 256);
        Exact(session.Property(x => x.NativeSecurityStamp), 256);
        session.Property(x => x.Version).IsRowVersion();
        session.HasIndex(x => new { x.GlobalUserId, x.TenantId });
        session.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.GlobalUserId).OnDelete(DeleteBehavior.Restrict);

        var family = model.Entity<IdentityFamily>();
        family.ToTable("Families", "identity");
        family.HasKey(x => x.Id);
        family.Property(x => x.Id).ValueGeneratedNever();
        Exact(family.Property(x => x.Issuer), 256);
        Exact(family.Property(x => x.Subject), 256);
        Exact(family.Property(x => x.ClientId), 96);
        Exact(family.Property(x => x.AuthorizationId), 128);
        family.Property(x => x.Version).IsRowVersion();
        family.HasIndex(x => x.AuthorizationId).IsUnique();
        family.HasIndex(x => new { x.GlobalUserId, x.TenantId });
        family.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.GlobalUserId).OnDelete(DeleteBehavior.Restrict);
        family.HasOne<IdentitySession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        family.HasOne<TenantRegistration>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        var refresh = model.Entity<IdentityRefresh>();
        refresh.ToTable("RefreshUses", "identity");
        refresh.HasKey(x => x.TokenHash);
        Exact(refresh.Property(x => x.TokenHash), 64).IsFixedLength();
        refresh.Property(x => x.Version).IsRowVersion();
        refresh.HasOne<IdentityFamily>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);

        var installation = model.Entity<IdentityInstallation>();
        installation.ToTable("Installations", "identity");
        installation.HasKey(x => x.NativePrefix);
        Exact(installation.Property(x => x.NativePrefix), 32);
        Exact(installation.Property(x => x.Issuer), 256);
        Exact(installation.Property(x => x.FeatureHash), 64).IsFixedLength();
        Exact(installation.Property(x => x.MigrationHash), 64).IsFixedLength();
    }
}
