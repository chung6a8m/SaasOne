// M0 scaffolding. Selective Orchard CMS dependencies are pinned, not activated.
// M1 supplies reviewed CMS registration, bootstrap and readiness before tenant middleware.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOrchardCore();
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", milestone = "M0" }));
app.MapFallback(() => Results.Problem(statusCode: 503, title: "Tenant runtime is not provisioned."));
app.Run();
public partial class Program { }
