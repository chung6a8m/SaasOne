using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SaaS.Build;

public static class RepositoryGraph
{
    public static IReadOnlyList<DependencyNode> Load(string root)
    {
        root = Path.GetFullPath(root);
        var solution = Path.Combine(root, "SaasOne.slnx");
        if (!File.Exists(solution)) throw new InvalidDataException("Missing solution and restored dependency graph.");
        var projects = XDocument.Load(solution).Descendants("Project")
            .Select(p => Path.GetFullPath(Path.Combine(root, p.Attribute("Path")?.Value ?? throw new InvalidDataException("Project path missing."))))
            .Where(p => Path.GetRelativePath(root, p).Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal)).ToArray();
        if (projects.Length == 0) throw new InvalidDataException("No source projects in solution.");
        var actual = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/obj/") && !p.Replace('\\', '/').Contains("/bin/")).Select(Path.GetFullPath);
        if (!actual.Order().SequenceEqual(projects.Order())) throw new InvalidDataException("Every source project must be included in the solution.");

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var layers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Ensure(string name) { if (!edges.ContainsKey(name)) edges[name] = new(StringComparer.OrdinalIgnoreCase); }
        foreach (var project in projects)
        {
            var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (!File.Exists(assets)) throw new InvalidDataException($"Restore required for {Path.GetRelativePath(root, project)}.");
            using var json = JsonDocument.Parse(File.ReadAllText(assets));
            var name = json.RootElement.GetProperty("project").GetProperty("restore").GetProperty("projectName").GetString()
                ?? throw new InvalidDataException("Restored project has no name.");
            names.Add(project, name);
            var relative = Path.GetRelativePath(root, project).Replace('\\', '/');
            layers[name] = relative.StartsWith("src/Platform/") ? "Platform" : relative.StartsWith("src/Themes/") ? "Theme"
                : relative.StartsWith("src/Modules/") ? "Business" : name == "SaaS.Web" ? "Host" : "Other";
            Ensure(name);
            if (XDocument.Load(project).Descendants("Reference").Any())
                throw new InvalidDataException("Raw assembly references require an architecture-check adapter: " + relative);
        }
        foreach (var project in projects)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json")));
            var document = json.RootElement;
            var name = names[project];
            foreach (var target in document.GetProperty("targets").EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                {
                    var id = library.Name[..library.Name.LastIndexOf('/')];
                    Ensure(id);
                    if (library.Value.TryGetProperty("dependencies", out var dependencies))
                        foreach (var dependency in dependencies.EnumerateObject()) edges[id].Add(dependency.Name);
                }
            foreach (var framework in document.GetProperty("project").GetProperty("frameworks").EnumerateObject())
                if (framework.Value.TryGetProperty("dependencies", out var dependencies))
                    foreach (var dependency in dependencies.EnumerateObject()) edges[name].Add(dependency.Name);
            foreach (var framework in document.GetProperty("project").GetProperty("restore").GetProperty("frameworks").EnumerateObject())
                if (framework.Value.TryGetProperty("projectReferences", out var references))
                    foreach (var reference in references.EnumerateObject())
                    {
                        var path = Path.GetFullPath(reference.Name);
                        if (!names.TryGetValue(path, out var referencedName)) throw new InvalidDataException("Source reference outside the checked solution: " + path);
                        edges[name].Add(referencedName);
                    }
        }
        // Fail closed until M1 introduces and tests a feature-manifest graph adapter.
        foreach (var manifest in Directory.GetFiles(Path.Combine(root, "src"), "Manifest.cs", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/obj/") && !p.Replace('\\', '/').Contains("/bin/")))
            if (Regex.IsMatch(File.ReadAllText(manifest), @"\b(Dependencies|BaseTheme)\s*="))
                throw new InvalidDataException("Feature dependency adapter required before accepting: " + Path.GetRelativePath(root, manifest));
        return edges.OrderBy(p => p.Key).Select(p => new DependencyNode(p.Key, layers.GetValueOrDefault(p.Key, "Package"), p.Value.Order().ToArray())).ToArray();
    }
}
