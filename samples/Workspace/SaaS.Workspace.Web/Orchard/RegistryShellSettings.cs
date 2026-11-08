using Microsoft.EntityFrameworkCore;
using OrchardCore.Environment.Shell;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Domain;
using SaaS.Tenancy.Operations;
namespace SaaS.Workspace.Web.Orchard;

public sealed class RegistryShellSettings(TenancyConnections connections) : IShellSettingsManager
{
    public ShellSettings CreateDefaultSettings() => NativeServices.Settings("Default", "", Guid.Empty, connections.Platform("runtime"));
    public async Task<IEnumerable<ShellSettings>> LoadSettingsAsync()
    {
        await using var database = TenancyConnections.PlatformContext(connections.Platform("runtime"));
        var bootstrap = await RequirePlatformAsync(database);
        var settings = new List<ShellSettings> { DefaultSettings(bootstrap) };
        foreach (var registration in await database.Tenants.AsNoTracking().ToArrayAsync())
        {
            try { if (CanLoad(registration)) settings.Add(CustomerSettings(registration)); }
            catch (TenantContractException) { /* Quarantine this customer; never substitute another database. */ }
        }
        return settings;
    }
    public async Task<IEnumerable<string>> LoadSettingsNamesAsync()
    {
        await using var database = TenancyConnections.PlatformContext(connections.Platform("runtime"));
        _ = await RequirePlatformAsync(database);
        var names = new List<string> { "Default" };
        foreach (var registration in await database.Tenants.AsNoTracking().ToArrayAsync())
        {
            try { if (CanLoad(registration)) names.Add(registration.Slug); }
            catch (TenantContractException) { }
        }
        return names;
    }
    public async Task<ShellSettings> LoadSettingsAsync(string tenant)
    {
        await using var database = TenancyConnections.PlatformContext(connections.Platform("runtime"));
        var bootstrap = await RequirePlatformAsync(database);
        if (tenant == "Default") return DefaultSettings(bootstrap);
        var registration = await database.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == tenant);
        if (registration is null || !CanLoad(registration)) throw new TenantContractException("unknown_shell");
        return CustomerSettings(registration);
    }
    private ShellSettings DefaultSettings(PlatformBootstrap bootstrap) => NativeServices.Settings("Default", "", bootstrap.InstallationId, connections.Platform("runtime"));
    private ShellSettings CustomerSettings(TenantRegistration registration) => NativeServices.Settings(registration.Slug, registration.Slug, registration.Id, connections.Customer(registration.Id, "runtime"));
    private static bool CanLoad(TenantRegistration registration)
    {
        if (!TenantPolicy.IsOperational(registration.Status(), DateTimeOffset.UtcNow)) return false;
        TenantDatabaseVerifier.ValidateRegistration(registration);
        return true;
    }
    private static async Task<PlatformBootstrap> RequirePlatformAsync(PlatformDbContext database)
    {
        var bootstrap = await database.Bootstrap.AsNoTracking().SingleAsync();
        if (!bootstrap.Completed || bootstrap.InstallationId == Guid.Empty || bootstrap.BaselineVersion != 1)
            throw new TenantContractException("platform_not_ready");
        return bootstrap;
    }
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
