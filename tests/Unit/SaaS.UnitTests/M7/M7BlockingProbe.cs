using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SaaS.Tenancy.Operations;
namespace SaaS.UnitTests;

// Test-only probe; SQL text and raw lock identities never leave this reader.
internal sealed class M7BlockingProbe
{
    private readonly SqlConnection _connection;
    private readonly string _query;
    internal M7BlockingProbe(SqlConnection connection, IReadOnlyDictionary<string, string> databases)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(databases);
        if (databases.Count is < 1 or > 5 || databases.Values.Distinct(StringComparer.Ordinal).Count() != databases.Count)
            throw new ArgumentException("Invalid observation scope.", nameof(databases));
        foreach (var (database, label) in databases)
            if (!Regex.IsMatch(database, "^saas_(platform|t_[0-9a-f]{32})$", RegexOptions.CultureInvariant)
                || label is not ("platform" or "alpha" or "beta" or "gamma" or "delta" or "test"))
                throw new ArgumentException("Invalid observation scope.", nameof(databases));
        _connection = connection;
        var branches = databases.OrderBy(x => x.Key, StringComparer.Ordinal).Select(pair => $$"""
            SELECT N'{{pair.Value}}' AS Tenant,
                CASE WHEN w.resource_type = 'APPLICATION' THEN
                    CASE WHEN w.resource_description LIKE '%orders-resource:%' THEN 'app.order-resource'
                         WHEN w.resource_description LIKE '%orders-command:%' THEN 'app.order-command'
                         WHEN w.resource_description LIKE '%saas-acl-admin-v1%' THEN 'app.acl'
                         WHEN w.resource_description LIKE '%saas-tenant-write-gate-v1%' THEN 'app.write-gate'
                         ELSE 'app.other' END
                    WHEN s.name = 'orders' AND o.name = 'Orders' THEN 'orders.Orders'
                    WHEN s.name = 'orders' AND o.name = 'Revisions' THEN 'orders.Revisions'
                    WHEN s.name = 'orders' AND o.name = 'Receipts' THEN 'orders.Receipts'
                    WHEN s.name = 'tenancy' AND o.name = 'WriteGate' THEN 'tenancy.WriteGate'
                    WHEN s.name = 'tenancy' AND o.name = 'Identity' THEN 'tenancy.Identity'
                    WHEN s.name = 'tenancy' THEN 'tenancy.metadata'
                    WHEN s.name = 'approvals' THEN 'approvals'
                    WHEN s.name = 'access' THEN 'access'
                    WHEN s.name = 'audit' THEN 'audit'
                    WHEN s.name = 'identity' THEN 'platform.identity'
                    WHEN s.name = 'platform' THEN 'platform.control'
                    WHEN s.name = 'dbo' THEN 'native.dbo'
                    ELSE 'Other' END AS Resource,
                CASE WHEN w.resource_type IN ('KEY','PAGE','RID','HOBT','OBJECT','APPLICATION','DATABASE')
                    THEN w.resource_type ELSE 'Other' END AS ResourceType,
                CASE WHEN w.wait_type IN ('LCK_M_S','LCK_M_X','LCK_M_U','LCK_M_IS','LCK_M_IX','LCK_M_SIX',
                    'LCK_M_SCH_S','LCK_M_SCH_M','LCK_M_RS_S','LCK_M_RS_U','LCK_M_RIn_N','LCK_M_RIn_S',
                    'LCK_M_RIn_U','LCK_M_RIn_X','LCK_M_RX_S','LCK_M_RX_U','LCK_M_RX_X')
                    THEN w.wait_type ELSE 'Other' END AS WaitType,
                CASE WHEN w.request_mode IN ('S','X','U','IS','IX','SIX','Sch-S','Sch-M','RangeS_S','RangeS_U',
                    'RangeI_N','RangeI_S','RangeI_U','RangeI_X','RangeX_S','RangeX_U','RangeX_X')
                    THEN w.request_mode ELSE 'Other' END AS Mode,
                w.wait_duration_ms AS WaitMs
            FROM waiting w
            LEFT JOIN [{{pair.Key}}].sys.partitions p ON w.resource_type IN ('KEY','PAGE','RID','HOBT')
                AND p.hobt_id = w.resource_associated_entity_id
            LEFT JOIN [{{pair.Key}}].sys.objects o ON o.object_id =
                CASE WHEN w.resource_type = 'OBJECT' THEN w.resource_associated_entity_id ELSE p.object_id END
            LEFT JOIN [{{pair.Key}}].sys.schemas s ON s.schema_id = o.schema_id
            WHERE w.resource_database_id = DB_ID(N'{{pair.Key}}')
            """);
        _query = """
            WITH waiting AS (
                SELECT l.resource_database_id, l.resource_type, l.resource_associated_entity_id,
                    l.resource_description, l.request_mode, w.wait_type, w.wait_duration_ms
                FROM sys.dm_tran_locks l
                INNER JOIN sys.dm_os_waiting_tasks w ON l.lock_owner_address = w.resource_address
                WHERE w.blocking_session_id > 0 AND l.request_session_id <> @@SPID
                    AND l.request_status <> 'GRANT'
            ), mapped AS (
            """ + string.Join("\nUNION ALL\n", branches) + """
            )
            SELECT Tenant, Resource, ResourceType, WaitType, Mode, COUNT_BIG(*), MAX(WaitMs)
            FROM mapped GROUP BY Tenant, Resource, ResourceType, WaitType, Mode
            """;
    }
    internal async Task<IReadOnlyList<M7BlockingRow>> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_connection.State != ConnectionState.Open) throw new InvalidOperationException("Observation connection is not open.");
        await using var command = SqlOperations.Command(_connection, _query);
        command.CommandTimeout = 5;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<M7BlockingRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6)));
        return rows;
    }
}
internal sealed record M7BlockingRow(string Tenant, string Resource, string ResourceType, string WaitType,
    string Mode, long TaskObservations, long MaxObservedWaitMs);
