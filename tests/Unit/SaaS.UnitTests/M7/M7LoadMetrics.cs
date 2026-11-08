using System.Text.Json;
using Xunit;
namespace SaaS.UnitTests;

internal sealed record M7LoadConfig(int SchemaVersion, int VirtualUsersPerTenant, int RampSeconds, int MeasureSeconds, int ThinkMilliseconds)
{
    internal void Validate()
    {
        if (SchemaVersion != 1 || VirtualUsersPerTenant != 25 || RampSeconds is < 5 or > 60 || MeasureSeconds is < 30 or > 120
            || RampSeconds + MeasureSeconds > 180 || ThinkMilliseconds is < 0 or > 5000)
            throw new InvalidDataException("M7 requires four tenants, 25 virtual users each and a bounded ramp/measurement window.");
    }
    internal static M7LoadConfig Load()
    {
        var path = System.Environment.GetEnvironmentVariable("SAAS_M7_LOAD_CONFIG") ?? throw new InvalidOperationException("BLOCKED: committed load configuration required.");
        var value = JsonSerializer.Deserialize<M7LoadConfig>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty load configuration.");
        value.Validate(); return value;
    }
}
internal sealed record M7Distribution(int Count, double P50Ms, double P95Ms, double P99Ms, double MaxMs)
{
    internal static M7Distribution From(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0 || sorted.Any(x => !double.IsFinite(x) || x < 0)) throw new InvalidDataException("Latency samples must be nonempty, finite and nonnegative.");
        double At(double rank) => sorted[Math.Clamp((int)Math.Ceiling(rank * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, At(.50), At(.95), At(.99), sorted[^1]);
    }
}
internal sealed record M7Sample(string Tenant, string Operation, double StartedSeconds, double CompletedSeconds, double LatencyMs, int Status, bool Success, string? Failure);

public sealed class M7MetricTests
{
    [Fact]
    public void Nearest_rank_percentiles_preserve_tail_and_sample_count()
    {
        var result = M7Distribution.From(Enumerable.Range(1, 100).Select(x => (double)x));
        Assert.Equal(new M7Distribution(100, 50, 95, 99, 100), result);
        Assert.Equal(new M7Distribution(1, 7, 7, 7, 7), M7Distribution.From([7]));
    }
    [Fact]
    public void Empty_negative_and_nonfinite_samples_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => M7Distribution.From([]));
        foreach (var value in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<InvalidDataException>(() => M7Distribution.From([value]));
    }
    [Fact]
    public void Load_configuration_cannot_silently_reduce_population_or_measurement()
    {
        new M7LoadConfig(1, 25, 30, 60, 250).Validate();
        Assert.Throws<InvalidDataException>(() => new M7LoadConfig(1, 24, 30, 60, 250).Validate());
        Assert.Throws<InvalidDataException>(() => new M7LoadConfig(1, 25, 0, 60, 250).Validate());
        Assert.Throws<InvalidDataException>(() => new M7LoadConfig(1, 25, 30, 0, 250).Validate());
    }
}
