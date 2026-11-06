// M0 scaffolding. No Orchard middleware, setup endpoint, or business routes before M1.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOrchardCms();
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", milestone = "M0" }));
app.MapFallback(() => Results.Problem(statusCode: 503, title: "Tenant runtime is not provisioned."));
app.Run();
public partial class Program { }
