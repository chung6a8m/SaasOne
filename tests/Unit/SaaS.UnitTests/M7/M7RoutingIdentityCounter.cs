using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SaaS.Tenancy.Data;

namespace SaaS.UnitTests;

/// <summary>
/// Test-only real EF SQL counter. It records only a count, never connection strings,
/// SQL text, principals, tenant IDs, parameter values, or native identities.
/// </summary>
internal sealed class M7RoutingIdentityCounter : IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _database;
    private readonly IDisposable _listener;
    private readonly List<IDisposable> _subscriptions = [];
    private int _count;

    internal M7RoutingIdentityCounter(Guid tenantId)
    {
        _database = TenancyConnections.TenantDatabase(tenantId);
        _listener = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal int Count => Volatile.Read(ref _count);

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
            _subscriptions.Add(listener.Subscribe(this,
                key => key == RelationalEventId.CommandExecuted.Name));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != RelationalEventId.CommandExecuted.Name
            || value.Value is not CommandExecutedEventData data
            || data.Context is not TenantDbContext
            || !string.Equals(data.Command.Connection?.Database, _database, StringComparison.OrdinalIgnoreCase)
            || !data.Command.CommandText.Contains("[tenancy].[Identity]", StringComparison.OrdinalIgnoreCase))
            return;
        Interlocked.Increment(ref _count);
    }

    public void OnError(Exception error) { }
    public void OnCompleted() { }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
    }
}
