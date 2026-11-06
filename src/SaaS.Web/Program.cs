// M0 scaffolding. Selective Orchard CMS dependencies are pinned, not activated.
// M1 supplies reviewed CMS registration, bootstrap and readiness before tenant middleware.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOrchardCore();
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", milestone = "M0" }));
// The default fallback excludes file-like paths; M0 must close those routes too.
app.MapFallback("{**path}", () => Results.Problem(statusCode: 503, title: "Tenant runtime is not provisioned."));
app.Run();
public partial class Program { }
