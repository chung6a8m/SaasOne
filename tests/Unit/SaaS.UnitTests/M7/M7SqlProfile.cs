using System.Diagnostics;
using SaaS.Tenancy.Operations;

namespace SaaS.UnitTests;

// Test-only observer: the only SqlClient payload properties read are a correlation UUID
// and monotonic timestamp. SQL, parameters, connections and errors are never read.
internal sealed class M7SqlProfile : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string Prefix = "Microsoft.Data.SqlClient.Write";
    private readonly object _gate = new();
    private readonly string _listenerName;
    private readonly IDisposable _all;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<(int Kind, Guid Id), (long Timestamp, string Stage)> _pending = [];
    private readonly Dictionary<string, StageTotals> _stages = new(StringComparer.Ordinal);
    private readonly int[] _counts = new int[2], _errors = new int[2];
    private readonly double[] _totals = new double[2], _maxima = new double[2];
    private int _orphans, _duplicates;
    private bool _stopped;

    private sealed class StageTotals
    {
        internal readonly int[] Count = new int[2], Errors = new int[2];
        internal readonly double[] Total = new double[2], Max = new double[2];
        internal void Add(int kind, bool error, double ms)
        {
            Count[kind]++;
            if (error) Errors[kind]++;
            Total[kind] += ms;
            Max[kind] = Math.Max(Max[kind], ms);
        }
        internal M7SqlMetric Metric(int kind) => new(Count[kind], Errors[kind], Total[kind], Max[kind]);
    }

    internal M7SqlProfile(string listenerName = "SqlClientDiagnosticListener")
    {
        _listenerName = listenerName;
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }
    private static bool Enabled(string name) => name is
        Prefix + "ConnectionOpenBefore" or Prefix + "ConnectionOpenAfter" or Prefix + "ConnectionOpenError"
        or Prefix + "CommandBefore" or Prefix + "CommandAfter" or Prefix + "CommandError";

    private static string CurrentStage()
    {
        for (var span = Activity.Current; span is not null; span = span.Parent)
            if (span.Source.Name == SaaSStageActivity.SourceName)
                return span.OperationName switch
                {
                    "routing" or "identity.fresh" or "identity.native" or "acl.authorize" or "acl.snapshot"
                    or "orders.read" or "orders.write" or "tenant.transaction" or "orders.lock.command"
                    or "orders.lock.resource" or "orders.lock.page" or "acl.lock" or "writegate.lock" or "other.lock"
                        => span.OperationName,
                    _ => "other"
                };
        return "other";
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != _listenerName) return;
        lock (_gate)
            if (!_stopped) _subscriptions.Add(listener.Subscribe(this, Enabled));
    }
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (!Enabled(value.Key) || value.Value is null) return;
        var type = value.Value.GetType();
        if (type.GetProperty("OperationId")?.GetValue(value.Value) is not Guid id || id == Guid.Empty
            || type.GetProperty("Timestamp")?.GetValue(value.Value) is not long timestamp) return;
        var kind = value.Key.StartsWith(Prefix + "ConnectionOpen", StringComparison.Ordinal) ? 0 : 1;
        var stage = value.Key.EndsWith("Before", StringComparison.Ordinal) ? CurrentStage() : null;
        lock (_gate)
        {
            if (_stopped) return;
            var key = (kind, id);
            if (stage is not null)
            {
                if (!_pending.TryAdd(key, (timestamp, stage))) _duplicates++;
                return;
            }
            if (!_pending.Remove(key, out var start) || timestamp < start.Timestamp) { _orphans++; return; }
            var ms = (timestamp - start.Timestamp) * 1000d / Stopwatch.Frequency;
            var error = value.Key.EndsWith("Error", StringComparison.Ordinal);
            _counts[kind]++;
            if (error) _errors[kind]++;
            _totals[kind] += ms;
            _maxima[kind] = Math.Max(_maxima[kind], ms);
            if (!_stages.TryGetValue(start.Stage, out var group)) _stages[start.Stage] = group = new();
            group.Add(kind, error, ms);
        }
    }
    internal M7SqlStageMetric[] SnapshotStages()
    {
        lock (_gate) return _stages.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new M7SqlStageMetric(x.Key, x.Value.Metric(0), x.Value.Metric(1))).ToArray();
    }
    internal M7SqlProfileReport Snapshot()
    {
        lock (_gate)
        {
            M7SqlMetric Metric(int kind) => new(_counts[kind], _errors[kind], _totals[kind], _maxima[kind]);
            return new(Metric(0), Metric(1), _pending.Count, _orphans, _duplicates);
        }
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        IDisposable[] subscriptions;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            subscriptions = _subscriptions.ToArray();
        }
        _all.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
internal sealed record M7SqlMetric(int Count, int Errors, double TotalMs, double MaxMs);
internal sealed record M7SqlProfileReport(M7SqlMetric Open, M7SqlMetric Command, int Pending, int Orphans, int Duplicates);
internal sealed record M7SqlStageMetric(string Stage, M7SqlMetric Open, M7SqlMetric Command);
