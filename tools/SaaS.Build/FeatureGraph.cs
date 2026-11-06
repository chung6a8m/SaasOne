using System.Diagnostics;
using System.Text.Json;
namespace SaaS.Build;

public sealed record NativeFeature(string Id, string ExtensionId, string[] Dependencies);
public static class FeatureGraph
{
    public static IReadOnlyList<DependencyNode> Combine(IReadOnlyList<DependencyNode> projects, IEnumerable<NativeFeature> features)
    {
        var owners = projects.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var native = new Dictionary<string, NativeFeature>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
            if (string.IsNullOrWhiteSpace(feature.Id) || !owners.ContainsKey(feature.ExtensionId) || !native.TryAdd(feature.Id, feature))
                throw new InvalidDataException("Missing, duplicate or unowned native feature metadata.");
        if (native.Count == 0) throw new InvalidDataException("Native feature metadata is empty.");
        foreach (var feature in native.Values)
            if (feature.Dependencies is null || feature.Dependencies.Any(dependency => !native.ContainsKey(dependency)))
                throw new InvalidDataException("Unresolved native feature dependency: " + feature.Id);
        var graph = projects.Select(node => node with
        {
            Dependencies = node.Dependencies.Concat(native.Values.Where(feature => feature.ExtensionId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(feature => "feature:" + feature.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        }).ToList();
        graph.AddRange(native.Values.Select(feature => new DependencyNode("feature:" + feature.Id, owners[feature.ExtensionId].Layer,
            feature.Dependencies.Select(dependency => "feature:" + dependency).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())));
        return graph;
    }

    public static NativeFeature[] ReadBuiltHost(string root)
    {
        var host = Path.Combine(root, "src", "SaaS.Web", "bin", "Release", "net10.0", "SaaS.Web.dll");
        if (!File.Exists(host)) throw new InvalidDataException("A fresh Release host build is required for native feature inspection.");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--feature-graph");
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SAAS_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new InvalidDataException("Native feature inspection could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); throw new InvalidDataException("Native feature inspection timed out."); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new InvalidDataException("Native feature inspection failed; exit code " + process.ExitCode);
        var features = JsonSerializer.Deserialize<NativeFeature[]>(output.Result) ?? throw new InvalidDataException("Native feature inspection returned no metadata.");
        if (!features.Any(feature => feature.ExtensionId == "SaaS.Web") || !features.Any(feature => feature.ExtensionId == "SaaS.Theme.App"))
            throw new InvalidDataException("The real application and theme must be present in native metadata.");
        return features;
    }
}
