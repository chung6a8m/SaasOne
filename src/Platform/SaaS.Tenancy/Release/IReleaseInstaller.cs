using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Release;

/// <summary>Product-host composition supplies migration inventory and actual schema/native verification.</summary>
public interface IReleaseInstaller
{
    string ManifestSha256 { get; }
    Task ApplyAsync(TenantContext tenant, string plan, string connection, Guid operationId, Guid ownerId,
        Func<CancellationToken, Task> demandClosed, CancellationToken ct);
    Task VerifyAsync(TenantContext tenant, string plan, string connection, CancellationToken ct);
}
