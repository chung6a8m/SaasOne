using System.Data;
using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Operations;

public static class SqlOperations
{
    public static async Task<SqlConnection> OpenAsync(string connection, CancellationToken cancellationToken)
    {
        var sql = new SqlConnection(connection);
        try { await sql.OpenAsync(cancellationToken); return sql; }
        catch { await sql.DisposeAsync(); throw; }
    }
    public static SqlCommand Command(SqlConnection connection, string text, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 60;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public static async Task<object?> ScalarAsync(SqlConnection connection, string text, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, text, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
    public static async Task ExecuteAsync(SqlConnection connection, string text, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, text, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public static async Task RequireRuntimeAsync(string connection, CancellationToken cancellationToken)
    {
        await using var sql = await OpenAsync(connection, cancellationToken);
        await RequireRuntimeAsync(sql, cancellationToken);
    }
    // Borrow an already-open, server-selected connection; ownership stays with the caller.
    public static async Task RequireRuntimeAsync(SqlConnection sql, CancellationToken cancellationToken)
    {
        var safe = await ScalarAsync(sql, """
            SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 0
                AND IS_MEMBER('db_owner') = 0 AND IS_MEMBER('db_ddladmin') = 0
                AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 0
                AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 0
                AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 0
                AND HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') = 0
                AND HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN') = 0
                THEN 1 ELSE 0 END
            """, cancellationToken);
        if (safe is not int result || result != 1) throw new TenantContractException("runtime_principal_overprivileged");
    }
}
