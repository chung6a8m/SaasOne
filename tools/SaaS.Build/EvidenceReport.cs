namespace SaaS.Build;
public sealed record SuiteResult(string Name, int Discovered, int Executed, int Passed, int Failed, int Skipped);
public static class EvidenceReport
{
    public static IReadOnlyList<SuiteResult> Read(string directory, IReadOnlyCollection<string> requiredSuites) => [];
}
