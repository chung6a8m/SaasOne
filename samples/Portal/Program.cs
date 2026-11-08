using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrchardCore.DisplayManagement.Manifest;
using OrchardCore.Environment.Extensions;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Theme.Portal;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ApplicationName = typeof(Program).Assembly.GetName().Name,
    ContentRootPath = AppContext.BaseDirectory
});
// This independent, read-only composition never hosts public setup, customer routes or
// identity-management endpoints. Its own theme renders the platform landing page.
builder.Logging.ClearProviders();
builder.Services.AddOrchardCore().AddMvc();
await using var app = builder.Build();
if (args.Length == 1 && args[0] == "--feature-graph")
{
    var features = app.Services.GetRequiredService<IExtensionManager>().GetFeatures().Select(feature => new
    {
        feature.Id,
        ExtensionId = feature.Extension.Id,
        Dependencies = feature.Dependencies.Concat(feature.Extension.Manifest.ModuleInfo is ThemeAttribute { BaseTheme.Length: > 0 } theme
            ? new[] { theme.BaseTheme } : Array.Empty<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    }).OrderBy(feature => feature.Id, StringComparer.Ordinal).ToArray();
    if (!features.Any(feature => feature.Id == "SaaS.Theme.Portal")) throw new InvalidDataException("Portal theme metadata is missing.");
    Console.WriteLine(JsonSerializer.Serialize(features));
    return;
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health/live")
    {
        await context.Response.WriteAsJsonAsync(new { status = "live" }, context.RequestAborted);
        return;
    }
    try
    {
        var connection = new TenancyConnections().Platform("runtime");
        await SqlOperations.RequireRuntimeAsync(connection, context.RequestAborted);
        await using var db = TenancyConnections.PlatformContext(connection);
        await TenancyConnections.VerifySchemaAsync(db, context.RequestAborted);
        var bootstrap = await db.Bootstrap.AsNoTracking().SingleAsync(context.RequestAborted);
        if (!bootstrap.Completed || bootstrap.BaselineVersion != 1 || bootstrap.InstallationId == Guid.Empty)
            throw new InvalidDataException("Platform bootstrap is incomplete.");
        await using var sql = await SqlOperations.OpenAsync(connection, context.RequestAborted);
        await PlatformReleaseBarrier.DemandClearAsync(sql, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
    catch
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { status = "not-ready" }, context.RequestAborted);
        return;
    }
    await next(context);
});
app.MapGet("/health/ready", () => Results.Json(new { status = "ready" }));
app.MapGet("/", () => Results.Content(PortalPage.Html, "text/html; charset=utf-8"));
app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
await app.RunAsync();
public partial class Program { }
