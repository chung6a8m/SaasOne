using Xunit;
namespace SaaS.UnitTests;

public sealed class M7LoadEvidenceTests
{
    [Fact]
    public void Nearest_rank_percentiles_and_window_counts_preserve_errors_and_drain()
    {
        Assert.Equal(95, M7LoadEvidence.Percentile(Enumerable.Range(1, 100).Select(x => (double)x), .95));
        Assert.Equal(99, M7LoadEvidence.Percentile(Enumerable.Range(1, 100).Select(x => (double)x), .99));
        var groups = M7LoadEvidence.Summarize([
            new("alpha", "read", 0, 1, 1000, 200, true, null),
            new("alpha", "read", 11, 12, 1000, 200, true, null),
            new("alpha", "read", 12, 13, 1000, 503, false, null),
            new("alpha", "read", 39, 41, 2000, 200, true, null)], new(25, 10, 30, 120));
        Assert.Equal(4, groups["alpha/read"].Count); Assert.Equal(1, groups["alpha/read"].Unexpected);
        Assert.Equal(1, groups["alpha/read"].SteadyCompletions); Assert.Equal(2000, groups["alpha/read"].P95Milliseconds);
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.Percentile([], .95));
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.Percentile([double.NaN], .95));
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.Summarize([], new(25, 10, 30, 120)));
    }
    [Fact]
    public void Empty_missing_failed_and_under_concurrent_load_evidence_cannot_pass()
    {
        var complete = M7LoadEvidence.RequiredGroups.ToDictionary(x => x, _ => new M7Distribution(25, 0, 1, 2, 3, 4, 1, 1));
        M7LoadEvidence.DemandComplete(complete, 100, 100, 0);
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandComplete([], 100, 100, 0));
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandComplete(complete, 100, 99, 0));
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandComplete(complete, 100, 100, 1));
        complete["gamma/approve"] = complete["gamma/approve"] with { Unexpected = 1 };
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandComplete(complete, 100, 100, 0));
        complete.Remove("gamma/approve");
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandComplete(complete, 100, 100, 0));
    }
    [Fact]
    public void Prelocked_performance_limits_are_not_inferred_from_the_acceptance_measurement()
    {
        var groups = M7LoadEvidence.RequiredGroups.ToDictionary(x => x, _ => new M7Distribution(25, 0, 1, 20, 30, 40, 25, 1));
        var target = new M7LoadTargets("source", "hash", "code", "config", DateTimeOffset.UtcNow, 4, "sql", "os",
            M7LoadEvidence.RequiredGroups.ToDictionary(x => x, _ => 10d), 1d);
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandPerformance(groups, target));
        var fast = groups.ToDictionary(x => x.Key, x => x.Value with { P95Milliseconds = 5 });
        M7LoadEvidence.DemandPerformance(fast, target);
        Assert.Throws<InvalidDataException>(() => M7LoadEvidence.DemandPerformance(fast, target with { MinimumSteadyRequestsPerSecond = 100 }));
    }
}
