using System.Diagnostics;
namespace SaaS.UnitTests;

// Test-only observer: the only payload properties read are a correlation UUID and
// a monotonic timestamp. SQL, parameters, connections and errors are never read.
internal sealed class M7SqlProfile : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string Prefix = "Microsoft.Data.SqlClient.Write";
    private readonly object _gate = new();
    private readonly string _listenerName;
    private readonly IDisposable _all;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<(int Kind, Guid Id), long> _pending = [];
    private readonly int[] _counts = new int[2], _errors = new int[2];
    private readonly double[] _totals = new double[2], _maxima = new double[2];
    private int _orphans, _duplicates;
    private bool _stopped;

    internal M7SqlProfile(string listenerName = "SqlClientDiagnosticListener")
    {
        _listenerName = listenerName;
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }
    private static bool Enabled(string name) => name is
        Prefix + "ConnectionOpenBefore" or Prefix + "ConnectionOpenAfter" or Prefix + "ConnectionOpenError"
        or Prefix + "CommandBefore" or Prefix + "CommandAfter" or Prefix + "CommandError";
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
        lock (_gate)
        {
            if (_stopped) return;
            var key = (kind, id);
            if (value.Key.EndsWith("Before", StringComparison.Ordinal))
            {
                if (!_pending.TryAdd(key, timestamp)) _duplicates++;
                return;
            }
            if (!_pending.Remove(key, out var start) || timestamp < start) { _orphans++; return; }
            var ms = (timestamp - start) * 1000d / Stopwatch.Frequency;
            _counts[kind]++;
            if (value.Key.EndsWith("Error", StringComparison.Ordinal)) _errors[kind]++;
            _totals[kind] += ms;
            _maxima[kind] = Math.Max(_maxima[kind], ms);
        }
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
