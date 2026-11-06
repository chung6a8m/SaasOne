namespace SaaS.Tenancy.Data;

// Application authority only. Native passwords, OpenID applications/tokens and
// authorizations remain owned by Orchard/YesSql, not by this EF migration stream.
public sealed class IdentityAccount
{
    public Guid Id { get; set; }
    public bool Enabled { get; set; } = true;
    public long Generation { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] Version { get; set; } = [];
}

public sealed class IdentitySubject
{
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public Guid GlobalUserId { get; set; }
}

public sealed class IdentityMembership
{
    public Guid GlobalUserId { get; set; }
    public Guid TenantId { get; set; }
    public long Generation { get; set; } = 1;
    public bool Active { get; set; }
    public byte[] Version { get; set; } = [];
}

public sealed class IdentityTenantSecurity
{
    public Guid TenantId { get; set; }
    public long Generation { get; set; } = 1;
    public byte[] Version { get; set; } = [];
}

public sealed class IdentityClient
{
    public string ClientId { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public string ShellName { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string LogoutRedirectUri { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class IdentitySession
{
    public Guid Id { get; set; }
    public Guid GlobalUserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string NativeSecurityStamp { get; set; } = string.Empty;
    public long AccountGeneration { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[] Version { get; set; } = [];
}

public sealed class IdentityFamily
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid GlobalUserId { get; set; }
    public Guid TenantId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string AuthorizationId { get; set; } = string.Empty;
    public long AccountGeneration { get; set; }
    public long TenantGeneration { get; set; }
    public long RegistryVersion { get; set; }
    public long MembershipGeneration { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset SubscriptionExpiresAt { get; set; }
    public DateTimeOffset RefreshIdleExpiresAt { get; set; }
    public DateTimeOffset RefreshAbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[] Version { get; set; } = [];
}

public sealed class IdentityRefresh
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public byte[] Version { get; set; } = [];
}

public sealed class IdentityInstallation
{
    public string NativePrefix { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string FeatureHash { get; set; } = string.Empty;
    public string MigrationHash { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
