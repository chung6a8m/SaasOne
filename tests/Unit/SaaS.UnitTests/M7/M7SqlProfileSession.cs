using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

// Reads only aggregate server diagnostics from the private disposable fixture.
// Admin credentials never enter an application host, process argument or report.
internal sealed class M7SqlProfileSession(string connection, M7ServerSnapshot before) : IDisposable
{
    private readonly M7SqlProfile _profile = new();
    internal static async Task<M7SqlProfileSession> StartAsync(string connection, CancellationToken ct) =>
        new(connection, await CaptureAsync(connection, ct));
    internal async Task CompleteAsync(int requests, CancellationToken ct)
    {
        _profile.Dispose();
        var client = _profile.Snapshot();
        var after = await CaptureAsync(connection, ct);
        var oldWaits = before.Waits.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var oldCounters = before.Counters.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var waits = after.Waits.Select(x =>
        {
            var old = oldWaits.GetValueOrDefault(x.Name) ?? new(x.Name, 0, 0, 0);
            return new M7SqlWait(x.Name, x.Tasks - old.Tasks, x.WaitMs - old.WaitMs, x.SignalMs - old.SignalMs);
        }).Where(x => x.Tasks != 0 || x.WaitMs != 0).OrderByDescending(x => x.WaitMs).ToArray();
        var counters = after.Counters.Select(x => new M7SqlCounter(x.Name, x.Value - (oldCounters.GetValueOrDefault(x.Name)?.Value ?? 0))).ToArray();
        M7Evidence.Write("sql-profile.json", new
        {
            schemaVersion = 1, sourceSha = M7Evidence.SourceSha, acceptance = "NOT_ASSESSED",
            scope = "Entire load window including ramp and drain, after token issuance and before integrity queries. Dedicated disposable SQL instance.",
            interpretation = "Client totalMs is the sum across concurrent operations, not wall-clock time or a percentage. Command timings stop when Execute returns; reader consumption is not included. Server waits include SQL background activity. Counters are cumulative deltas, not rates.",
            beforeUtc = before.Utc, afterUtc = after.Utc, requests, client,
            connectionOpensPerRequest = requests == 0 ? 0 : client.Open.Count / (double)requests,
            commandsPerRequest = requests == 0 ? 0 : client.Command.Count / (double)requests,
            waits, counters
        });
        Assert.True(client.Open.Count > 0 && client.Command.Count > 0, "SQL profile must observe actual driver operations.");
    }
    private static async Task<M7ServerSnapshot> CaptureAsync(string connection, CancellationToken ct)
    {
        await using var sql = await SqlOperations.OpenAsync(connection, ct);
        var waits = new List<M7SqlWait>();
        await using (var command = SqlOperations.Command(sql, "SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms FROM sys.dm_os_wait_stats WHERE waiting_tasks_count > 0"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) waits.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        var counters = new List<M7SqlCounter>();
        await using (var command = SqlOperations.Command(sql, "SELECT RTRIM(counter_name), cntr_value FROM sys.dm_os_performance_counters WHERE counter_name IN ('Logins/sec','Logouts/sec','Batch Requests/sec','SQL Compilations/sec') AND instance_name = ''"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) counters.Add(new(reader.GetString(0), reader.GetInt64(1)));
        return new(DateTimeOffset.UtcNow, waits.ToArray(), counters.ToArray());
    }
    public void Dispose() => _profile.Dispose();
}
internal sealed record M7SqlWait(string Name, long Tasks, long WaitMs, long SignalMs);
internal sealed record M7SqlCounter(string Name, long Value);
internal sealed record M7ServerSnapshot(DateTimeOffset Utc, M7SqlWait[] Waits, M7SqlCounter[] Counters);
