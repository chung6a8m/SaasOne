using OrchardCore.Data.Migration;
using OrchardCore.Environment.Shell.Builders;
using OrchardCore.Environment.Shell.Descriptor;
using OrchardCore.Environment.Shell.Descriptor.Models;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Orchard;

public sealed class OrchardMaintenance : IOrchardMaintenance
{
    public async Task<OrchardBaseline> InitializeAsync(string shell, string prefix, Guid tenantId, string connection, CancellationToken cancellationToken)
    {
        await InShellAsync(shell, prefix, tenantId, connection, async provider =>
        {
            var manager = provider.GetRequiredService<IShellDescriptorManager>();
            var descriptor = await manager.GetShellDescriptorAsync();
            if (descriptor is null)
                await manager.UpdateShellDescriptorAsync(0, FoundationProfile.Features.Select(id => new ShellFeature(id)));
            else FoundationProfile.VerifyFeatures(descriptor.Features.Select(feature => feature.Id));
            await provider.GetRequiredService<IDataMigrationManager>().UpdateAllFeaturesAsync();
        }, cancellationToken);
        return await FoundationProfile.ReadSnapshotAsync(connection, cancellationToken);
    }

    public async Task<OrchardBaseline> VerifyAsync(string shell, string prefix, Guid tenantId, string connection, CancellationToken cancellationToken)
    {
        // Inspect persisted state before building a runtime shell: missing/changed data never triggers repair.
        var result = await FoundationProfile.ReadSnapshotAsync(connection, cancellationToken);
        await InShellAsync(shell, prefix, tenantId, connection, async provider =>
        {
            var descriptor = await provider.GetRequiredService<IShellDescriptorManager>().GetShellDescriptorAsync()
                ?? throw new TenantContractException("orchard_descriptor_missing");
            FoundationProfile.VerifyFeatures(descriptor.Features.Select(feature => feature.Id));
            if ((await provider.GetRequiredService<IDataMigrationManager>().GetFeaturesThatNeedUpdateAsync()).Any())
                throw new TenantContractException("orchard_migration_pending");
        }, cancellationToken);
        return result;
    }

    private static async Task InShellAsync(string name, string prefix, Guid id, string connection, Func<IServiceProvider, Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = NativeServices.Settings(name, prefix, id, connection);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders(); // Never emit provider exceptions or connection material from maintenance.
        NativeServices.Add(builder.Services, new SingleShellSettings(settings));
        await using var application = builder.Build(); // Deliberately never starts an HTTP listener.
        var factory = application.Services.GetRequiredService<IShellContextFactory>();
        var descriptor = new ShellDescriptor { Features = FoundationProfile.Features.Select(feature => new ShellFeature(feature)).ToList() };
        await using var context = await factory.CreateDescribedContextAsync(settings, descriptor);
        var scope = await context.CreateScopeAsync();
        // Do not detach native work on cancellation: the process/SQL fence remains held until disposal completes.
        await scope.UsingServiceScopeAsync(async current => await action(current.ServiceProvider));
        cancellationToken.ThrowIfCancellationRequested();
    }
}
