using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl;
using SaaS.OrderApprovals.Data;
using SaaS.Tenancy.Data;
namespace SaaS.OrderApprovals;

/// <summary>Maintenance-only, entitlement-independent history validation. Caller holds the closed durable gate.</summary>
public static class ApprovalHistoryVerifier
{
    public static async Task DemandColumnsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT TOP(0) [OrderId],[SubmittedRevision],[SubmittedVersion],[CreatedBy],[SubmittedBy],[SubmittedAt] FROM [approvals].[Requests]; SELECT TOP(0) [OrderId],[SubmittedRevision],[ResultRevision],[DecidedBy],[Approved],[Reason],[DecidedAt] FROM [approvals].[Decisions];";
        await command.ExecuteNonQueryAsync(ct);
    }
    public static async Task VerifyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var db = ApprovalsDbContext.Open(connection);
        await TenancyConnections.VerifySchemaAsync(db, ct);
        await DemandColumnsAsync(connection, null, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN
            EXISTS (
                SELECT 1 FROM [approvals].[Requests] r
                LEFT JOIN [orders].[Orders] o ON o.[Id] = r.[OrderId]
                LEFT JOIN [orders].[Revisions] v ON v.[OrderId] = r.[OrderId] AND v.[Revision] = r.[SubmittedRevision]
                LEFT JOIN [approvals].[Decisions] d ON d.[OrderId] = r.[OrderId] AND d.[SubmittedRevision] = r.[SubmittedRevision]
                WHERE o.[Id] IS NULL OR v.[OrderId] IS NULL OR r.[CreatedBy] <> o.[CreatedBy]
                    OR v.[Action] <> 3 OR v.[Status] <> 2 OR v.[ApprovalRequired] <> 1 OR v.[ActorId] <> r.[SubmittedBy]
                    OR COALESCE(JSON_VALUE(v.[SnapshotJson], '$.Version'), '') COLLATE Latin1_General_100_BIN2 <> r.[SubmittedVersion]
                    OR TRY_CONVERT(uniqueidentifier, JSON_VALUE(v.[SnapshotJson], '$.CreatedBy')) IS NULL
                    OR TRY_CONVERT(uniqueidentifier, JSON_VALUE(v.[SnapshotJson], '$.CreatedBy')) <> r.[CreatedBy]
                    OR (d.[OrderId] IS NULL AND (o.[Revision] <> r.[SubmittedRevision] OR o.[Status] <> 2))
            ) OR EXISTS (
                SELECT 1 FROM [approvals].[Decisions] d
                LEFT JOIN [approvals].[Requests] r ON r.[OrderId] = d.[OrderId] AND r.[SubmittedRevision] = d.[SubmittedRevision]
                LEFT JOIN [orders].[Revisions] v ON v.[OrderId] = d.[OrderId] AND v.[Revision] = d.[ResultRevision]
                WHERE r.[OrderId] IS NULL OR v.[OrderId] IS NULL OR d.[DecidedBy] = r.[CreatedBy]
                    OR d.[ResultRevision] - d.[SubmittedRevision] <> 1 OR v.[ActorId] <> d.[DecidedBy] OR v.[ApprovalRequired] <> 1
                    OR v.[Action] <> CASE WHEN d.[Approved] = 1 THEN 4 ELSE 5 END
                    OR v.[Status] <> CASE WHEN d.[Approved] = 1 THEN 4 ELSE 3 END
            ) OR EXISTS (
                SELECT 1 FROM [orders].[Revisions] v
                WHERE (v.[Action] = 3 AND v.[Status] = 2 AND NOT EXISTS (
                    SELECT 1 FROM [approvals].[Requests] r WHERE r.[OrderId] = v.[OrderId] AND r.[SubmittedRevision] = v.[Revision]))
                OR (v.[Action] IN (4,5) AND NOT EXISTS (
                    SELECT 1 FROM [approvals].[Decisions] d WHERE d.[OrderId] = v.[OrderId] AND d.[ResultRevision] = v.[Revision]))
            ) THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0)
            throw new AccessDeniedException(503, "approval_history_invalid");
    }
}
