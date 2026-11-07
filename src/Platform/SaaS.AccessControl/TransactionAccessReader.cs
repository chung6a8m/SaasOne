using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.AccessControl;

public sealed record TransactionAccess(CurrentAccess Current, AccessSnapshot Snapshot);

/// <summary>Trusted in-process composition only; never an HTTP ACL-disclosure endpoint.
/// Keeps a consistent ACL read under the caller's write-gate transaction and rechecks current external authority.</summary>
public static class TransactionAccessReader
{
    public static async Task<TransactionAccess> ReadAsync(Guid routedTenantId, TenantTransaction tx,
        Func<CancellationToken, Task<CurrentAccess>> currentAuthority, CancellationToken ct)
    {
        if (!ReferenceEquals(tx.Connection, tx.Transaction.Connection)) throw new AccessDeniedException(503, "transaction_mismatch");
        var sql = (SqlConnection)tx.Connection; var transaction = (SqlTransaction)tx.Transaction;
        await SqlTransactionLocks.AcquireAsync(sql, transaction, AclRepository.LockResource, false, 30000, ct);
        var actor = await currentAuthority(ct);
        var gate = await TenantWriteGate.ReadAsync(sql, transaction, ct);
        if (routedTenantId == Guid.Empty || actor.TenantId != routedTenantId || gate.TenantId != routedTenantId)
            throw new AccessDeniedException(403, "tenant_mismatch");
        if (!gate.IsOpen) throw new AccessDeniedException(503, "tenant_operation_in_progress");
        await using var db = AccessDbContext.Open(sql);
        await db.Database.UseTransactionAsync(transaction, ct);
        return new(actor, await AclRepository.ReadAsync(db, ct));
    }
}
