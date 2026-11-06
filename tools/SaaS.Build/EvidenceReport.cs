using System.Xml.Linq;

namespace SaaS.Build;

public sealed record SuiteResult(string Name, int Discovered, int Executed, int Passed, int Failed, int Skipped);

public static class EvidenceReport
{
    public static IReadOnlyList<SuiteResult> Read(string directory, IReadOnlyCollection<string> requiredSuites)
    {
        if (!Directory.Exists(directory) || requiredSuites.Count == 0) throw new InvalidDataException("Missing test results or required suite contract.");
        var reports = new Dictionary<string, SuiteResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            var assemblies = document.Descendants().Where(e => e.Name.LocalName == "TestMethod")
                .Select(e => Path.GetFileNameWithoutExtension((e.Attribute("codeBase")?.Value ?? "").Replace('\\', '/')))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var suite = requiredSuites.SingleOrDefault(s => assemblies.Contains(s, StringComparer.OrdinalIgnoreCase));
            if (suite is null) throw new InvalidDataException("Unknown or empty suite report: " + Path.GetFileName(path));
            if (reports.ContainsKey(suite)) throw new InvalidDataException("Duplicate/stale suite report: " + suite);
            var counters = document.Descendants().SingleOrDefault(e => e.Name.LocalName == "Counters")
                ?? throw new InvalidDataException("Missing test counters: " + suite);
            int Counter(string name) => int.TryParse(counters.Attribute(name)?.Value, out var value) && value >= 0
                ? value : throw new InvalidDataException("Invalid test counter: " + suite + "/" + name);
            var result = new SuiteResult(suite, Counter("total"), Counter("executed"), Counter("passed"), Counter("failed"), Counter("notExecuted"));
            if (result.Discovered == 0 || result.Discovered != result.Executed || result.Executed != result.Passed || result.Failed != 0 || result.Skipped != 0)
                throw new InvalidDataException($"Suite must execute every discovered case successfully: {result}");
            var summary = document.Descendants().Single(e => e.Name.LocalName == "ResultSummary");
            if (summary.Attribute("outcome")?.Value is not ("Completed" or "Passed")) throw new InvalidDataException("Failed/aborted run: " + suite);
            reports.Add(suite, result);
        }
        foreach (var suite in requiredSuites)
            if (!reports.ContainsKey(suite)) throw new InvalidDataException("Required suite did not execute: " + suite);
        return requiredSuites.Select(s => reports[s]).ToArray();
    }
}
