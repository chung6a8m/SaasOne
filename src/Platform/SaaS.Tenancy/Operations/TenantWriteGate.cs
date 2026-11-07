using Microsoft.Data.SqlClient;
namespace SaaS.Tenancy.Operations;

public sealed record WriteGateState(Guid TenantId, bool IsOpen, long Version, Guid OperationId, long Fence);
public static class TenantWriteGate
{
    public static Task<WriteGateState> ReadAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken ct) => throw new NotImplementedException();
    public static Task AcquireWriterAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId, long expectedVersion, CancellationToken ct) => throw new NotImplementedException();
    public static Task<WriteGateState> CloseAsync(string connection, Guid tenantId, long expectedVersion, Guid operationId, long fence, int timeoutMilliseconds, CancellationToken ct) => throw new NotImplementedException();
    public static Task<WriteGateState> PrepareOpenAsync(string connection, Guid tenantId, long expectedVersion, Guid operationId, long fence, CancellationToken ct) => throw new NotImplementedException();
}
