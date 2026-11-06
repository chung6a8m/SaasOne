using SaaS.Tenancy.Data;
using SaaS.Web.Maintenance;
using SaaS.Web.Orchard;

if (args.Length == 1 && args[0] == "--feature-graph")
{
    await NativeFeatureMetadata.WriteAsync();
    return;
}
if (args.Length > 0 && args[0] == "--maintenance")
{
    Environment.ExitCode = await MaintenanceCommand.RunAsync(args[1..]);
    return;
}
var builder = WebApplication.CreateBuilder(args);
if (TenancyConnections.RuntimeConfigured)
    NativeServices.Add(builder.Services, new RegistryShellSettings(new TenancyConnections()), runtimeRoutes: true);
else builder.Services.AddOrchardCore();
var app = builder.Build();
app.Use(RuntimeBoundary.EnterAsync);
if (TenancyConnections.RuntimeConfigured) app.UseOrchardCore();
app.Run(context => { context.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; });
app.Run();
public partial class Program { }
