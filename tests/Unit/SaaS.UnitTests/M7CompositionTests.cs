using System.Text.Json;
using System.Xml.Linq;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M7CompositionTests
{
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidDataException("Repository root missing.");
        }
    }

    [Fact]
    public void Independent_product_has_its_own_host_theme_and_platform_composition()
    {
        var path = Path.Combine(Root, "samples/Workspace/SaaS.Workspace.Web/SaaS.Workspace.Web.csproj");
        Assert.True(File.Exists(path), "The second product must exist, not merely disable Orders in SaaS.Web.");
        var project = XDocument.Load(path);
        var references = project.Descendants("ProjectReference").Select(x => (string?)x.Attribute("Include") ?? "").ToArray();
        Assert.Contains(references, x => x.EndsWith("SaaS.Tenancy.csproj", StringComparison.Ordinal));
        Assert.Contains(references, x => x.EndsWith("SaaS.Workspace.Theme.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(references, x => x.Contains("SaaS.Web.csproj", StringComparison.Ordinal) || x.Contains("SaaS.Orders", StringComparison.Ordinal) || x.Contains("SaaS.OrderApprovals", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Root, "samples/Workspace/Workspace.slnx")));
    }

    [Fact]
    public void Module_template_is_named_and_does_not_depend_on_the_business_host()
    {
        var config = Path.Combine(Root, "templates/saas-module/.template.config/template.json");
        Assert.True(File.Exists(config), "A reusable module template is required.");
        using var template = JsonDocument.Parse(File.ReadAllText(config));
        Assert.Equal("saas-module", template.RootElement.GetProperty("shortName").GetString());
        Assert.Equal("SaaS.Example", template.RootElement.GetProperty("sourceName").GetString());
        var project = XDocument.Load(Path.Combine(Root, "templates/saas-module/SaaS.Example.csproj"));
        var references = project.Descendants("ProjectReference").Select(x => (string?)x.Attribute("Include") ?? "").ToArray();
        Assert.Single(references);
        Assert.EndsWith("SaaS.Abstractions.csproj", references[0]);
    }
}
