using System.Diagnostics;
using System.Text.Json;
namespace SaaS.Build;

public sealed record NativeFeature(string Id, string ExtensionId, string[] Dependencies, bool EnabledInM1 = false);
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
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in native.Values)
        {
            if (feature.Dependencies is null) throw new InvalidDataException("Missing native dependency metadata: " + feature.Id);
            foreach (var dependency in feature.Dependencies.Where(dependency => !native.ContainsKey(dependency)))
            {
                // Selective M0 packages include dormant OpenID features whose CMS dependencies are intentionally absent.
                // Preserve these edges as explicitly unavailable; never accept missing first-party or enabled dependencies.
                if (owners[feature.ExtensionId].Layer != "Package" || !feature.ExtensionId.StartsWith("OrchardCore.", StringComparison.Ordinal)
                    || !dependency.StartsWith("OrchardCore.", StringComparison.Ordinal))
                    throw new InvalidDataException("Unresolved native feature dependency: " + feature.Id + " -> " + dependency);
                unavailable.Add(dependency);
            }
        }
        foreach (var root in native.Values.Where(feature => feature.EnabledInM1))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(root.Dependencies);
            while (pending.TryPop(out var dependency))
            {
                if (!seen.Add(dependency)) continue;
                if (!native.TryGetValue(dependency, out var target)) throw new InvalidDataException("Enabled profile requires unavailable feature: " + dependency);
                foreach (var child in target.Dependencies) pending.Push(child);
            }
        }
        var graph = projects.Select(node => node with
        {
            Dependencies = node.Dependencies.Concat(native.Values.Where(feature => feature.ExtensionId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(feature => "feature:" + feature.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        }).ToList();
        graph.AddRange(native.Values.Select(feature => new DependencyNode("feature:" + feature.Id, owners[feature.ExtensionId].Layer,
            feature.Dependencies.Select(dependency => "feature:" + dependency).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())));
        graph.AddRange(unavailable.Select(id => new DependencyNode("feature:" + id, "UnavailableNativeFeature", [])));
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
        if (!features.Any(feature => feature.ExtensionId == "SaaS.Web" && feature.EnabledInM1)
            || !features.Any(feature => feature.ExtensionId == "SaaS.Theme.App" && feature.EnabledInM1))
            throw new InvalidDataException("The real application and theme must be enabled in native metadata.");
        return features;
    }
}
