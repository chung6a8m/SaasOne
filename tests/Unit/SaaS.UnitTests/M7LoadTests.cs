using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Tenancy.Operations;
using Xunit;
using static SaaS.UnitTests.M7TenantHarness;
namespace SaaS.UnitTests;

public sealed partial class M7FourTenantTests
{
    [Fact]
    public async Task One_hundred_native_HTTPS_sessions_record_source_bound_four_tenant_load_and_SQL_invariants()
    {
        var root = M7CompositionTests.Root;
        var configBytes = await File.ReadAllBytesAsync(Path.Combine(root, "tests/Load/m7-load.json"), Ct);
        var config = JsonSerializer.Deserialize<M7LoadConfiguration>(configBytes, M7LoadEvidence.Json) ?? throw new InvalidDataException("load_configuration_missing");
        config.Validate();
        var configHash = M7LoadEvidence.Hash(configBytes);
        var mode = System.Environment.GetEnvironmentVariable("SAAS_M7_MODE") ?? throw new InvalidDataException("load_mode_missing");
        if (mode is not ("Baseline" or "Acceptance")) throw new InvalidDataException("load_mode_invalid");
        var source = System.Environment.GetEnvironmentVariable("SAAS_M7_SOURCE_SHA") ?? throw new InvalidDataException("load_source_missing");
        var digest = System.Environment.GetEnvironmentVariable("SAAS_M7_SOURCE_DIGEST") ?? throw new InvalidDataException("load_source_digest_missing");
        var output = System.Environment.GetEnvironmentVariable("SAAS_M7_OUTPUT") ?? throw new InvalidDataException("load_output_missing");
        var setupWatch = Stopwatch.StartNew();
        await using var setup = await M7TenantHarness.CreateAsync(fixture); var http = setup.Http;
        var workers = new List<(string Slug, int Number, string Token)>();
        // 100 independent native OAuth families, 25 per tenant; eight distinct business
        // accounts (creator and non-creator reviewer per tenant), not 100 distinct people.
        for (var worker = 0; worker < config.VirtualUsersPerTenant; worker++)
            foreach (var slug in Slugs)
            {
                var actor = setup.Creators[slug];
                var grant = worker == 0 ? actor.Tokens : await actor.Browser.ApiGrantAsync(http, slug);
                workers.Add((slug, worker, grant.AccessToken));
            }
        await setup.DemandFourActiveAsync();
        await using var sql = await SqlOperations.OpenAsync(setup.Environment.PlatformAdmin, Ct);
        var sqlVersion = Convert.ToString(await SqlOperations.ScalarAsync(sql, "SELECT CONVERT(varchar(32), SERVERPROPERTY('ProductVersion'))", Ct))!;
        Assert.Equal("16.0.4305.5", sqlVersion);
        var started = DateTimeOffset.UtcNow;
        var os = RuntimeInformation.OSDescription; var processors = System.Environment.ProcessorCount;
        var target = mode == "Acceptance" ? M7LoadEvidence.ReadLockedTargets(root, digest, configHash, started, processors, sqlVersion, os) : null;
        var waitsBefore = await SqlWaitsAsync(setup.Environment.PlatformAdmin);
        setupWatch.Stop();
        var samples = new ConcurrentBag<M7Sample>();
        var failures = new ConcurrentBag<string>();
        var completed = new ConcurrentDictionary<string, int>(Slugs.Select(x => new KeyValuePair<string, int>(x, 0)), StringComparer.Ordinal);
        var active = 0; var peak = 0; var status = "FAIL";
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var wall = Stopwatch.StartNew();
        Dictionary<string, M7Distribution> groups = new(StringComparer.Ordinal);
        object[] sqlWaitDelta = [];
        try
        {
            await Task.WhenAll(workers.Select(async (worker, index) =>
            {
                var activeWorker = false;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.RampSeconds * (double)index / workers.Count), Ct);
                    var current = Interlocked.Increment(ref active); activeWorker = true;
                    int seen;
                    do { seen = Volatile.Read(ref peak); if (current <= seen) break; }
                    while (Interlocked.CompareExchange(ref peak, current, seen) != seen);
                    using var client = http.NewClient();
                    client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
                    var iteration = 0;
                    do
                    {
                        var number = $"LOAD-{worker.Number:D2}-{iteration:D6}";
                        var draft = Draft(number) with { CustomerName = "M7 " + worker.Slug, Amount = 100 + worker.Number };
                        var collection = "/" + worker.Slug + "/api/v1/orders";
                        var order = await CommandAsync("create", collection, number + "-create", draft, worker.Token, HttpStatusCode.Created);
                        var path = collection + "/" + order.Id;
                        await ReadAsync(order);
                        order = await CommandAsync("edit", path + "/edit", number + "-edit",
                            new { expectedRevision = order.Revision, expectedVersion = order.Version, draft = draft with { Amount = draft.Amount + 1 } }, worker.Token, HttpStatusCode.OK);
                        order = await CommandAsync("submit", path + "/submit", number + "-submit", Expected(order), worker.Token, HttpStatusCode.OK);
                        if (worker.Slug is "beta" or "gamma")
                        {
                            Assert.Equal(OrderStatus.PendingApproval, order.Status);
                            order = await CommandAsync("approve", path + "/approve", number + "-approve", Expected(order), setup.Reviewers[worker.Slug].Tokens.AccessToken, HttpStatusCode.OK);
                        }
                        Assert.Equal(OrderStatus.Confirmed, order.Status);
                        Assert.Equal(worker.Slug is "beta" or "gamma" ? 4 : 3, order.Revision);
                        await ReadAsync(order);
                        completed.AddOrUpdate(worker.Slug, 1, (_, value) => value + 1);
                        iteration++;
                    } while (wall.Elapsed.TotalSeconds < config.RampSeconds + config.SteadySeconds);

                    async Task<HttpResponseMessage> RequestAsync(string operation, string path, string? key, object? body, string token, HttpStatusCode expected)
                    {
                        var begin = wall.Elapsed.TotalSeconds; var requestWatch = Stopwatch.StartNew();
                        var responseStatus = 0; var success = false; string? exception = null;
                        try
                        {
                            var response = await SendAsync(client, token, path, key, body);
                            responseStatus = (int)response.StatusCode; success = response.StatusCode == expected;
                            if (!success) { response.Dispose(); throw new InvalidDataException("unexpected_http_status_" + responseStatus); }
                            return response;
                        }
                        catch (Exception error) { exception = error.GetType().Name; throw; }
                        finally
                        {
                            requestWatch.Stop();
                            samples.Add(new(worker.Slug, operation, begin, wall.Elapsed.TotalSeconds, requestWatch.Elapsed.TotalMilliseconds, responseStatus, success, exception));
                        }
                    }
                    async Task<OrderView> CommandAsync(string operation, string path, string key, object body, string token, HttpStatusCode expected)
                    {
                        using var response = await RequestAsync(operation, path, key, body, token, expected);
                        var result = await response.Content.ReadFromJsonAsync<OrderCommandResult>(Ct) ?? throw new InvalidDataException("command_result_missing");
                        Assert.False(result.Replayed); Assert.Equal(setup.Creators[worker.Slug].Id, result.Order.CreatedBy);
                        Assert.Equal(numberFor(result.Order), result.Order.Number);
                        return result.Order;
                        string numberFor(OrderView _) => $"LOAD-{worker.Number:D2}-{iteration:D6}";
                    }
                    async Task ReadAsync(OrderView expected)
                    {
                        using var response = await RequestAsync("read", "/" + worker.Slug + "/api/v1/orders/" + expected.Id, null, null, worker.Token, HttpStatusCode.OK);
                        var actual = await response.Content.ReadFromJsonAsync<OrderView>(Ct) ?? throw new InvalidDataException("order_read_missing");
                        Assert.Equal(expected.Id, actual.Id); Assert.Equal(expected.Number, actual.Number); Assert.Equal(expected.CreatedBy, actual.CreatedBy);
                        Assert.Equal(expected.Revision, actual.Revision); Assert.Equal(expected.Version, actual.Version); Assert.Equal(expected.Status, actual.Status);
                        Assert.Equal(expected.Amount, actual.Amount); Assert.Equal("M7 " + worker.Slug, actual.CustomerName);
                    }
                }
                catch (Exception error) { failures.Add(worker.Slug + ":" + error.GetType().Name); }
                finally { if (activeWorker) Interlocked.Decrement(ref active); }
            }));
            wall.Stop();
            var waitsAfter = await SqlWaitsAsync(setup.Environment.PlatformAdmin);
            sqlWaitDelta = waitsAfter.Select(x => new { waitType = x.Key, milliseconds = x.Value - waitsBefore.GetValueOrDefault(x.Key) })
                .Where(x => x.milliseconds > 0).OrderByDescending(x => x.milliseconds).Take(12).Cast<object>().ToArray();
            groups = M7LoadEvidence.Summarize(samples, config);
            M7LoadEvidence.DemandComplete(groups, workers.Count, peak, failures.Count);
            await setup.DemandFourActiveAsync();
            foreach (var slug in Slugs)
            {
                Assert.True(completed[slug] >= 25);
                Assert.Equal(completed[slug], groups[slug + "/create"].Count);
                Assert.Equal(completed[slug] * 2, groups[slug + "/read"].Count);
                Assert.Equal(completed[slug], groups[slug + "/edit"].Count);
                Assert.Equal(completed[slug], groups[slug + "/submit"].Count);
                await AssertHistoryAsync(setup, slug, completed[slug]);
                if (slug is "beta" or "gamma")
                {
                    Assert.Equal(completed[slug], groups[slug + "/approve"].Count);
                    await using var approvals = ApprovalsDbContext.Open(setup.Environment.CustomerAdmin(http.Tenant(slug)));
                    Assert.Equal(completed[slug], await approvals.Requests.CountAsync(Ct));
                    Assert.Equal(completed[slug], await approvals.Decisions.CountAsync(Ct));
                }
            }
            if (target is not null) M7LoadEvidence.DemandPerformance(groups, target);
            status = mode == "Baseline" ? "BASELINE_RECORDED" : "PASS";
        }
        finally
        {
            wall.Stop(); process.Refresh();
            if (groups.Count == 0 && !samples.IsEmpty) groups = M7LoadEvidence.Summarize(samples, config);
            var report = new
            {
                schemaVersion = 1, mode, status, sourceSha = source, sourceDigest = digest, configurationSha256 = configHash,
                configuration = config, startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
                environment = new { osDescription = os, architecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = processors,
                    cpuModel = LinuxField("/proc/cpuinfo", "model name"), memory = LinuxField("/proc/meminfo", "MemTotal"),
                    framework = RuntimeInformation.FrameworkDescription, sqlVersion, runnerImage = System.Environment.GetEnvironmentVariable("ImageVersion"),
                    sdk = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json"))).RootElement.GetProperty("sdk").GetProperty("version").GetString() },
                transport = "Kestrel HTTPS loopback with pinned fixture certificate; native Orchard/OpenIddict; real SQL Server",
                topology = "Load client and application share the runner process; SQL Server is a dedicated local container. Not production capacity.",
                workloadAccounts = 8, enrolledAccounts = 9, creatorOAuthFamilies = workers.Count, reviewerOAuthFamilies = 4,
                virtualUsers = workers.Count, peakActiveVirtualUsers = peak, setupSeconds = setupWatch.Elapsed.TotalSeconds,
                elapsedSecondsIncludingRampAndDrain = wall.Elapsed.TotalSeconds,
                completedRequests = samples.Count, unexpectedResponsesOrTransportExceptions = samples.Count(x => !x.Success), workerFailures = failures.ToArray(),
                httpStatusCounts = samples.GroupBy(x => x.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToDictionary(x => x.Key, x => x.Count()),
                successfulRequestsPerSecondIncludingRampAndDrain = samples.Count(x => x.Success) / wall.Elapsed.TotalSeconds,
                steadyRequestsPerSecond = groups.Values.Sum(x => x.SteadyRequestsPerSecond),
                steadyWindowDefinition = "Successful requests both started and finished within [rampSeconds, rampSeconds + steadySeconds]. Drain is excluded from steady throughput, included in latency distributions.",
                percentileMethod = "Nearest rank, all request latencies including unsuccessful requests; milliseconds.",
                completedWorkflowsByTenant = completed, groups, sqlWaitDelta,
                applicationAndClientCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                applicationAndClientAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                applicationAndClientWorkingSetBytes = process.WorkingSet64,
                targetSha256 = target is null ? null : M7LoadEvidence.Hash(File.ReadAllBytes(Path.Combine(root, "tests/Load/m7-targets.json")))
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(report, M7LoadEvidence.Json), CancellationToken.None);
        }
    }
    private static string LinuxField(string path, string name)
    {
        if (!File.Exists(path)) return "not available on this operating system";
        return File.ReadLines(path).FirstOrDefault(x => x.Split(':', 2)[0].Trim() == name)?.Split(':', 2)[1].Trim() ?? "not reported";
    }
    private static async Task<Dictionary<string, long>> SqlWaitsAsync(string connection)
    {
        await using var sql = await SqlOperations.OpenAsync(connection, Ct);
        await using var command = SqlOperations.Command(sql, "SELECT [wait_type], [wait_time_ms] FROM sys.dm_os_wait_stats WHERE [wait_time_ms] > 0");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var waits = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct)) waits.Add(reader.GetString(0), reader.GetInt64(1));
        return waits;
    }
}
