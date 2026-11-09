namespace SaaS.UnitTests;

// RED scaffold: intentionally does not subscribe yet. Behavioral tests must fail.
internal sealed class M7StageProfile : IDisposable
{
    internal M7StageReport Snapshot() => new([], []);
    public void Dispose() { }
}

internal sealed record M7StageMetric(string Stage, int Count, double TotalMs,
    double P50Ms, double P95Ms, double P99Ms, double MaxMs);
internal sealed record M7StageReport(M7StageMetric[] Stages, M7StageMetric[] Holds);
