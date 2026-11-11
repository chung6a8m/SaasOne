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
                    if (context is { } label)
                        AddOperation(label.Tenant, label.Operation, name, duration);
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

    internal M7StageReport Snapshot()
    {
        lock (_gate) return new(Series(_stages), Series(_holds), OperationSeries());
    }
    public void Dispose() => _listener.Dispose();
}

internal sealed record M7StageMetric(string Stage, int Count, double TotalMs,
    double P50Ms, double P95Ms, double P99Ms, double MaxMs);
internal sealed record M7StageReport(M7StageMetric[] Stages, M7StageMetric[] Holds, M7OperationStageMetric[] ByOperation);
internal sealed record M7OperationStageMetric(string Tenant, string Operation, string Stage, int Count,
    double TotalMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs);
