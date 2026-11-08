using System.Diagnostics;
using System.Text.Json;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M7SqlProfileTests
{
    private const string Prefix = "Microsoft.Data.SqlClient.Write";
    private sealed class Payload(Guid id, long timestamp)
    {
        public Guid OperationId => id;
        public long Timestamp => timestamp;
        public string ConnectionString => throw new InvalidOperationException("Sensitive payload must not be read.");
        public string Command => throw new InvalidOperationException("SQL payload must not be read.");
        public string Exception => throw new InvalidOperationException("Exception payload must not be read.");
    }
    [Fact]
    public void Collector_pairs_open_and_command_events_without_reading_sensitive_payloads()
    {
        using var listener = new DiagnosticListener("m7-test-" + Guid.NewGuid());
        using var profile = new M7SqlProfile(listener.Name);
        var open = Guid.NewGuid(); var command = Guid.NewGuid(); var tick = Stopwatch.Frequency;
        listener.Write(Prefix + "ConnectionOpenBefore", new Payload(open, tick));
        listener.Write(Prefix + "CommandBefore", new Payload(command, tick));
        Assert.Equal(2, profile.Snapshot().Pending);
        listener.Write(Prefix + "ConnectionOpenAfter", new Payload(open, 2 * tick));
        listener.Write(Prefix + "CommandError", new Payload(command, 3 * tick));
        var result = profile.Snapshot();
        Assert.Equal(new M7SqlMetric(1, 0, 1000, 1000), result.Open);
        Assert.Equal(new M7SqlMetric(1, 1, 2000, 2000), result.Command);
        Assert.Equal(0, result.Pending);
        Assert.DoesNotContain("Sensitive", JsonSerializer.Serialize(result));
    }
    [Fact]
    public void Collector_rejects_duplicate_orphan_and_unrelated_events_and_stops_on_dispose()
    {
        using var listener = new DiagnosticListener("m7-test-" + Guid.NewGuid());
        var profile = new M7SqlProfile(listener.Name);
        var id = Guid.NewGuid(); var tick = Stopwatch.Frequency;
        listener.Write(Prefix + "ConnectionOpenBefore", new Payload(id, tick));
        listener.Write(Prefix + "ConnectionOpenBefore", new Payload(id, 2 * tick));
        listener.Write(Prefix + "ConnectionOpenAfter", new Payload(id, 3 * tick));
        listener.Write(Prefix + "ConnectionOpenAfter", new Payload(id, 4 * tick));
        listener.Write("unrelated", new Payload(id, tick));
        var result = profile.Snapshot();
        Assert.Equal(new M7SqlMetric(1, 0, 2000, 2000), result.Open);
        Assert.Equal(1, result.Duplicates); Assert.Equal(1, result.Orphans);
        profile.Dispose(); profile.Dispose();
        listener.Write(Prefix + "CommandBefore", new Payload(Guid.NewGuid(), tick));
        Assert.Equal(result, profile.Snapshot());
    }
}
