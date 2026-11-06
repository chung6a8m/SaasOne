using System.Text.Json;
using OrchardCore.DisplayManagement.Manifest;
using OrchardCore.Environment.Extensions;
namespace SaaS.Web.Orchard;

public static class NativeFeatureMetadata
{
    public static async Task WriteAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.Services.AddOrchardCore().AddMvc();
        await using var application = builder.Build(); // Metadata only; no listener and no database settings.
        var manager = application.Services.GetRequiredService<IExtensionManager>();
        var features = manager.GetFeatures().Select(feature => new
        {
            feature.Id,
            ExtensionId = feature.Extension.Id,
            Dependencies = feature.Dependencies.Concat(feature.Extension.Manifest.ModuleInfo is ThemeAttribute { BaseTheme.Length: > 0 } theme
                ? new[] { theme.BaseTheme } : Array.Empty<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        }).OrderBy(feature => feature.Id, StringComparer.Ordinal).ToArray();
        if (features.Length == 0) throw new InvalidDataException("Native Orchard feature inventory is empty.");
        Console.WriteLine(JsonSerializer.Serialize(features));
    }
}
