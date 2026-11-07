using System.Data.Common;
namespace SaaS.Abstractions.Tenancy;

public enum ObligationState { Unknown = 0, Clear = 1, Outstanding = 2 }
public sealed record ObligationResult(ObligationState State, int Count);

/// <summary>Host-composed reader; must query markers independently of enabled business features.</summary>
public interface IApprovalObligationReader
{
    Task<ObligationResult> ReadAsync(TenantContext tenant, DbConnection connection, CancellationToken cancellationToken);
}

public interface IPlanReconciler
{
    Task MigrateAsync(TenantContext tenant, string targetPlan, CancellationToken cancellationToken);
    Task ApplyFeaturesAsync(TenantContext tenant, string targetPlan, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(TenantContext tenant, string targetPlan, CancellationToken cancellationToken);
}

public sealed record TenantTransaction(DbConnection Connection, DbTransaction Transaction);
public interface ITenantTransactionCoordinator
{
    Task<T> ExecuteAsync<T>(Guid tenantId, long gateVersion, Func<TenantTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
