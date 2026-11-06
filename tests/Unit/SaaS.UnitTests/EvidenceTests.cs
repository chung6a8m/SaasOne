using SaaS.Build;
using Xunit;
namespace SaaS.UnitTests;
public sealed class EvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "saas-m0-" + Guid.NewGuid().ToString("N"));
    public EvidenceTests() => Directory.CreateDirectory(_root);
    private void Trx(string name, int total, int executed, int passed, int failed, int skipped) => File.WriteAllText(Path.Combine(_root, name + ".trx"), $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions><UnitTest name="probe"><TestMethod className="{name}.Probe" codeBase="{name}.dll" name="probe" /></UnitTest></TestDefinitions>
          <ResultSummary outcome="Completed"><Counters total="{total}" executed="{executed}" passed="{passed}" failed="{failed}" notExecuted="{skipped}" /></ResultSummary>
        </TestRun>
        """);
    [Fact]
    public void Reads_real_counters_and_requires_both_suites()
    {
        Trx("SaaS.UnitTests", 4, 4, 4, 0, 0);
        Trx("SaaS.ArchitectureTests", 3, 3, 3, 0, 0);
        var result = EvidenceReport.Read(_root, ["SaaS.UnitTests", "SaaS.ArchitectureTests"]);
        Assert.Equal(7, result.Sum(s => s.Executed));
        Assert.Equal(7, result.Sum(s => s.Passed));
    }
    [Fact]
    public void Missing_suite_is_not_a_pass() => Assert.Throws<InvalidDataException>(() => EvidenceReport.Read(_root, ["SaaS.UnitTests"]));
    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(2, 1, 1, 0, 1)]
    [InlineData(2, 2, 1, 1, 0)]
    [InlineData(2, 1, 1, 0, 0)]
    public void Empty_skipped_failed_or_inconsistent_counters_fail(int total, int executed, int passed, int failed, int skipped)
    {
        Trx("SaaS.UnitTests", total, executed, passed, failed, skipped);
        Assert.Throws<InvalidDataException>(() => EvidenceReport.Read(_root, ["SaaS.UnitTests"]));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
