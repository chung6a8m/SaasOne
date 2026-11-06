using Microsoft.EntityFrameworkCore;
using OrchardCore.Environment.Shell;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Orchard;

public sealed class RegistryShellSettings(TenancyConnections connections) : IShellSettingsManager
{
    public ShellSettings CreateDefaultSettings() => NativeServices.Settings("Default", string.Empty, Guid.Empty, connections.Platform("runtime"));
    public async Task<IEnumerable<ShellSettings>> LoadSettingsAsync()
    {
        await using var database = TenancyConnections.PlatformContext(connections.Platform("runtime"));
        var bootstrap = await database.Bootstrap.AsNoTracking().SingleAsync();
        if (!bootstrap.Completed || bootstrap.InstallationId == Guid.Empty || bootstrap.BaselineVersion != 1)
            throw new TenantContractException("platform_not_ready");
        var settings = new List<ShellSettings> { NativeServices.Settings("Default", string.Empty, bootstrap.InstallationId, connections.Platform("runtime")) };
        foreach (var registration in await database.Tenants.AsNoTracking().ToArrayAsync())
        {
            TenantDatabaseVerifier.ValidateRegistration(registration);
            // Lifecycle readiness remains authoritative per request; provisioning rows are never routed.
            if (registration.Operation != OperationStatus.None) continue;
            settings.Add(NativeServices.Settings(registration.Slug, registration.Slug, registration.Id, connections.Customer(registration.Id, "runtime")));
        }
        return settings;
    }
    public async Task<IEnumerable<string>> LoadSettingsNamesAsync() => (await LoadSettingsAsync()).Select(settings => settings.Name).ToArray();
    public async Task<ShellSettings> LoadSettingsAsync(string tenant) => (await LoadSettingsAsync()).SingleOrDefault(settings => settings.Name == tenant)
        ?? throw new TenantContractException("unknown_shell");
    public Task SaveSettingsAsync(ShellSettings settings) => throw new TenantContractException("private_command_required");
    public Task RemoveSettingsAsync(ShellSettings settings) => throw new TenantContractException("private_command_required");
}

internal sealed class SingleShellSettings(ShellSettings settings) : IShellSettingsManager
{
    public ShellSettings CreateDefaultSettings() => settings;
    public Task<IEnumerable<ShellSettings>> LoadSettingsAsync() => Task.FromResult<IEnumerable<ShellSettings>>([settings]);
    public Task<IEnumerable<string>> LoadSettingsNamesAsync() => Task.FromResult<IEnumerable<string>>([settings.Name]);
    public Task<ShellSettings> LoadSettingsAsync(string tenant) => tenant == settings.Name ? Task.FromResult(settings) : throw new TenantContractException("unknown_shell");
    public Task SaveSettingsAsync(ShellSettings value) => throw new TenantContractException("private_command_required");
    public Task RemoveSettingsAsync(ShellSettings value) => throw new TenantContractException("private_command_required");
}
