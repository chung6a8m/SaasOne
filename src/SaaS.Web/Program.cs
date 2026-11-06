var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOrchardCore();
var app = builder.Build();

// Maintenance has no public HTTP surface, including before platform bootstrap.
// The M1 runtime adapter will admit only its explicitly registered read-only routes.
app.Use(async (context, next) =>
{
    var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
    string[] closed = ["setup", "admin", "api", "connect", "media", "templates", "features", "recipes"];
    if (segments.Take(2).Any(segment => closed.Contains(segment, StringComparer.OrdinalIgnoreCase)
        || segment.StartsWith("OrchardCore.", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", milestone = "M1" }));
app.MapFallback("{**path}", () => Results.Problem(statusCode: 503, title: "Tenant runtime is not provisioned."));
app.Run();
public partial class Program { }
