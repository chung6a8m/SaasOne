using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

// Private-fixture observation only. No admin credentials enter an application host or report.
internal sealed class M7SqlProfileSession : IDisposable
{
    private readonly string _connection;
    private readonly M7ServerSnapshot _before;
    private readonly SqlConnection _observation;
    private readonly M7BlockingProbe _probe;
    private readonly M7SqlProfile _profile = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<(string, string, string, string, string), M7BlockingRow> _blocking = [];
    private readonly Task _poll;
    private long _samples;
    private double _samplingElapsedMs;
    private bool _samplingFailed, _disposed;

    private M7SqlProfileSession(string connection, M7ServerSnapshot before, SqlConnection observation, M7BlockingProbe probe)
    {
        _connection = connection; _before = before; _observation = observation; _probe = probe;
        _poll = PollAsync();
    }
    internal static async Task<M7SqlProfileSession> StartAsync(string connection, CancellationToken ct)
    {
        var observation = await SqlOperations.OpenAsync(connection, ct);
        try
        {
            var databases = new Dictionary<string, string>(StringComparer.Ordinal) { ["saas_platform"] = "platform" };
            // Fixed private registry, before profiling and before the measured load. Never HTTP input.
            await using (var command = SqlOperations.Command(observation,
                "SELECT [DatabaseName], [Slug] FROM [saas_platform].[platform].[Tenants] WHERE [Slug] IN (N'alpha',N'beta',N'gamma',N'delta')"))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) databases.Add(reader.GetString(0), reader.GetString(1));
            if (databases.Count != 5) throw new InvalidOperationException("Four-tenant observation scope is missing.");
            var probe = new M7BlockingProbe(observation, databases);
            return new(connection, await CaptureAsync(connection, ct), observation, probe);
        }
        catch { await observation.DisposeAsync(); throw; }
    }
    private async Task PollAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                var timer = Stopwatch.StartNew();
                // Stop cancels the delay, not an in-flight query. Query timeout is bounded to 5s.
                var rows = await _probe.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                _samplingElapsedMs += timer.Elapsed.TotalMilliseconds;
                _samples++;
                foreach (var row in rows)
                {
                    var key = (row.Tenant, row.Resource, row.ResourceType, row.WaitType, row.Mode);
                    var previous = _blocking.GetValueOrDefault(key);
                    _blocking[key] = previous is null ? row : row with
                    {
                        TaskObservations = checked(previous.TaskObservations + row.TaskObservations),
                        MaxObservedWaitMs = Math.Max(previous.MaxObservedWaitMs, row.MaxObservedWaitMs)
                    };
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { _samplingFailed = true; } // No raw SQL/connection exception is exported; Complete fails the gate.
    }
    internal async Task CompleteAsync(int requests, CancellationToken ct)
    {
        _stop.Cancel();
        await _poll.ConfigureAwait(false);
        _profile.Dispose();
        var client = _profile.Snapshot();
        await _observation.DisposeAsync();
        var after = await CaptureAsync(_connection, ct);
        var oldWaits = _before.Waits.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var oldCounters = _before.Counters.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var waits = after.Waits.Select(x =>
        {
            var old = oldWaits.GetValueOrDefault(x.Name) ?? new(x.Name, 0, 0, 0);
            return new M7SqlWait(x.Name, x.Tasks - old.Tasks, x.WaitMs - old.WaitMs, x.SignalMs - old.SignalMs);
        }).Where(x => x.Tasks != 0 || x.WaitMs != 0).OrderByDescending(x => x.WaitMs).ToArray();
        var counters = after.Counters.Select(x => new M7SqlCounter(x.Name, x.Value - (oldCounters.GetValueOrDefault(x.Name)?.Value ?? 0))).ToArray();
        M7Evidence.Write("sql-profile.json", new
        {
            schemaVersion = 2, sourceSha = M7Evidence.SourceSha, acceptance = "NOT_ASSESSED",
            scope = "Entire load window including ramp and drain, after token issuance and before integrity queries. Dedicated disposable SQL instance.",
            interpretation = "Client totalMs sums concurrent operations, not wall-clock/CPU. Command counts/timings INCLUDE observation queries; application command count subtracts successful observation-query count only. Opens EXCLUDE the observation connection opened before the observer. Reader consumption is not in command timings. Server waits/counters include background and administrative snapshot traffic. Sampling adds measured overhead.",
            beforeUtc = _before.Utc, afterUtc = after.Utc, requests, client,
            connectionOpensPerRequest = requests == 0 ? 0 : client.Open.Count / (double)requests,
            applicationCommandCount = client.Command.Count - _samples,
            commandsPerRequest = requests == 0 ? 0 : (client.Command.Count - _samples) / (double)requests,
            blocking = new
            {
                status = _samplingFailed || _samples == 0 ? "FAIL" : "PASS",
                successfulSamples = _samples, intervalMilliseconds = 1000, queryTimeoutSeconds = 5,
                queryElapsedMs = _samplingElapsedMs,
                interpretation = "Live sampled waiting-task observations, not unique requests or total blocked duration. Repeated wait durations are NOT added; only maximum wait is retained. Labels are fixed buckets, not SQL text, session/connection IDs, resource hashes, user/login names or database identifiers.",
                rows = _blocking.Values.OrderByDescending(x => x.TaskObservations).ToArray()
            },
            waits, counters
        });
        Assert.True(!_samplingFailed && _samples > 0, "SQL blocking observation did not complete successfully.");
        Assert.True(client.Open.Count > 0 && client.Command.Count > _samples, "SQL profile must observe actual application operations.");
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
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        // Poll continuations never capture the test synchronization context; query timeout bounds cleanup.
        _poll.GetAwaiter().GetResult();
        _profile.Dispose(); _observation.Dispose(); _stop.Dispose();
    }
}
internal sealed record M7SqlWait(string Name, long Tasks, long WaitMs, long SignalMs);
internal sealed record M7SqlCounter(string Name, long Value);
internal sealed record M7ServerSnapshot(DateTimeOffset Utc, M7SqlWait[] Waits, M7SqlCounter[] Counters);
