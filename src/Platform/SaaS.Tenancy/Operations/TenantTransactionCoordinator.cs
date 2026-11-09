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
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);
        await TenantWriteGate.AcquireWriterAsync(sql, transaction, tenantId, gateVersion, cancellationToken);
        var result = await action(new TenantTransaction(sql, transaction), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
