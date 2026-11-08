using OrchardCore.Data.Migration;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Models;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Workspace.Web.Orchard;

public static class NativeServices
{
    public static void Add(IServiceCollection services, IShellSettingsManager settings, bool runtimeRoutes = false)
    {
        services.AddSingleton(new TenancyConnections());
        services.AddScoped(provider => TenancyConnections.PlatformContext(provider.GetRequiredService<TenancyConnections>().Platform("runtime")));
        var orchard = services.AddOrchardCore().AddMvc().AddIdGeneration().AddDataAccess().AddDataStorage();
        services.AddSingleton(settings);
        orchard.ConfigureServices(tenantServices =>
        {
            tenantServices.AddScoped<IDataMigration, FoundationMigration>();
            tenantServices.AddScoped(provider =>
            {
                var selected = provider.GetRequiredService<ShellSettings>();
                if (selected.Name == "Default" || !Guid.TryParse(selected["SaaS:TenantId"], out var id) || id == Guid.Empty)
                    throw new TenantContractException("customer_shell_required");
                return new TenantContext(id, selected.Name, selected.RequestUrlPrefix);
            });
            tenantServices.AddScoped(provider =>
            {
                _ = provider.GetRequiredService<TenantContext>();
                return TenancyConnections.CustomerContext(provider.GetRequiredService<ShellSettings>()["ConnectionString"]);
            });
        });
        if (runtimeRoutes) orchard.Configure((app, routes, provider) => app.Run(RuntimeBoundary.RespondFromShellAsync));
    }
    public static ShellSettings Settings(string name, string prefix, Guid id, string connection)
    {
        var settings = new ShellSettings { Name = name, RequestUrlPrefix = prefix, State = TenantState.Running, VersionId = id.ToString("N") };
        settings["DatabaseProvider"] = "SqlConnection";
        settings["ConnectionString"] = connection;
        settings["TablePrefix"] = "oc";
        settings["Schema"] = "dbo";
        settings["SaaS:TenantId"] = id.ToString("D");
        return settings;
    }
}
