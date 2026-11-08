using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Workspace.Web.Orchard;
using SaaS.Workspace.Web.Release;

if (args is ["--feature-graph"])
{
    await NativeFeatureMetadata.WriteAsync();
    return;
}
if (args.Length > 0 && args[0] == "--maintenance")
{
    try
    {
        var service = new TenancyProvisioning(new TenancyConnections(), new OrchardMaintenance());
        var command = args[1..];
        if (command.Length == 2 && command[0] == "bootstrap" && Guid.TryParseExact(command[1], "D", out var installation))
            await service.BootstrapAsync(installation, CancellationToken.None);
        else if (command.Length > 0 && command[0] == "provision")
            await service.ProvisionAsync(ProvisionRequest.Parse(command), CancellationToken.None);
        else throw new TenantContractException("invalid_maintenance_command");
        Console.WriteLine("maintenance_completed");
    }
    catch (TenantContractException error) { Console.Error.WriteLine(error.Code); Environment.ExitCode = 1; }
    catch (Exception error) { Console.Error.WriteLine("maintenance_failed:" + error.GetType().Name); Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
if (TenancyConnections.RuntimeConfigured)
    NativeServices.Add(builder.Services, new RegistryShellSettings(new TenancyConnections()), runtimeRoutes: true);
else builder.Services.AddOrchardCore();
await using var app = builder.Build();
if (TenancyConnections.RuntimeConfigured) app.Use(PlatformReleaseRuntimeGate.EnterAsync);
app.Use(RuntimeBoundary.EnterAsync);
app.UseOrchardCore();
app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
await app.RunAsync();
public partial class Program;
