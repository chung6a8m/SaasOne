using SaaS.Build;
using Xunit;
namespace SaaS.ArchitectureTests;
public sealed class RepositoryGraphTests
{
    [Fact]
    public void Reads_the_real_restored_project_and_package_graph()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var nodes = RepositoryGraph.Load(directory.FullName);
        Assert.Contains("SaaS.Tenancy", Assert.Single(nodes, n => n.Id == "SaaS.Web").Dependencies);
        Assert.Contains("SaaS.Abstractions", Assert.Single(nodes, n => n.Id == "SaaS.Tenancy").Dependencies);
        Assert.Equal("Theme", Assert.Single(nodes, n => n.Id == "SaaS.Theme.App").Layer);
        Assert.Empty(DependencyRules.Validate(nodes));
    }
    [Fact]
    public void Missing_assets_cannot_be_an_empty_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "saas-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Throws<InvalidDataException>(() => RepositoryGraph.Load(root)); }
        finally { Directory.Delete(root, true); }
    }
}
