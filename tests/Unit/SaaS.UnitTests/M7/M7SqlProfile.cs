using System.Diagnostics;
namespace SaaS.UnitTests;

// Test-first seam. The RED commit deliberately contains no collector implementation.
internal sealed class M7SqlProfile(string listenerName = "SqlClientDiagnosticListener") : IDisposable
{
    internal M7SqlProfileReport Snapshot() => new(new(0, 0, 0, 0), new(0, 0, 0, 0), 0, 0, 0);
    public void Dispose() { _ = listenerName; }
}
internal sealed record M7SqlMetric(int Count, int Errors, double TotalMs, double MaxMs);
internal sealed record M7SqlProfileReport(M7SqlMetric Open, M7SqlMetric Command, int Pending, int Orphans, int Duplicates);
