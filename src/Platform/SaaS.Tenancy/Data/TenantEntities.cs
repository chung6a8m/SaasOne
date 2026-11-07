namespace SaaS.Tenancy.Data;

public sealed class TenantDatabaseIdentity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DatabaseNonce { get; set; }
    public Guid InitialOperationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TenantBaseline
{
    public int Id { get; set; }
    public int SeedVersion { get; set; }
    public string Recipe { get; set; } = string.Empty;
    public string FeatureHash { get; set; } = string.Empty;
    public string OrchardMigrationHash { get; set; } = string.Empty;
}

/// <summary>Deployment-owned manifest. Runtime reads it but cannot reconcile or repair it.</summary>
public sealed class TenantApplicationProfile
{
    public int Id { get; set; } = 1;
    public Guid TenantId { get; set; }
    public string Plan { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public int Version { get; set; } = 1;
}

public sealed class TenantWriteGateRecord
{
    public int Id { get; set; } = 1;
    public Guid TenantId { get; set; }
    public bool IsOpen { get; set; }
    public long Version { get; set; } = 1;
    public Guid OperationId { get; set; }
    public long Fence { get; set; } = 1;
}
