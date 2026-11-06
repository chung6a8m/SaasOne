using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SaaS.Build;

public static class LockInventory
{
    public static JsonObject Create(string root)
    {
        var projects = new JsonObject();
        var solution = XDocument.Load(Path.Combine(root, "SaasOne.slnx"));
        foreach (var project in solution.Descendants("Project").Select(p => p.Attribute("Path")!.Value).Order(StringComparer.Ordinal))
        {
            var path = (Path.GetDirectoryName(project)!.Replace('\\', '/') + "/packages.lock.json");
            if (!File.Exists(Path.Combine(root, path))) throw new InvalidDataException("NuGet lock missing: " + path);
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(root, path)))!["dependencies"]!.AsObject();
            var targets = new JsonObject();
            foreach (var target in source)
            {
                var packages = new JsonObject();
                foreach (var package in target.Value!.AsObject())
                {
                    var data = new JsonObject();
                    foreach (var property in package.Value!.AsObject())
                        if (property.Key is "type" or "resolved" or "dependencies" or "contentHash") data[property.Key] = property.Value?.DeepClone();
                    packages[package.Key] = data;
                }
                targets[target.Key] = packages;
            }
            projects[path] = targets;
        }
        if (projects.Count == 0) throw new InvalidDataException("No locked projects.");
        return new JsonObject { ["generatedBy"] = "dotnet restore; do not hand-edit", ["projects"] = projects };
    }

    public static void Validate(string root)
    {
        var central = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var versions = central.Descendants("PackageVersion").ToArray();
        if (versions.Length == 0 || versions.Any(p => !Regex.IsMatch(p.Attribute("Version")?.Value ?? "", @"^\d+\.\d+\.\d+$")))
            throw new InvalidDataException("Central packages must have exact release versions.");
        foreach (var project in XDocument.Load(Path.Combine(root, "SaasOne.slnx")).Descendants("Project"))
            foreach (var reference in XDocument.Load(Path.Combine(root, project.Attribute("Path")!.Value)).Descendants("PackageReference"))
                if (reference.Attribute("Version") is not null || reference.Attribute("VersionOverride") is not null || reference.Element("Version") is not null)
                    throw new InvalidDataException("Package versions belong in Directory.Packages.props.");
        var existing = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs/compatibility/resolved-packages.json")));
        if (!JsonNode.DeepEquals(existing, Create(root))) throw new InvalidDataException("Resolved package inventory differs from committed NuGet locks.");
    }

    public static void Write(string root) => File.WriteAllText(Path.Combine(root, "docs/compatibility/resolved-packages.json"),
        Create(root).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}
