using System.Xml.Linq;
using Xunit;
namespace SaaS.ArchitectureTests;

public sealed class M7ProductSourceTests
{
    [Fact]
    public void Independent_product_closure_contains_platform_and_own_theme_but_no_business_or_original_host()
    {
        var root = Root();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(Path.Combine(root, "samples/Portal/SaaS.Portal.csproj"));
        while (pending.TryDequeue(out var path))
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) continue;
            Assert.True(File.Exists(path), "The independent product and all of its project references must exist.");
            var project = XDocument.Load(path);
            Assert.Empty(project.Descendants("Reference"));
            var name = Path.GetFileNameWithoutExtension(path);
            Assert.False(name is "SaaS.Web" or "SaaS.Orders" or "SaaS.OrderApprovals", "A product example must not reference the original host or business implementation.");
            foreach (var reference in project.Descendants("ProjectReference"))
                pending.Enqueue(Path.Combine(Path.GetDirectoryName(path)!, reference.Attribute("Include")!.Value.Replace('\\', '/')));
        }
        foreach (var name in new[] { "SaaS.Abstractions", "SaaS.Tenancy", "SaaS.Identity", "SaaS.AccessControl", "SaaS.Auditing", "SaaS.Theme.Portal" })
            Assert.Contains(visited, path => Path.GetFileNameWithoutExtension(path) == name);
    }

    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository source is required.");
    }
}
