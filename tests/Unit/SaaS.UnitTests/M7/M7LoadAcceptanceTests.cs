using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using SaaS.Auditing;
using SaaS.OrderApprovals.Data;
using SaaS.Orders;
using SaaS.Orders.Data;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M7SqlAcceptanceTests
{
    [Fact]
    public async Task Hundred_virtual_users_report_nonempty_read_write_approval_baseline()
    {
        await fixture.DemandMatrixAsync(Ct);
        var config = M7LoadConfig.Load();
        using var driver = new M7LoadDriver(fixture, config);
        await driver.RunAsync(Ct);
        await fixture.DemandMatrixAsync(Ct);
    }
}

internal sealed class M7LoadDriver(M7SqlFixture fixture, M7LoadConfig config) : IDisposable
{
    private readonly ConcurrentQueue<M7Sample> _samples = new();
    private readonly ConcurrentQueue<(string Tenant, double CompletedSeconds)> _transactions = new();
    private readonly ConcurrentQueue<string> _failures = new();
    private readonly Stopwatch _watch = new();
    private readonly HttpClient _api = CreateLoadClient(fixture, config);
    private int _activeUsers, _peakUsers, _activeRequests, _peakRequests;
    private readonly string _prefix = ("M7LOAD-" + Guid.NewGuid().ToString("N")[..10] + "-").ToUpperInvariant();
    private static readonly string[] Slugs = ["alpha", "beta", "gamma", "delta"];
    private double End => config.RampSeconds + config.MeasureSeconds;

    private static HttpClient CreateLoadClient(M7SqlFixture fixture, M7LoadConfig config)
    {
        config.Validate();
        var client = fixture.NewClient();
        // An observation deadline, not an accepted service latency target. Native identity
        // setup and all correctness tests retain their original 30-second client deadline.
        client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
        return client;
    }
    public void Dispose() => _api.Dispose();

    internal async Task RunAsync(CancellationToken ct)
    {
        var tokens = new ConcurrentDictionary<string, string[]>();
        var reviewers = new ConcurrentDictionary<string, string[]>();
        await Task.WhenAll(Slugs.Select(async slug =>
        {
            var grants = new string[config.VirtualUsersPerTenant];
            for (var i = 0; i < grants.Length; i++)
                grants[i] = (await fixture.GrantAsync(fixture.Creators[slug][i % fixture.Creators[slug].Length], slug)).AccessToken;
            tokens[slug] = grants;
            if (slug is "beta" or "gamma") reviewers[slug] =
                [(await fixture.GrantAsync(fixture.Reviewers[0], slug)).AccessToken, (await fixture.GrantAsync(fixture.Reviewers[1], slug)).AccessToken];
        }));
        await fixture.DemandMatrixAsync(ct);
        using var sqlProfile = await M7SqlProfileSession.StartAsync(fixture.Environment.Admin, ct);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var gcStart = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var started = DateTimeOffset.UtcNow;
        _watch.Start();
        try
        {
            await Task.WhenAll(Slugs.SelectMany(slug => Enumerable.Range(0, config.VirtualUsersPerTenant)
                .Select(index => UserAsync(slug, index, tokens[slug][index], reviewers.TryGetValue(slug, out var values) ? values[index % values.Length] : null, ct))));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _failures.Enqueue("run-cancelled"); }
        finally { _watch.Stop(); }
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        var gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gcStart[i]).ToArray();
        var peakWorkingSet = process.PeakWorkingSet64;
        await sqlProfile.CompleteAsync(_samples.Count, CancellationToken.None);
        // Capture process resources before post-load integrity queries, matching the timer.
        var integrity = await VerifyPersistedAsync(CancellationToken.None);
        var samples = _samples.ToArray();
        var rows = new List<object>();
        var missing = new List<string>();
        foreach (var slug in Slugs)
        foreach (var operation in slug is "beta" or "gamma" ? new[] { "create", "edit", "submit", "approve", "detail", "list" } : new[] { "create", "edit", "submit", "detail", "list" })
        {
            var all = samples.Where(x => x.Tenant == slug && x.Operation == operation).ToArray();
            var measured = all.Where(x => x.StartedSeconds >= config.RampSeconds && x.StartedSeconds < End).ToArray();
            if (measured.Length == 0 || !measured.Any(x => x.Success)) missing.Add(slug + "/" + operation);
            rows.Add(new
            {
                tenant = slug, operation, totalRequests = all.Length, measuredRequests = measured.Length,
                measuredFailures = measured.Count(x => !x.Success), allPhaseFailures = all.Count(x => !x.Success),
                unexpected5xx = all.Count(x => x.Status >= 500), transportFailures = all.Count(x => x.Status == 0),
                successfulCompletionsPerSecond = all.Count(x => x.Success && x.CompletedSeconds >= config.RampSeconds && x.CompletedSeconds < End) / (double)config.MeasureSeconds,
                latency = measured.Length == 0 ? null : M7Distribution.From(measured.Select(x => x.LatencyMs))
            });
        }
        var elapsed = _watch.Elapsed.TotalSeconds;
        var valid = missing.Count == 0 && samples.Length > 0 && samples.All(x => x.Success) && _failures.IsEmpty && integrity.Count == 0 && _peakUsers == 100;
        M7Evidence.Write("load-samples.json", new { schemaVersion = 1, sourceSha = M7Evidence.SourceSha, samples = samples.OrderBy(x => x.StartedSeconds) });
        M7Evidence.Write("load.json", new
        {
            schemaVersion = 1, sourceSha = M7Evidence.SourceSha,
            mode = System.Environment.GetEnvironmentVariable("SAAS_M7_MODE") ?? "Baseline",
            status = valid ? "PASS" : "FAIL", acceptance = "NOT_ASSESSED", config,
            startedUtc = started, completedUtc = DateTimeOffset.UtcNow, elapsedIncludingDrainSeconds = elapsed,
            transport = "Kestrel HTTPS; native Orchard/OpenIddict; real SQL Server",
            topology = "Load generator and application share one process; SQL Server is a separate local container. Not a production capacity claim.",
            observationDeadline = "A bounded client measurement deadline, not a latency SLO. Earlier failed 30-second experiments remain failures; targets require separate owner review.",
            latencyCohort = "Requests started in the measured window, including their drain time; nearest-rank percentiles.",
            throughputCohort = "Successful requests completed inside the measured window, divided by window seconds.",
            accounts = new { creatorAccounts = 20, reviewerAccounts = 2, creatorGrants = 100, reviewerGrants = 4 },
            configuredVirtualUsers = 100, peakVirtualUsers = _peakUsers, peakConcurrentRequests = _peakRequests,
            completedTransactions = Slugs.Select(slug => new
            {
                tenant = slug, total = _transactions.Count(x => x.Tenant == slug),
                measuredCompletionsPerSecond = _transactions.Count(x => x.Tenant == slug && x.CompletedSeconds >= config.RampSeconds && x.CompletedSeconds < End) / (double)config.MeasureSeconds
            }),
            environment = new
            {
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription, logicalProcessors = System.Environment.ProcessorCount,
                gcAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, peakWorkingSetBytes = peakWorkingSet,
                processCpuSeconds = cpuSeconds,
                processCpuPercentOfAvailableCores = elapsed > 0 ? cpuSeconds / elapsed / System.Environment.ProcessorCount * 100 : 0,
                gcCollections
            },
            missingCells = missing, unexpectedFailures = _failures.ToArray(), integrityErrors = integrity, rows
        });
        Assert.True(valid, "M7 load failed correctness, error, population or mandatory metric coverage; inspect sanitized load.json and samples.");
    }
    internal static string DraftNumber(string prefix, string slug, int index, int sequence) =>
        (prefix + slug + "-" + index + "-" + sequence).ToUpperInvariant();

    private async Task UserAsync(string slug, int index, string token, string? reviewer, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(config.RampSeconds * index / (double)(config.VirtualUsersPerTenant - 1)), ct);
        Max(ref _peakUsers, Interlocked.Increment(ref _activeUsers));
        var owner = fixture.Creators[slug][index % fixture.Creators[slug].Length].Id;
        var sequence = 0;
        try
        {
            while (_watch.Elapsed.TotalSeconds < End && !ct.IsCancellationRequested)
            {
                var draft = new OrderDraft(DraftNumber(_prefix, slug, index, sequence++), "Load customer " + slug, 42m, "USD", "Disposable M7 load");
                try
                {
                    var created = await RequestAsync<OrderCommandResult>(slug, "create", token, "", draft, 201,
                        x => x.Order.CreatedBy == owner && x.Order.Number == draft.Number && x.Order.Status == OrderStatus.Draft, ct);
                    var edited = await RequestAsync<OrderCommandResult>(slug, "edit", token, "/" + created.Order.Id + "/edit",
                        new { expectedRevision = created.Order.Revision, expectedVersion = created.Order.Version, draft = draft with { Amount = 43m } }, 200,
                        x => x.Order.Id == created.Order.Id && x.Order.Amount == 43m && x.Order.Revision == created.Order.Revision + 1, ct);
                    var submitted = await RequestAsync<OrderCommandResult>(slug, "submit", token, "/" + edited.Order.Id + "/submit", M7Http.Revision(edited.Order), 200,
                        x => x.Order.Id == edited.Order.Id && x.Order.Status == (reviewer is null ? OrderStatus.Confirmed : OrderStatus.PendingApproval), ct);
                    var final = submitted.Order;
                    if (reviewer is not null)
                    {
                        var approved = await RequestAsync<OrderCommandResult>(slug, "approve", reviewer, "/" + final.Id + "/approve", M7Http.Revision(final), 200,
                            x => x.Order.Id == submitted.Order.Id && x.Order.Status == OrderStatus.Confirmed && x.Order.Revision == submitted.Order.Revision + 1, ct);
                        final = approved.Order;
                    }
                    await RequestAsync<OrderView>(slug, "detail", token, "/" + final.Id, null, 200,
                        x => x.Id == final.Id && x.CreatedBy == owner && x.Status == OrderStatus.Confirmed && x.Amount == 43m && x.Number == draft.Number, ct);
                    await RequestAsync<OrderPage>(slug, "list", token, "", null, 200, x => x.Items.All(item => item.CreatedBy == owner), ct);
                    _transactions.Enqueue((slug, _watch.Elapsed.TotalSeconds));
                }
                catch (M7LoadFailure) { _failures.Enqueue(slug + "/business-transaction-failed"); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { _failures.Enqueue(slug + "/cancelled"); return; }
                await Task.Delay(config.ThinkMilliseconds, ct);
            }
        }
        finally { Interlocked.Decrement(ref _activeUsers); }
    }
    private async Task<T> RequestAsync<T>(string slug, string operation, string token, string suffix, object? body, int expected, Func<T, bool> validate, CancellationToken ct)
    {
        var start = _watch.Elapsed.TotalSeconds; var ticks = Stopwatch.GetTimestamp();
        var status = 0; var success = false; string? failure = null;
        Max(ref _peakRequests, Interlocked.Increment(ref _activeRequests));
        try
        {
            using var response = await M7Http.SendAsync(fixture, token, slug, suffix, body, ct: ct, client: _api);
            status = (int)response.StatusCode;
            if (status != expected) { failure = "unexpected-status"; throw new M7LoadFailure(); }
            var value = await response.Content.ReadFromJsonAsync<T>(ct);
            if (value is null || !validate(value)) { failure = "invalid-business-result"; throw new M7LoadFailure(); }
            success = true; return value;
        }
        catch (M7LoadFailure) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { failure = "run-cancelled"; throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            failure = status == 0 ? "transport-or-timeout" : "invalid-response"; throw new M7LoadFailure();
        }
        finally
        {
            _samples.Enqueue(new(slug, operation, start, _watch.Elapsed.TotalSeconds, Stopwatch.GetElapsedTime(ticks).TotalMilliseconds, status, success, failure));
            Interlocked.Decrement(ref _activeRequests);
        }
    }
    private async Task<List<string>> VerifyPersistedAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var slug in Slugs)
        {
            try
            {
                var connection = fixture.Environment.CustomerAdmin(fixture.Tenant(slug));
                await using var orders = OrdersDbContext.Open(connection);
                var rows = await orders.Orders.AsNoTracking().Where(x => x.Number.StartsWith(_prefix)).ToArrayAsync(ct);
                var expected = _samples.Count(x => x.Tenant == slug && x.Operation == "create" && x.Success);
                var owners = fixture.Creators[slug].Select(x => x.Id).ToArray();
                if (rows.Length != expected || rows.Length == 0 || rows.Any(x => x.Status != OrderStatus.Confirmed || !owners.Contains(x.CreatedBy) || x.Amount != 43m)) errors.Add(slug + "/order-state-or-count");
                var ids = rows.Select(x => x.Id).ToArray();
                await using var audit = AuditDbContext.Open(connection);
                if (await audit.Events.CountAsync(x => ids.Contains(x.ResourceId ?? Guid.Empty) && x.Action == "Orders.Create", ct) != rows.Length) errors.Add(slug + "/create-audit-count");
                if (slug is "beta" or "gamma")
                {
                    await using var approvals = ApprovalsDbContext.Open(connection);
                    if (await approvals.Decisions.CountAsync(x => ids.Contains(x.OrderId), ct) != rows.Length) errors.Add(slug + "/approval-decision-count");
                    if (await audit.Events.CountAsync(x => ids.Contains(x.ResourceId ?? Guid.Empty) && x.Action == "Orders.Approve", ct) != rows.Length) errors.Add(slug + "/approval-audit-count");
                }
            }
            catch { errors.Add(slug + "/integrity-query-failed"); }
        }
        return errors;
    }
    private static void Max(ref int target, int value)
    {
        var previous = Volatile.Read(ref target);
        while (value > previous)
        {
            var actual = Interlocked.CompareExchange(ref target, value, previous);
            if (actual == previous) return;
            previous = actual;
        }
    }
    private sealed class M7LoadFailure : Exception { }
}
