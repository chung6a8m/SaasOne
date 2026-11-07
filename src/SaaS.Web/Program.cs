using SaaS.Identity;
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
var identityEnabled = IdentityDeployment.Configured;
if (identityEnabled)
    IdentityRuntimeHost.Configure(builder, IdentityDeployment.Load());
else if (TenancyConnections.RuntimeConfigured)
    NativeServices.Add(builder.Services, new RegistryShellSettings(new TenancyConnections()), runtimeRoutes: true);
else builder.Services.AddOrchardCore();
var app = builder.Build();
if (identityEnabled) IdentityRuntimeHost.Use(app);
else
{
    app.Use(RuntimeBoundary.EnterAsync);
    if (TenancyConnections.RuntimeConfigured) app.UseOrchardCore();
}
app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
app.Run();
public partial class Program { }
