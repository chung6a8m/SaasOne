using System.Security.Cryptography;
using System.Text.Json;
namespace SaaS.UnitTests;

internal sealed record M7LoadConfiguration(int VirtualUsersPerTenant, int RampSeconds, int SteadySeconds, int RequestTimeoutSeconds)
{
    public void Validate()
    {
        if (VirtualUsersPerTenant != 25 || RampSeconds < 1 || SteadySeconds < 30 || SteadySeconds > 120 || RequestTimeoutSeconds != 120)
            throw new InvalidDataException("m7_workload_configuration_invalid");
    }
}
internal sealed record M7Sample(string Tenant, string Operation, double StartedSeconds, double FinishedSeconds,
    double Milliseconds, int Status, bool Success, string? ExceptionType);
internal sealed record M7Distribution(int Count, int Unexpected, double P50Milliseconds, double P95Milliseconds,
    double P99Milliseconds, double MaxMilliseconds, int SteadyCompletions, double SteadyRequestsPerSecond);
internal sealed record M7LoadTargets(string BaselineSource, string BaselineReportSha256, string SourceDigest,
    string ConfigurationSha256, DateTimeOffset LockedAtUtc, int LogicalProcessors, string SqlVersion,
    string OsDescription, Dictionary<string, double> MaxP95Milliseconds, double MinimumSteadyRequestsPerSecond);

internal static class M7LoadEvidence
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string[] RequiredGroups => M7TenantHarness.Slugs.SelectMany(slug =>
        (slug is "beta" or "gamma" ? new[] { "create", "read", "edit", "submit", "approve" } : new[] { "create", "read", "edit", "submit" })
        .Select(operation => slug + "/" + operation)).Order(StringComparer.Ordinal).ToArray();
    public static double Percentile(IEnumerable<double> values, double probability)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0 || probability <= 0 || probability > 1 || !double.IsFinite(probability)
            || ordered.Any(x => !double.IsFinite(x) || x < 0)) throw new InvalidDataException("invalid_latency_samples");
        return ordered[(int)Math.Ceiling(probability * ordered.Length) - 1];
    }
    public static Dictionary<string, M7Distribution> Summarize(IEnumerable<M7Sample> values, M7LoadConfiguration configuration)
    {
        configuration.Validate();
        var samples = values.ToArray();
        if (samples.Length == 0 || samples.Any(x => !double.IsFinite(x.StartedSeconds) || !double.IsFinite(x.FinishedSeconds)
            || x.StartedSeconds < 0 || x.FinishedSeconds < x.StartedSeconds)) throw new InvalidDataException("invalid_load_window");
        return samples.GroupBy(x => x.Tenant + "/" + x.Operation).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
        {
            var all = group.ToArray(); var latency = all.Select(x => x.Milliseconds).ToArray();
            var steady = all.Count(x => x.Success && x.StartedSeconds >= configuration.RampSeconds
                && x.FinishedSeconds <= configuration.RampSeconds + configuration.SteadySeconds);
            return new M7Distribution(all.Length, all.Count(x => !x.Success), Percentile(latency, .5), Percentile(latency, .95),
                Percentile(latency, .99), latency.Max(), steady, (double)steady / configuration.SteadySeconds);
        }, StringComparer.Ordinal);
    }
    public static void DemandComplete(Dictionary<string, M7Distribution> groups, int workers, int peakWorkers, int workerFailures)
    {
        if (!groups.Keys.Order(StringComparer.Ordinal).SequenceEqual(RequiredGroups, StringComparer.Ordinal)
            || workers != 100 || peakWorkers != 100 || workerFailures != 0
            || groups.Values.Any(x => x.Count < 25 || x.Unexpected != 0 || x.SteadyCompletions < 1
                || !double.IsFinite(x.P95Milliseconds) || x.P95Milliseconds <= 0))
            throw new InvalidDataException("load_correctness_or_sample_inventory_failed");
    }
    public static M7LoadTargets ReadLockedTargets(string root, string sourceDigest, string configHash, DateTimeOffset started,
        int logicalProcessors, string sqlVersion, string osDescription)
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, "tests/Load/m7-targets.json"));
        var target = JsonSerializer.Deserialize<M7LoadTargets>(bytes, Json) ?? throw new InvalidDataException("load_targets_missing");
        var baselineBytes = File.ReadAllBytes(Path.Combine(root, "docs/verification/m7-baseline.json"));
        using var baseline = JsonDocument.Parse(baselineBytes); var b = baseline.RootElement;
        if (Hash(baselineBytes) != target.BaselineReportSha256 || target.SourceDigest != sourceDigest
            || target.ConfigurationSha256 != configHash || target.LockedAtUtc >= started
            || target.LogicalProcessors != logicalProcessors || target.SqlVersion != sqlVersion || target.OsDescription != osDescription
            || target.BaselineSource != b.GetProperty("sourceSha").GetString() || b.GetProperty("sourceDigest").GetString() != sourceDigest
            || b.GetProperty("configurationSha256").GetString() != configHash || b.GetProperty("mode").GetString() != "Baseline"
            || b.GetProperty("status").GetString() != "BASELINE_RECORDED" || b.GetProperty("finishedUtc").GetDateTimeOffset() >= target.LockedAtUtc
            || !target.MaxP95Milliseconds.Keys.Order(StringComparer.Ordinal).SequenceEqual(RequiredGroups, StringComparer.Ordinal)
            || target.MaxP95Milliseconds.Values.Any(x => !double.IsFinite(x) || x <= 0)
            || !double.IsFinite(target.MinimumSteadyRequestsPerSecond) || target.MinimumSteadyRequestsPerSecond <= 0)
            throw new InvalidDataException("load_target_provenance_or_environment_mismatch");
        return target;
    }
    public static void DemandPerformance(Dictionary<string, M7Distribution> groups, M7LoadTargets targets)
    {
        if (!groups.Keys.Order(StringComparer.Ordinal).SequenceEqual(RequiredGroups, StringComparer.Ordinal)
            || !targets.MaxP95Milliseconds.Keys.Order(StringComparer.Ordinal).SequenceEqual(RequiredGroups, StringComparer.Ordinal)
            || groups.Any(x => x.Value.Unexpected != 0 || x.Value.P95Milliseconds > targets.MaxP95Milliseconds[x.Key])
            || groups.Values.Sum(x => x.SteadyRequestsPerSecond) < targets.MinimumSteadyRequestsPerSecond)
            throw new InvalidDataException("prelocked_load_target_failed");
    }
}
