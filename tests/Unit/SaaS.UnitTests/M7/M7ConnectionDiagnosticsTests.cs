using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M1 SQL")]
[Trait("Category", "SqlM1")]
public sealed class M7ConnectionDiagnosticsTests(M1SqlFixture fixture, ITestOutputHelper output) : IClassFixture<M1SqlFixture>
{
    [Fact]
    public async Task Unpooled_open_diagnostic_preserves_fresh_logins_and_reports_both_scheduling_paths()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = fixture.Environment.Runtime(fixture.Environment.Tenants[0]);
        Assert.False(new SqlConnectionStringBuilder(connection).Pooling);
        await BatchAsync(connection, false, 4, 1, ct);
        await BatchAsync(connection, true, 4, 1, ct);
        var rows = new List<object>();
        foreach (var synchronous in new[] { false, true, true, false })
        {
            using var profile = new M7SqlProfile();
            var row = await BatchAsync(connection, synchronous, 32, 8, ct);
            var observed = profile.Snapshot();
            Assert.Equal(256, observed.Open.Count);
            Assert.Equal(0, observed.Open.Errors);
            Assert.Equal(256, observed.Command.Count);
            Assert.Equal(0, observed.Command.Errors);
            Assert.Equal(0, observed.Pending);
            rows.Add(row);
        }
        output.WriteLine("M7_UNPOOLED_PROBE " + JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            acceptance = "NOT_ASSESSED",
            scope = "Isolated diagnostic, not HTTP workload. Fresh encrypted unpooled connections; fixed SELECT 1. ABBA sequence; dedicated synchronous workers are test-only.",
            logicalProcessors = System.Environment.ProcessorCount,
            driverVersion = typeof(SqlConnection).Assembly.GetName().Version?.ToString(),
            workers = 32, iterationsPerWorker = 8, rows
        }));
    }

    private static async Task<object> BatchAsync(string connection, bool synchronous, int workers, int count, CancellationToken ct)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var syncStart = new ManualResetEventSlim(false);
        var queued = 0;
        var latencies = new ConcurrentBag<double>();
        void Arrive() { if (Interlocked.Increment(ref queued) == workers) ready.SetResult(); }
        async Task AsyncWorker()
        {
            Arrive();
            await starts.Task;
            for (var i = 0; i < count; i++)
            {
                await using var sql = new SqlConnection(connection);
                var timer = Stopwatch.StartNew();
                await sql.OpenAsync(ct);
                latencies.Add(timer.Elapsed.TotalMilliseconds);
                await using var command = sql.CreateCommand();
                command.CommandText = "SELECT 1";
                Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(ct)));
            }
        }
        void SyncWorker()
        {
            Arrive();
            syncStart.Wait(ct);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var sql = new SqlConnection(connection);
                var timer = Stopwatch.StartNew();
                sql.Open();
                latencies.Add(timer.Elapsed.TotalMilliseconds);
                using var command = sql.CreateCommand();
                command.CommandText = "SELECT 1";
                Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
            }
        }
        var tasks = Enumerable.Range(0, workers).Select(_ => synchronous
            ? Task.Factory.StartNew(SyncWorker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : AsyncWorker()).ToArray();
        await ready.Task;
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        starts.SetResult();
        syncStart.Set();
        await Task.WhenAll(tasks);
        watch.Stop();
        process.Refresh();
        var sorted = latencies.Order().ToArray();
        Assert.Equal(workers * count, sorted.Length);
        double Percentile(double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
        return new
        {
            mode = synchronous ? "DedicatedSynchronousWorkers" : "DriverOpenAsync",
            operations = sorted.Length, elapsedMs = watch.Elapsed.TotalMilliseconds,
            processCpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            openMeanMs = sorted.Average(), openP50Ms = Percentile(.50), openP95Ms = Percentile(.95),
            openP99Ms = Percentile(.99), openMaxMs = sorted[^1]
        };
    }
}
