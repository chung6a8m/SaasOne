using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Operations;

/// <summary>
/// Owns one physical connection and transaction for the whole command, including audit.
/// The execution policy is deliberately NON-retrying. Contexts enlisted here must not enable per-context retries.
/// A caller may retry the whole command with its durable idempotency key after resolving an ambiguous commit.
/// </summary>
public sealed class TenantTransactionCoordinator(string connection) : ITenantTransactionCoordinator
{
    public async Task<T> ExecuteAsync<T>(Guid tenantId, long gateVersion, Func<TenantTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        // Outlives SQL transaction disposal; acquired-to-end samples approximate lock hold.
        using var stage = SaaSStageActivity.Start(SaaSStage.TenantTransaction);
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        return await ExecuteOnOpenAsync(sql, tenantId, gateVersion, action, cancellationToken);
    }

    /// <summary>
    /// Owns the fixed-source connection across a fresh, out-of-transaction preflight
    /// and the subsequent transaction. The transaction rechecks the gate under its
    /// shared lock; the preflight result is only a version fence, never authority.
    /// No connection is shared across operations or reused after this call returns.
    /// </summary>
    public async Task<T> ExecuteWithPreflightAsync<T>(Guid tenantId,
        Func<SqlConnection, CancellationToken, Task<long>> preflight,
        Func<TenantTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(action);
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        var gateVersion = await preflight(sql, cancellationToken);
        using var stage = SaaSStageActivity.Start(SaaSStage.TenantTransaction);
        return await ExecuteOnOpenAsync(sql, tenantId, gateVersion, action, cancellationToken);
    }

    private static async Task<T> ExecuteOnOpenAsync<T>(SqlConnection sql, Guid tenantId, long gateVersion,
        Func<TenantTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);
        await TenantWriteGate.AcquireWriterAsync(sql, transaction, tenantId, gateVersion, cancellationToken);
        var result = await action(new TenantTransaction(sql, transaction), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
