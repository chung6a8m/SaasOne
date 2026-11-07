using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OrchardCore.Data.Migration;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Builders;
using OrchardCore.Environment.Shell.Descriptor;
using OrchardCore.Environment.Shell.Descriptor.Models;
using OrchardCore.OpenId;
using OrchardCore.OpenId.YesSql.Models;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;

namespace SaaS.UnitTests;

// Real Orchard Core/YesSql stores in the fixed platform DB and independent customer
// DBs. A separate test-only table prefix leaves the M1 profile/history untouched.
internal sealed class NativeIssuerFixture : IAsyncDisposable
{
    private readonly M1SqlEnvironment _environment;
    private readonly string _prefix = "m2poc" + Guid.NewGuid().ToString("N")[..10];
    private WebApplication _application = null!;
    public IShellHost Shells => _application.Services.GetRequiredService<IShellHost>();
    public DefaultIssuerStoreProbe Probe => new(Shells);
    private NativeIssuerFixture(M1SqlEnvironment environment) => _environment = environment;

    public static async Task<NativeIssuerFixture> CreateAsync(M1SqlEnvironment environment)
    {
        var fixture = new NativeIssuerFixture(environment);
        var settings = fixture.Settings(maintenance: true);
        await using (var maintenance = fixture.Build(settings))
        {
            var factory = maintenance.Services.GetRequiredService<IShellContextFactory>();
            foreach (var selected in settings)
            {
                var features = FoundationProfile.Features.Append(OpenIdConstants.Features.Core).Distinct(StringComparer.Ordinal).ToArray();
                var descriptor = new ShellDescriptor { Features = features.Select(id => new ShellFeature(id)).ToList() };
                await using var context = await factory.CreateDescribedContextAsync(selected, descriptor);
                var scope = await context.CreateScopeAsync();
                await scope.UsingServiceScopeAsync(async current =>
                {
                    await current.ServiceProvider.GetRequiredService<IShellDescriptorManager>()
                        .UpdateShellDescriptorAsync(0, descriptor.Features);
                    await current.ServiceProvider.GetRequiredService<IDataMigrationManager>().UpdateAllFeaturesAsync();
                });
            }
        }
        var runtime = fixture.Settings(maintenance: false);
        await fixture.GrantRuntimeAccessAsync(runtime);
        fixture._application = fixture.Build(runtime);
        await fixture.Shells.InitializeAsync();
        return fixture;
    }

    private async Task GrantRuntimeAccessAsync(ShellSettings[] runtime)
    {
        foreach (var selected in runtime)
        {
            var admin = selected.Name == "Default" ? _environment.PlatformAdmin :
                _environment.CustomerAdmin(_environment.Tenants.Single(tenant => tenant.Slug == selected.Name));
            var principal = new SqlConnectionStringBuilder(selected["ConnectionString"]).UserID;
            await using var connection = await SqlOperations.OpenAsync(admin, CancellationToken.None);
            // The M1 runtime grant is an explicit table allowlist. New native PoC tables
            // need their own DML grants; never grant schema-wide DML, DDL or db_owner.
            await SqlOperations.ExecuteAsync(connection, """
                DECLARE @sql nvarchar(max);
                SELECT @sql = STRING_AGG(CONVERT(nvarchar(max),
                    N'GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' TO ' + QUOTENAME(@principal) + N';'), CHAR(10))
                FROM sys.tables WHERE SCHEMA_NAME(schema_id) = N'dbo' AND LEFT(name, LEN(@prefix)) = @prefix;
                IF @sql IS NULL THROW 51000, 'Native PoC tables were not created', 1;
                EXEC sys.sp_executesql @sql;
                """, CancellationToken.None, ("@principal", principal), ("@prefix", _prefix + "_"));
        }
    }

    private ShellSettings[] Settings(bool maintenance)
    {
        var connections = new TenancyConnections();
        var settings = new List<ShellSettings>
        {
            NativeServices.Settings("Default", "", _environment.InstallationId,
                connections.Platform(maintenance ? "migrator" : "runtime"))
        };
        settings.AddRange(_environment.Tenants.Take(2).Select(tenant => NativeServices.Settings(
            tenant.Slug, tenant.Slug, tenant.Id, maintenance ? _environment.Migrator(tenant) : _environment.Runtime(tenant))));
        foreach (var item in settings) item["TablePrefix"] = _prefix;
        return settings.ToArray();
    }

    private WebApplication Build(ShellSettings[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        NativeServices.Add(builder.Services, new PocShellSettings(settings));
        builder.Services.AddOrchardCore().ConfigureServices(services =>
            services.Configure<OpenIddictCoreOptions>(options => options.DisableEntityCaching = true));
        return builder.Build(); // No HTTP listener, public setup or runtime migration entrypoint.
    }

    public async Task InShellAsync(string name, Func<IServiceProvider, Task> action)
    {
        var scope = await Shells.GetScopeAsync(name);
        await scope.UsingServiceScopeAsync(current => action(current.ServiceProvider));
    }

    public async Task<IssuerEntry> SeedAsync(string shell = "Default", string type = OpenIddictConstants.TokenTypeIdentifiers.AccessToken)
    {
        IssuerEntry? result = null;
        await InShellAsync(shell, async provider =>
        {
            var subject = Guid.NewGuid().ToString("D");
            var authorizations = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
            {
                Subject = subject, Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.AuthorizationTypes.Permanent,
                CreationDate = DateTimeOffset.UtcNow
            });
            var authorizationId = (await authorizations.GetIdAsync(authorization!))!;
            var tokens = provider.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                AuthorizationId = authorizationId, Subject = subject,
                Status = OpenIddictConstants.Statuses.Valid, Type = type,
                CreationDate = DateTimeOffset.UtcNow, ExpirationDate = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
            result = new IssuerEntry((await tokens.GetIdAsync(token!))!, authorizationId, subject);
        });
        return result!;
    }

    public async Task RevokeAsync(IssuerEntry entry, bool authorization)
    {
        await InShellAsync("Default", async provider =>
        {
            if (authorization)
            {
                var manager = provider.GetRequiredService<IOpenIddictAuthorizationManager>();
                var value = await manager.FindByIdAsync(entry.AuthorizationId);
                if (value is null || !await manager.TryRevokeAsync(value)) throw new InvalidOperationException("Cannot revoke native authorization.");
            }
            else
            {
                var manager = provider.GetRequiredService<IOpenIddictTokenManager>();
                var value = await manager.FindByIdAsync(entry.TokenId);
                if (value is null || !await manager.TryRevokeAsync(value)) throw new InvalidOperationException("Cannot revoke native token.");
            }
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        });
    }

    public async Task<IssuerEntryDecision> ValidateFromAsync(string customer, IssuerEntry entry)
    {
        var decision = IssuerEntryDecision.Unavailable;
        await InShellAsync(customer, async _ => decision = await Probe.ValidateAsync(entry, CancellationToken.None));
        return decision;
    }

    public async Task SetDefaultReadFailureAsync(bool fail)
    {
        await using var connection = await SqlOperations.OpenAsync(_environment.PlatformAdmin, CancellationToken.None);
        // Target the native OpenId collection, NOT the unrelated default document table.
        // These identifiers are locally generated/fixed, never request or token values.
        await SqlOperations.ExecuteAsync(connection,
            $"{(fail ? "DENY" : "GRANT")} SELECT ON OBJECT::[dbo].[{_prefix}_{OpenIdToken.OpenIdCollection}_Document] TO [saas_platform_runtime]", CancellationToken.None);
    }

    public async Task RestartAsync()
    {
        await _application.DisposeAsync();
        _application = Build(Settings(maintenance: false));
        await Shells.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _application.DisposeAsync();

    private sealed class PocShellSettings(ShellSettings[] settings) : IShellSettingsManager
    {
        public ShellSettings CreateDefaultSettings() => settings.Single(item => item.Name == "Default");
        public Task<IEnumerable<ShellSettings>> LoadSettingsAsync() => Task.FromResult<IEnumerable<ShellSettings>>(settings);
        public Task<IEnumerable<string>> LoadSettingsNamesAsync() => Task.FromResult(settings.Select(item => item.Name));
        public Task<ShellSettings> LoadSettingsAsync(string tenant) => Task.FromResult(settings.Single(item => item.Name == tenant));
        public Task SaveSettingsAsync(ShellSettings value) => throw new InvalidOperationException("Test settings are immutable.");
        public Task RemoveSettingsAsync(ShellSettings value) => throw new InvalidOperationException("Test settings are immutable.");
    }
}
