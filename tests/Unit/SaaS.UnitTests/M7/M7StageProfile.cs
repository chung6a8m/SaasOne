using System.Diagnostics;
using SaaS.Tenancy.Operations;

namespace SaaS.UnitTests;

// Test-only observer. No SQL text, resource name, token, trace id, session or tenant identifier is exported.
internal sealed class M7StageProfile : IDisposable
{
    private static readonly HashSet<string> Known =
    [
        "routing", "identity.fresh", "identity.native", "acl.authorize", "acl.snapshot",
        "orders.read", "orders.write", "tenant.transaction", "orders.lock.command",
        "orders.lock.resource", "orders.lock.page", "acl.lock", "writegate.lock", "other.lock"
    ];
    private readonly object _gate = new();
    private readonly ActivityListener _listener;
    private readonly Dictionary<string, List<double>> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _holds = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Operation, string Stage), List<double>> _operationStages = [];
    private readonly Dictionary<Activity, List<(string Stage, DateTimeOffset Acquired)>> _acquired = [];

    internal M7StageProfile()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SaaSStageActivity.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = Stopped
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private static void Add(Dictionary<string, List<double>> series, string name, double value)
    {
        if (!series.TryGetValue(name, out var samples)) series[name] = samples = [];
        samples.Add(value);
    }

    private void Stopped(Activity activity)
    {
        if (!Known.Contains(activity.OperationName)) return;
        lock (_gate)
        {
            Add(_stages, activity.OperationName, activity.Duration.TotalMilliseconds);
            var context = M7OperationAttribution.For(activity);
            if (context is { } label)
                AddOperation(label.Tenant, label.Operation, activity.OperationName, activity.Duration.TotalMilliseconds);
            if (activity.OperationName is "orders.lock.command" or "orders.lock.resource" or "orders.lock.page")
            {
                // Only an explicit successful acquisition event is eligible for hold timing.
                var acquired = activity.Events.FirstOrDefault(e => e.Name == "acquired");
                if (acquired.Name == "acquired")
                {
                    var parent = activity.Parent;
                    while (parent is not null && (parent.Source.Name != SaaSStageActivity.SourceName || parent.OperationName != "tenant.transaction"))
                        parent = parent.Parent;
                    if (parent is not null)
                    {
                        if (!_acquired.TryGetValue(parent, out var markers)) _acquired[parent] = markers = [];
                        markers.Add(("orders.hold." + activity.OperationName["orders.lock.".Length..], acquired.Timestamp));
                    }
                }
            }
            if (activity.OperationName == "tenant.transaction" && _acquired.Remove(activity, out var holds))
            {
                // Transaction span stops after commit/disposal. This is an approximation,
                // not a SQL-server lock release callback or blocking-owner identity.
                var ended = new DateTimeOffset(activity.StartTimeUtc + activity.Duration);
                foreach (var (name, started) in holds)
                {
                    var duration = Math.Max(0, (ended - started).TotalMilliseconds);
                    Add(_holds, name, duration);
                    if (context is { } holder)
                        AddOperation(holder.Tenant, holder.Operation, name, duration);
                }
            }
        }
    }

    private static M7StageMetric[] Series(Dictionary<string, List<double>> series) =>
        series.OrderBy(x => x.Key, StringComparer.Ordinal).Select(entry =>
        {
            var sorted = entry.Value.Order().ToArray();
            double Percentile(double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
            return new M7StageMetric(entry.Key, sorted.Length, sorted.Sum(),
                Percentile(.50), Percentile(.95), Percentile(.99), sorted[^1]);
        }).ToArray();

    private void AddOperation(string tenant, string operation, string stage, double durationMs)
    {
        var key = (tenant, operation, stage);
        if (!_operationStages.TryGetValue(key, out var samples)) _operationStages[key] = samples = [];
        samples.Add(durationMs);
    }

    private M7OperationStageMetric[] OperationSeries() =>
        _operationStages.OrderBy(x => x.Key.Tenant, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Operation, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Stage, StringComparer.Ordinal)
            .Select(entry =>
            {
                var sorted = entry.Value.Order().ToArray();
                double Percentile(double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
                return new M7OperationStageMetric(entry.Key.Tenant, entry.Key.Operation, entry.Key.Stage,
                    sorted.Length, sorted.Sum(), Percentile(.50), Percentile(.95), Percentile(.99), sorted[^1]);
            }).ToArray();


    // A present request stage is not proof that the required SQL locks were
    // observed. Missing is recorded as missing, NEVER as a measured 0-ms lock.
    // This inventory is test-only, and contains only allowlisted labels.
    private static M7LockCoverage CheckLockCoverage(M7OperationStageMetric[] stages)
    {
        var available = stages.Where(x => x.Count > 0)
            .Select(x => (x.Tenant, x.Operation, x.Stage)).ToHashSet();
        var missing = new List<M7MissingLockStage>();
        var complete = 0;
        foreach (var tenant in new[] { "alpha", "beta", "gamma", "delta" })
        foreach (var operation in tenant is "beta" or "gamma"
            ? new[] { "create", "edit", "submit", "approve", "detail", "list" }
            : new[] { "create", "edit", "submit", "detail", "list" })
        {
            var before = missing.Count;
            var required = operation switch
            {
                "list" => new[] { "orders.lock.page", "orders.hold.page" },
                "detail" => new[] { "orders.lock.resource", "orders.hold.resource" },
                _ => new[] {
                    "orders.lock.command", "orders.hold.command",
                    "orders.lock.resource", "orders.hold.resource"
                }
            };
            foreach (var stage in required)
                if (!available.Contains((tenant, operation, stage)))
                    missing.Add(new(tenant, operation, stage));
            if (missing.Count == before) complete++;
        }
        return new(22, complete, missing.ToArray());
    }

    internal M7StageReport Snapshot()
    {
        lock (_gate)
        {
            var operationStages = OperationSeries();
            return new(Series(_stages), Series(_holds), operationStages, CheckLockCoverage(operationStages));
        }
    }
    public void Dispose() => _listener.Dispose();
}

internal sealed record M7StageMetric(string Stage, int Count, double TotalMs,
    double P50Ms, double P95Ms, double P99Ms, double MaxMs);
internal sealed record M7StageReport(M7StageMetric[] Stages, M7StageMetric[] Holds,
    M7OperationStageMetric[] ByOperation, M7LockCoverage LockCoverage);
internal sealed record M7LockCoverage(int ExpectedCells, int CompleteCells, M7MissingLockStage[] Missing);
internal sealed record M7MissingLockStage(string Tenant, string Operation, string Stage);
internal sealed record M7OperationStageMetric(string Tenant, string Operation, string Stage, int Count,
    double TotalMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs);
