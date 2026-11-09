using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OrchardCore.Data.Migration;
using OrchardCore.Data.Migration.Records;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Builders;
using OrchardCore.Environment.Shell.Descriptor;
using OrchardCore.Environment.Shell.Descriptor.Models;
using OrchardCore.Users;
using OrchardCore.Users.Models;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;

namespace SaaS.Web.Orchard;

/// <summary>Private maintenance only. No HTTP entrypoint and no migration at runtime startup.</summary>
public sealed class IdentityInstaller(TenancyConnections connections, IdentityDeployment deployment)
{
    public static string[] Features => FoundationProfile.Features.Concat(new[]
    {
        "OrchardCore.Settings", "OrchardCore.Roles.Core", "OrchardCore.Users",
        "OrchardCore.OpenId", "OrchardCore.OpenId.Management", "OrchardCore.OpenId.Server"
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static string FeatureHash => Hash(string.Join('\n', Features));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public async Task InitializeAsync(Guid installationId, CancellationToken ct)
    {
        var actor = await new SqlProvisioner(connections.Provisioner()).AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "platform-identity", ct);
        await RequireInstallationAsync(installationId, ct);
        // A completed installation is a verification boundary, not permission to
        // migrate, repair native history, reset clients, resolve secrets or re-grant DML.
        await using (var sql = await SqlOperations.OpenAsync(connections.Platform("migrator"), ct))
        {
            var exists = await SqlOperations.ScalarAsync(sql,
                "SELECT CASE WHEN OBJECT_ID(N'[identity].[Installations]', N'U') IS NULL THEN 0 ELSE 1 END", ct);
            if (Convert.ToInt32(exists, System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                await using var read = TenancyConnections.PlatformContext(connections.Platform("migrator"));
                if (await read.IdentityInstallations.AsNoTracking().AnyAsync(x => x.NativePrefix == deployment.NativePrefix, ct))
                {
                    await VerifyAsync(connections, deployment, ct);
                    return;
                }
            }
        }
        await using (var db = TenancyConnections.PlatformContext(connections.Platform("migrator")))
            await db.Database.MigrateAsync(ct);
        string migrationHash = "";
        await InMaintenanceAsync(installationId, async provider =>
        {
            var descriptors = provider.GetRequiredService<IShellDescriptorManager>();
            var descriptor = await descriptors.GetShellDescriptorAsync();
            if (descriptor is null) await descriptors.UpdateShellDescriptorAsync(0, Features.Select(x => new ShellFeature(x)));
            else if (!descriptor.Features.Select(x => x.Id).Order(StringComparer.Ordinal).SequenceEqual(Features, StringComparer.Ordinal))
                throw new TenantContractException("identity_profile_mismatch");
            await provider.GetRequiredService<IDataMigrationManager>().UpdateAllFeaturesAsync();
            await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
            migrationHash = await ReadMigrationHashAsync(connections.Platform("migrator"), deployment.NativePrefix, ct);
            await RegisterClientsAsync(provider, null, ct);
        }, ct);
        await lease.AssertHeldAsync(ct);
        await GrantRuntimeAsync(ct);
        await using (var db = TenancyConnections.PlatformContext(connections.Platform("migrator")))
        {
            db.IdentityInstallations.Add(new IdentityInstallation
            {
                NativePrefix = deployment.NativePrefix, Issuer = deployment.Issuer.AbsoluteUri, FeatureHash = FeatureHash,
                MigrationHash = migrationHash, Version = 1, CompletedAt = DateTimeOffset.UtcNow
            });
            db.Audit.Add(new OperationalAudit { Actor = actor, OperationId = Guid.NewGuid(), Action = "IdentityInitialized", OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        await VerifyAsync(connections, deployment, ct);
    }

    public async Task<Guid> EnrollUserAsync(Guid installationId, string username, string email, string credential, IReadOnlyCollection<Guid> tenants, CancellationToken ct)
    {
        _ = await new SqlProvisioner(connections.Provisioner()).AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "platform-identity", ct);
        await RequireInstallationAsync(installationId, ct);
        await VerifyAsync(connections, deployment, ct);
        if (tenants.Count == 0 || tenants.Any(x => x == Guid.Empty) || tenants.Distinct().Count() != tenants.Count)
            throw new TenantContractException("invalid_identity_memberships");
        await using (var db = TenancyConnections.PlatformContext(connections.Platform("migrator")))
        {
            foreach (var tenantId in tenants)
            {
                var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                    ?? throw new TenantContractException("tenant_not_found");
                TenantDatabaseVerifier.ValidateRegistration(tenant);
                if (!await db.IdentityClients.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Enabled, ct))
                    throw new TenantContractException("identity_client_not_registered");
            }
        }
        string subject = "";
        await InMaintenanceAsync(installationId, async provider =>
        {
            var manager = provider.GetRequiredService<UserManager<IUser>>();
            var user = await manager.FindByNameAsync(username);
            if (user is null)
            {
                user = new User { UserId = Guid.NewGuid().ToString("N"), UserName = username, Email = email, EmailConfirmed = true, IsEnabled = true, IsLockoutEnabled = true };
                var result = await manager.CreateAsync(user, credential);
                if (!result.Succeeded) throw new TenantContractException("identity_user_creation_failed");
                await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
            }
            else if (!await manager.CheckPasswordAsync(user, credential))
                throw new TenantContractException("identity_existing_user_requires_explicit_rotation");
            subject = await manager.GetUserIdAsync(user);
        }, ct);
        await lease.AssertHeldAsync(ct);
        var operations = new IdentityOperations(connections);
        var globalId = await operations.EnrollSubjectAsync(deployment.Issuer.AbsoluteUri, subject, ct);
        foreach (var tenant in tenants) await operations.SetMembershipAsync(globalId, tenant, true, ct);
        return globalId;
    }

    public async Task RegisterTenantAsync(Guid installationId, Guid tenantId, CancellationToken ct)
    {
        var actor = await new SqlProvisioner(connections.Provisioner()).AuthenticateAsync(ct);
        await using var lease = await OperationLease.AcquireAsync(connections.Platform("operator"), "platform-identity", ct);
        await RequireInstallationAsync(installationId, ct);
        await VerifyAsync(connections, deployment, ct);
        if (tenantId == Guid.Empty) throw new TenantContractException("tenant_not_found");
        await InMaintenanceAsync(installationId, provider => RegisterClientsAsync(provider, tenantId, ct), ct);
        await lease.AssertHeldAsync(ct);
        await using var db = TenancyConnections.PlatformContext(connections.Platform("operator"));
        db.Audit.Add(new OperationalAudit { Actor = actor, OperationId = Guid.NewGuid(), Action = "IdentityClientRegistered", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    private async Task RequireInstallationAsync(Guid installationId, CancellationToken ct)
    {
        await using var db = TenancyConnections.PlatformContext(connections.Platform("migrator"));
        var bootstrap = await db.Bootstrap.AsNoTracking().SingleAsync(ct);
        if (installationId == Guid.Empty || !bootstrap.Completed || bootstrap.BaselineVersion != 1 || bootstrap.InstallationId != installationId)
            throw new TenantContractException("platform_not_ready");
    }

    private async Task RegisterClientsAsync(IServiceProvider provider, Guid? selectedTenant, CancellationToken ct)
    {
        await using var db = TenancyConnections.PlatformContext(connections.Platform("migrator"));
        var applications = provider.GetRequiredService<IOpenIddictApplicationManager>();
        var query = db.Tenants.AsNoTracking().Where(x => x.RetiredAt == null && x.PurgedAt == null
            && x.Subscription != SubscriptionStatus.PendingDeletion);
        if (selectedTenant.HasValue) query = query.Where(x => x.Id == selectedTenant.Value);
        var tenants = await query.ToArrayAsync(ct);
        if (selectedTenant.HasValue && tenants.Length != 1) throw new TenantContractException("tenant_not_found");
        foreach (var tenant in tenants)
        {
            TenantDatabaseVerifier.ValidateRegistration(tenant);
            var client = new IdentityClient
            {
                ClientId = IdentityDeployment.ClientId(tenant.Id), TenantId = tenant.Id, ShellName = tenant.Slug,
                RedirectUri = deployment.Callback(tenant.Slug).AbsoluteUri, LogoutRedirectUri = deployment.LogoutCallback(tenant.Slug).AbsoluteUri
            };
            deployment.ValidateClient(client);
            var existing = await applications.FindByClientIdAsync(client.ClientId, ct);
            if (existing is null)
            {
                var descriptor = new OpenIddictApplicationDescriptor
                {
                    ClientId = client.ClientId, ClientSecret = deployment.ClientSecret(tenant.Id),
                    ClientType = OpenIddictConstants.ClientTypes.Confidential, ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                    DisplayName = "SaaS " + tenant.Slug
                };
                descriptor.RedirectUris.Add(new Uri(client.RedirectUri));
                descriptor.PostLogoutRedirectUris.Add(new Uri(client.LogoutRedirectUri));
                descriptor.Permissions.UnionWith(new[]
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization, OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.EndSession, OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken, OpenIddictConstants.Permissions.ResponseTypes.Code,
                    OpenIddictConstants.Permissions.Prefixes.Scope + IdentityNames.Scope
                });
                descriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
                await applications.CreateAsync(descriptor, ct);
            }
            else if (!await applications.ValidateClientSecretAsync(existing, deployment.ClientSecret(tenant.Id), ct)
                || !(await applications.GetRedirectUrisAsync(existing, ct)).SequenceEqual(new[] { client.RedirectUri }, StringComparer.Ordinal)
                || !(await applications.GetPostLogoutRedirectUrisAsync(existing, ct)).SequenceEqual(new[] { client.LogoutRedirectUri }, StringComparer.Ordinal))
                throw new TenantContractException("identity_client_reconciliation_required");
            var registered = await db.IdentityClients.SingleOrDefaultAsync(x => x.ClientId == client.ClientId, ct);
            if (registered is null) db.IdentityClients.Add(client);
            else if (registered.TenantId != client.TenantId || registered.ShellName != client.ShellName || registered.RedirectUri != client.RedirectUri
                || registered.LogoutRedirectUri != client.LogoutRedirectUri || !registered.Enabled)
                throw new TenantContractException("identity_client_reconciliation_required");
            if (!await db.IdentityTenantSecurity.AnyAsync(x => x.TenantId == tenant.Id, ct))
                db.IdentityTenantSecurity.Add(new IdentityTenantSecurity { TenantId = tenant.Id });
        }
        await provider.GetRequiredService<YesSql.ISession>().SaveChangesAsync();
        // Native creation is not platform authority. A lifecycle decision may have changed
        // during that separate store operation. Recheck in the short platform transaction
        // so deletion either precedes publication (reject) or follows it (revokes the client).
        // A native-only orphan cannot authorize without this independent enabled registry.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var selected in tenants)
        {
            var current = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == selected.Id, ct);
            if (current.RetiredAt is not null || current.PurgedAt is not null || current.Subscription == SubscriptionStatus.PendingDeletion)
                throw new TenantContractException("tenant_retired");
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task InMaintenanceAsync(Guid installationId, Func<IServiceProvider, Task> action, CancellationToken ct)
    {
        var settings = NativeServices.Settings("Default", "", installationId, connections.Platform("migrator"));
        settings["TablePrefix"] = deployment.NativePrefix;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Program).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        NativeServices.Add(builder.Services, new SingleShellSettings(settings));
        builder.Services.AddSaasIdentity(deployment);
        await using var application = builder.Build();
        var factory = application.Services.GetRequiredService<IShellContextFactory>();
        var descriptor = new ShellDescriptor { Features = Features.Select(x => new ShellFeature(x)).ToList() };
        await using var context = await factory.CreateDescribedContextAsync(settings, descriptor);
        var scope = await context.CreateScopeAsync();
        await scope.UsingServiceScopeAsync(current => action(current.ServiceProvider));
        ct.ThrowIfCancellationRequested();
    }

    private async Task GrantRuntimeAsync(CancellationToken ct)
    {
        var runtime = new SqlConnectionStringBuilder(connections.Platform("runtime"));
        await using var connection = await SqlOperations.OpenAsync(connections.Platform("migrator"), ct);
        await SqlOperations.ExecuteAsync(connection, """
            DECLARE @principal nvarchar(258) = QUOTENAME(@login);
            DECLARE @sql nvarchar(max) = N'REVOKE INSERT, UPDATE, DELETE ON SCHEMA::[platform] FROM ' + @principal + N';';
            SELECT @sql = @sql + N'DENY INSERT, UPDATE, DELETE ON OBJECT::[platform].' + QUOTENAME(name) + N' TO ' + @principal + N';'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('platform') AND name <> 'OperationalAudit';
            SET @sql = @sql + N'GRANT INSERT ON OBJECT::[platform].[OperationalAudit] TO ' + @principal + N';DENY UPDATE, DELETE ON OBJECT::[platform].[OperationalAudit] TO ' + @principal + N';';
            SELECT @sql = @sql + N'GRANT SELECT ON OBJECT::[identity].' + QUOTENAME(name) + N' TO ' + @principal + N';'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('identity');
            SELECT @sql = @sql + N'GRANT INSERT, UPDATE ON OBJECT::[identity].' + QUOTENAME(name) + N' TO ' + @principal + N';'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('identity') AND name IN ('Sessions','Families','RefreshUses');
            SET @sql = @sql + N'GRANT UPDATE ([Generation]) ON OBJECT::[identity].[Accounts] TO ' + @principal + N';';
            SELECT @sql = @sql + N'GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].' + QUOTENAME(name) + N' TO ' + @principal + N';'
                FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') AND LEFT(name, LEN(@prefix)) = @prefix;
            EXEC sys.sp_executesql @sql;
            GRANT SELECT, INSERT, UPDATE ON SCHEMA::[identity] TO [saas_operator];
            """, ct, ("@login", runtime.UserID), ("@prefix", deployment.NativePrefix + "_"));
        await SqlOperations.RequireRuntimeAsync(connections.Platform("runtime"), ct);
    }

    public static async Task VerifyAsync(TenancyConnections connections, IdentityDeployment deployment, CancellationToken ct)
    {
        await using var sql = await SqlOperations.OpenAsync(connections.Platform("runtime"), ct);
        await using var db = TenancyConnections.PlatformContext(sql);
        await VerifyAsync(db, deployment, ct);
    }

    internal static async Task VerifyAsync(PlatformDbContext db, IdentityDeployment deployment, CancellationToken ct)
    {
        await TenancyConnections.VerifySchemaAsync(db, ct);
        var installation = await db.IdentityInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.NativePrefix == deployment.NativePrefix, ct);
        if (installation is null || installation.Version != 1 || installation.Issuer != deployment.Issuer.AbsoluteUri || installation.FeatureHash != FeatureHash
            || installation.MigrationHash != await ReadMigrationHashAsync((SqlConnection)db.Database.GetDbConnection(), deployment.NativePrefix, ct))
            throw new TenantContractException("identity_not_ready");
    }

    private static async Task<string> ReadMigrationHashAsync(string connectionString, string prefix, CancellationToken ct)
    {
        await using var connection = await SqlOperations.OpenAsync(connectionString, ct);
        return await ReadMigrationHashAsync(connection, prefix, ct);
    }

    private static async Task<string> ReadMigrationHashAsync(SqlConnection connection, string prefix, CancellationToken ct)
    {
        await using var command = SqlOperations.Command(connection, $"SELECT [Type], [Content] FROM [dbo].[{prefix}_Document] WHERE [Type] LIKE N'%ShellDescriptor%' OR [Type] LIKE N'%DataMigrationRecord%'");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var descriptors = 0;
        var migrations = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(0);
            using var document = JsonDocument.Parse(reader.GetString(1));
            if (type == typeof(ShellDescriptor).FullName || type.StartsWith(typeof(ShellDescriptor).FullName + ",", StringComparison.Ordinal))
            {
                descriptors++;
                var actual = Property(document.RootElement, "Features").EnumerateArray().Select(x => Property(x, "Id").GetString()!).Order(StringComparer.Ordinal);
                if (!actual.SequenceEqual(Features, StringComparer.Ordinal)) throw new TenantContractException("identity_profile_mismatch");
            }
            else if (type == typeof(DataMigrationRecord).FullName || type.StartsWith(typeof(DataMigrationRecord).FullName + ",", StringComparison.Ordinal))
            {
                foreach (var migration in Property(document.RootElement, "DataMigrations").EnumerateArray())
                    migrations.Add(Property(migration, "DataMigrationClass").GetString() + ":" + Property(migration, "Version").GetInt32());
            }
        }
        if (descriptors != 1 || migrations.Count < 2) throw new TenantContractException("identity_native_history_missing");
        return Hash(string.Join('\n', migrations.Order(StringComparer.Ordinal)));
    }
    private static JsonElement Property(JsonElement element, string name) => element.EnumerateObject()
        .Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
