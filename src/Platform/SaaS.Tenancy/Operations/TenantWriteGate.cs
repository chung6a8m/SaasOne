using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Operations;

public sealed record WriteGateState(Guid TenantId, bool IsOpen, long Version, Guid OperationId, long Fence);

/// <summary>A database-local, transaction-owned lock. Disposing a connection releases the lock, never the durable state.</summary>
public static class TenantWriteGate
{
    public const string LockResource = "saas-tenant-write-gate-v1";

    public static async Task<WriteGateState> ReadAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = SqlOperations.Command(connection, """
            SELECT g.TenantId, g.IsOpen, g.Version, g.OperationId, g.Fence, i.TenantId
            FROM [tenancy].[WriteGate] g CROSS JOIN [tenancy].[Identity] i WHERE g.Id = 1 AND i.Id = 1
            """);
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new TenantContractException("write_gate_missing");
        var state = new WriteGateState(reader.GetGuid(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetGuid(3), reader.GetInt64(4));
        if (state.TenantId == Guid.Empty || state.TenantId != reader.GetGuid(5) || state.Version < 1 || state.Fence < 1
            || state.OperationId == Guid.Empty || await reader.ReadAsync(ct)) throw new TenantContractException("write_gate_invalid");
        return state;
    }

    public static async Task AcquireWriterAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId, long expectedVersion, CancellationToken ct)
    {
        await SqlTransactionLocks.AcquireAsync(connection, transaction, LockResource, exclusive: false, 30000, ct);
        var state = await ReadAsync(connection, transaction, ct);
        if (tenantId == Guid.Empty || state.TenantId != tenantId || !state.IsOpen || expectedVersion < 1 || state.Version != expectedVersion)
            throw new TenantContractException("write_gate_changed");
    }

    public static Task<WriteGateState> CloseAsync(string connection, Guid tenantId, long expectedVersion, Guid operationId, long fence, int timeoutMilliseconds, CancellationToken ct)
        => ChangeAsync(connection, tenantId, expectedVersion, operationId, fence, false, timeoutMilliseconds, ct);

    /// <summary>Only prepares the customer side. The caller MUST keep the platform closed until its lifecycle CAS succeeds.</summary>
    public static Task<WriteGateState> PrepareOpenAsync(string connection, Guid tenantId, long expectedVersion, Guid operationId, long fence, CancellationToken ct)
        => ChangeAsync(connection, tenantId, expectedVersion, operationId, fence, true, 30000, ct);

    private static async Task<WriteGateState> ChangeAsync(string connection, Guid tenantId, long expectedVersion, Guid operationId, long fence, bool open, int timeout, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || operationId == Guid.Empty || expectedVersion < 1 || fence < 1) throw new TenantContractException("write_gate_changed");
        await using var sql = await SqlOperations.OpenAsync(connection, ct);
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(ct);
        await SqlTransactionLocks.AcquireAsync(sql, transaction, LockResource, exclusive: true, timeout, ct);
        var state = await ReadAsync(sql, transaction, ct);
        if (state.TenantId != tenantId || state.Version != expectedVersion || fence < state.Fence
            || open && (state.OperationId != operationId || state.Fence != fence)
            || !open && fence == state.Fence && state.OperationId != operationId)
            throw new TenantContractException("write_gate_changed");
        if (state.IsOpen == open && state.OperationId == operationId && state.Fence == fence)
        {
            await transaction.CommitAsync(ct);
            return state;
        }
        var next = state with { IsOpen = open, Version = checked(state.Version + 1), OperationId = operationId, Fence = fence };
        await using var command = SqlOperations.Command(sql, """
            UPDATE [tenancy].[WriteGate] SET IsOpen = @open, Version = @next, OperationId = @operation, Fence = @fence
            WHERE Id = 1 AND TenantId = @tenant AND Version = @expected
            """, ("@open", open), ("@next", next.Version), ("@operation", operationId), ("@fence", fence), ("@tenant", tenantId), ("@expected", expectedVersion));
        command.Transaction = transaction;
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new TenantContractException("write_gate_changed");
        await transaction.CommitAsync(ct);
        return next;
    }
}

public static class SqlTransactionLocks
{
    /// <summary>
    /// Acquires distinct ordered shared application locks in one client round trip.
    /// Each resource retains its own SQL transaction-owned lock and its own timeout.
    /// The caller supplies the deterministic order and must roll back on any failure.
    /// </summary>
    public static async Task AcquireOrderedSharedAsync(SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> orderedResources, int timeoutMilliseconds, CancellationToken ct)
    {
        if (transaction.Connection != connection || orderedResources is null || orderedResources.Count > 51
            || timeoutMilliseconds is < 0 or > 30000)
            throw new TenantContractException("transaction_lock_invalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in orderedResources)
        {
            if (string.IsNullOrEmpty(resource) || resource.Length > 255 || resource.Contains('|')
                || !seen.Add(resource))
                throw new TenantContractException("transaction_lock_invalid");
        }
        if (orderedResources.Count == 0) return;
        await using var command = SqlOperations.Command(connection, """
            SET NOCOUNT ON;
            DECLARE @remaining nvarchar(max) = @resources;
            DECLARE @separator int;
            DECLARE @resource nvarchar(255);
            DECLARE @result int = 0;
            WHILE DATALENGTH(@remaining) > 0
            BEGIN
                SET @separator = CHARINDEX(N'|', @remaining);
                IF @separator = 0
                BEGIN
                    SET @resource = @remaining;
                    SET @remaining = N'';
                END
                ELSE
                BEGIN
                    SET @resource = LEFT(@remaining, @separator - 1);
                    SET @remaining = SUBSTRING(@remaining, @separator + 1, 2147483647);
                END
                EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Shared',
                    @LockOwner = 'Transaction', @LockTimeout = @timeout, @DbPrincipal = 'public';
                IF @result < 0 BREAK;
            END
            SELECT @result;
            """, ("@resources", string.Join("|", orderedResources)), ("@timeout", timeoutMilliseconds));
        command.Transaction = transaction;
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not int code || code < 0)
            throw new TenantContractException(result is int value && value == -1
                ? "write_gate_timeout" : "transaction_lock_unavailable");
    }

    public static async Task AcquireAsync(SqlConnection connection, SqlTransaction transaction, string resource, bool exclusive, int timeoutMilliseconds, CancellationToken ct)
    {
        if (transaction.Connection != connection || resource.Length is < 1 or > 255 || timeoutMilliseconds is < 0 or > 30000)
            throw new TenantContractException("transaction_lock_invalid");
        await using var command = SqlOperations.Command(connection, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = @mode,
                @LockOwner = 'Transaction', @LockTimeout = @timeout, @DbPrincipal = 'public';
            SELECT @result;
            """, ("@resource", resource), ("@mode", exclusive ? "Exclusive" : "Shared"), ("@timeout", timeoutMilliseconds));
        command.Transaction = transaction;
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not int code || code < 0)
            throw new TenantContractException(result is int value && value == -1 ? "write_gate_timeout" : "transaction_lock_unavailable");
    }
}
