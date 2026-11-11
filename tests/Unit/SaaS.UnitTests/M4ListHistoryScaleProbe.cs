using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Orders;
using SaaS.Orders.Data;
using SaaS.Tenancy.Operations;
using Xunit;

namespace SaaS.UnitTests;

// Disposable M4 SQL fixture diagnostic: never installed in a runtime host.
// SQL text/parameters, query plans and customer GUIDs are kept in process,
// and ONLY aggregated numeric results are written to the test artifact.
internal static class M4ListHistoryScaleProbe
{
    private sealed record QueryWork(long LogicalReads, long CpuMs, int ResultRows);
    private sealed record PlanShape(int Operators, int SeekOperators, int ScanOperators);
    private sealed record DepthReport(int RevisionsPerOrder, int Orders, QueryWork Projection,
        QueryWork LegacyThreeQueries, PlanShape ProjectionPlan, PlanShape GroupedHistoryPlan);

    internal static async Task ValidateAsync(M4SqlHarness harness, Guid[] selectedIds,
        Func<Task> demandCorrectFullPage, CancellationToken ct)
    {
        Assert.Equal(51, selectedIds.Length);
        var reports = new List<DepthReport>();
        var priorDepth = 1;
        foreach (var depth in new[] { 50, 1000 })
        {
            await ExpandHistoryAsync(harness, priorDepth, depth, ct);
            // This is the unmodified public Orders List path: same transaction,
            // ordered Shared locks, fresh ACL and complete snapshot/history guard.
            await demandCorrectFullPage();
            var report = await ReadPlanAndIoAsync(harness.Target.Connection, selectedIds, depth, ct);
            Assert.Equal(51, report.Projection.ResultRows);
            Assert.True(report.Projection.LogicalReads > 0, "Missing actual SQL Server projection logical-read evidence.");
            Assert.True(report.LegacyThreeQueries.LogicalReads > 0, "Missing actual SQL Server grouped-query logical-read evidence.");
            Assert.True(report.ProjectionPlan.Operators > 0 && report.GroupedHistoryPlan.Operators > 0,
                "Missing actual SQL Server XML query-plan operators.");
            reports.Add(report);
            priorDepth = depth;
        }

        // The M4 pinned SQL workflow retains test-results/m4-poc as an artifact.
        // Only fixed schema labels, counts and numeric SQL statistics are saved.
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SaasOne.slnx")))
            root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found for sanitized SQL evidence.");
        var destination = Path.Combine(root.FullName, "artifacts", "test-results", "m4-poc");
        Directory.CreateDirectory(destination);
        var evidence = new
        {
            schemaVersion = 1,
            status = "MEASURED_NOT_SLO_ACCEPTED",
            scope = "One isolated SQL Server 2022 fixture; full List 51 selected rows; 50 then 1000 history revisions per Order; no API auth bypass.",
            interpretation = "Actual SET STATISTICS IO logical reads and SQL CPU milliseconds from separate, warmed, read-only SQL statements on the same test database. The comparator executes the previous three SELECTs, while the candidate executes the new projection. Client round trips, concurrency and hardware differ from production. Numeric logical reads are diagnostic, not a production p95/p99 SLO. SHOWPLAN_XML is parsed in memory and only operator counts are exported.",
            depths = reports
        };
        File.WriteAllText(Path.Combine(destination, "issue15-list-history-plan.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ExpandHistoryAsync(M4SqlHarness harness, int previous, int depth, CancellationToken ct)
    {
        await using (var sql = await SqlOperations.OpenAsync(harness.Target.Connection, ct))
        {
            // Only the fixture's generated LIST-BATCH rows. Set-based synthesis
            // avoids 50k application round trips and adds no production DDL.
            await SqlOperations.ExecuteAsync(sql, """
                UPDATE [orders].[Orders] SET [Revision] = @depth
                WHERE [Number] LIKE 'LIST-BATCH-%';
                ;WITH N AS (
                    SELECT TOP (1000)
                        ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id) + 1 AS [Revision]
                    FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
                )
                INSERT [orders].[Revisions]
                    ([OrderId], [Revision], [ActorId], [Action], [Status],
                     [ApprovalRequired], [SnapshotJson], [OccurredAt])
                SELECT o.[Id], n.[Revision], o.[CreatedBy], 2, 1, 0,
                    JSON_MODIFY(first.[SnapshotJson], '$.Revision', n.[Revision]), o.[UpdatedAt]
                FROM [orders].[Orders] AS o
                JOIN [orders].[Revisions] AS first ON first.[OrderId] = o.[Id] AND first.[Revision] = 1
                CROSS JOIN N AS n
                WHERE o.[Number] LIKE 'LIST-BATCH-%'
                    AND n.[Revision] > @previous AND n.[Revision] < @depth;
                """, ct, ("@previous", previous), ("@depth", depth));
        }

        // The latest revision's snapshot must match the database-generated
        // current rowversion exactly, not a forged/stale history snapshot.
        await using var db = OrdersDbContext.Open(harness.Target.Connection);
        var rows = await db.Orders.AsNoTracking()
            .Where(x => x.Number.StartsWith("LIST-BATCH-")).ToArrayAsync(ct);
        Assert.Equal(52, rows.Length);
        db.Revisions.AddRange(rows.Select(row => new OrderRevision
        {
            OrderId = row.Id, Revision = depth, ActorId = row.CreatedBy,
            Action = OrderAction.Edit, Status = row.Status,
            ApprovalRequired = false, SnapshotJson = JsonSerializer.Serialize(row.View()),
            OccurredAt = row.UpdatedAt
        }));
        await db.SaveChangesAsync(ct);
        Assert.Equal(52L * depth, await db.Revisions.LongCountAsync(ct));
    }

    private static async Task<DepthReport> ReadPlanAndIoAsync(string connection, Guid[] ids,
        int depth, CancellationToken ct)
    {
        await using var db = OrdersDbContext.Open(connection);
        var rows = db.Orders.AsNoTracking().Where(x => ids.Contains(x.Id));
        var latest =
            from revision in db.Revisions.AsNoTracking()
            join order in db.Orders.AsNoTracking() on revision.OrderId equals order.Id
            where ids.Contains(order.Id) && revision.Revision == order.Revision
            select revision;
        var history = db.Revisions.AsNoTracking()
            .Where(x => ids.Contains(x.OrderId))
            .GroupBy(x => x.OrderId)
            .Select(group => new
            {
                OrderId = group.Key,
                Count = group.LongCount(),
                HistoricalMarker = group.Max(x => x.ApprovalRequired ? 1 : 0)
            });
        var projection = db.Orders.AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .Select(row => new
            {
                Row = row,
                Latest = db.Revisions.AsNoTracking()
                    .Where(revision => revision.OrderId == row.Id && revision.Revision == row.Revision)
                    .SingleOrDefault(),
                HistoryCount = db.Revisions.LongCount(revision => revision.OrderId == row.Id),
                HistoricalApproval = db.Revisions.Any(revision =>
                    revision.OrderId == row.Id && revision.ApprovalRequired)
            });

        // ToQueryString creates real EF SQL with in-process parameter
        // declarations for exactly the same 51 fixture IDs. Never emit it.
        var projectedSql = projection.ToQueryString();
        var oldSql = new[] { rows.ToQueryString(), latest.ToQueryString(), history.ToQueryString() };
        await using var sql = await SqlOperations.OpenAsync(connection, ct);

        // One warm-up per statement, then actual SQL Server IO/CPU observations.
        await SqlOperations.ExecuteAsync(sql, "SET STATISTICS IO ON; SET STATISTICS TIME ON;", ct);
        QueryWork candidate;
        QueryWork legacy;
        try
        {
            candidate = await MeasureAsync(sql, [projectedSql], ct);
            legacy = await MeasureAsync(sql, oldSql, ct);
        }
        finally
        {
            await SqlOperations.ExecuteAsync(sql, "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;", CancellationToken.None);
        }

        // Obtain real SQL Server optimizer plans, but do not export XML,
        // SQL literals or database/fixture resource names.
        var candidatePlan = await PlanAsync(sql, projectedSql, ct);
        var groupedPlan = await PlanAsync(sql, oldSql[2], ct);
        return new(depth, ids.Length, candidate, legacy, candidatePlan, groupedPlan);
    }

    private static async Task<QueryWork> MeasureAsync(SqlConnection sql,
        IReadOnlyList<string> statements, CancellationToken ct)
    {
        foreach (var statement in statements) await ConsumeAsync(sql, statement, ct);
        long reads = 0, cpu = 0;
        void OnMessage(object sender, SqlInfoMessageEventArgs eventArgs)
        {
            foreach (SqlError error in eventArgs.Errors)
            {
                foreach (Match match in Regex.Matches(error.Message, @"logical reads\s+(\d+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    reads += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                foreach (Match match in Regex.Matches(error.Message, @"CPU time\s*=\s*(\d+)\s*ms",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    cpu += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        sql.InfoMessage += OnMessage;
        var rowCount = 0;
        try
        {
            foreach (var statement in statements)
                rowCount += await ConsumeAsync(sql, statement, ct);
        }
        finally { sql.InfoMessage -= OnMessage; }
        return new(reads, cpu, rowCount);
    }

    private static async Task<int> ConsumeAsync(SqlConnection sql, string statement, CancellationToken ct)
    {
        await using var cmd = SqlOperations.Command(sql, statement);
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;
        do
        {
            while (await reader.ReadAsync(ct)) count++;
        } while (await reader.NextResultAsync(ct));
        return count;
    }

    private static async Task<PlanShape> PlanAsync(SqlConnection sql, string query, CancellationToken ct)
    {
        await SqlOperations.ExecuteAsync(sql, "SET SHOWPLAN_XML ON;", ct);
        try
        {
            await using var command = SqlOperations.Command(sql, query);
            command.CommandTimeout = 120;
            await using var reader = await command.ExecuteReaderAsync(ct);
            var plans = new List<string>();
            do
            {
                while (await reader.ReadAsync(ct))
                    if (!reader.IsDBNull(0)) plans.Add(reader.GetString(0));
            } while (await reader.NextResultAsync(ct));
            var xml = plans.FirstOrDefault(x => x.Contains("ShowPlanXML", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("SQL Server SHOWPLAN_XML did not return a query plan.");
            var operations = XDocument.Parse(xml).Descendants()
                .Where(node => node.Name.LocalName == "RelOp")
                .Select(node => (string?)node.Attribute("PhysicalOp") ?? "")
                .ToArray();
            return new(operations.Length,
                operations.Count(x => x.Contains("Seek", StringComparison.Ordinal)),
                operations.Count(x => x.Contains("Scan", StringComparison.Ordinal)));
        }
        finally
        {
            await SqlOperations.ExecuteAsync(sql, "SET SHOWPLAN_XML OFF;", CancellationToken.None);
        }
    }
}
