using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Maintenance;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

[CollectionDefinition("M1 SQL", DisableParallelization = true)]
public sealed class M1SqlCollection { }

[Collection("M1 SQL")]
[Trait("Category", "SqlM1")]
public sealed partial class M1SqlTests(M1SqlFixture fixture) : IClassFixture<M1SqlFixture>
{
    private M1SqlEnvironment Environment => fixture.Environment;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private TestTenant Alpha => Environment.Tenants[0];
    private TestTenant Beta => Environment.Tenants[1];

    [Fact]
    public async Task Four_real_customer_databases_are_provisioned_and_routed_through_Orchard()
    {
        foreach (var tenant in Environment.Tenants)
        {
            using var response = await fixture.Client.GetAsync($"/{tenant.Slug}/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(tenant.Id.ToString("D"), document.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal($"/{tenant.Slug}/", document.RootElement.GetProperty("home").GetString());
            Assert.Equal(tenant.Id, await Scalar(Environment.Runtime(tenant), "SELECT [TenantId] FROM [tenancy].[Identity] WHERE [Id] = 1"));
            Assert.Equal(4, await Scalar(Environment.PlatformAdmin, "SELECT [Checkpoint] FROM [platform].[Operations] WHERE [Id] = @id", ("@id", tenant.OperationId)));
            using var home = await fixture.Client.GetAsync($"/{tenant.Slug}/", Ct);
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.Contains($"href=\"/{tenant.Slug}/health/ready\"", await home.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Bootstrap_is_one_time_and_completed_retry_is_read_only()
    {
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["bootstrap", Environment.InstallationId.ToString("D")], Ct));
        var before = await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[OperationalAudit]");
        var version = await Scalar(Environment.PlatformAdmin, "SELECT [RegistryVersion] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", Alpha.Id));
        Assert.Equal(0, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(Alpha), Ct));
        Assert.Equal(before, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[OperationalAudit]"));
        Assert.Equal(version, await Scalar(Environment.PlatformAdmin, "SELECT [RegistryVersion] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", Alpha.Id)));
    }

    [Fact]
    public async Task Conflicting_idempotency_payload_and_duplicate_slug_are_rejected()
    {
        var changed = Environment.ProvisionArguments(Alpha);
        changed[4] = "Approval";
        Assert.Equal(1, await MaintenanceCommand.RunAsync(changed, Ct));
        Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(Alpha with { OperationId = Guid.NewGuid() }), Ct));
        var duplicate = Environment.NewTenant("alpha");
        Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(duplicate), Ct));
        Assert.Equal(0, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", duplicate.Id)));
        Assert.Equal(DBNull.Value, await Scalar(Environment.Admin, "SELECT DB_ID(@database)", ("@database", TenancyConnections.TenantDatabase(duplicate.Id))));
    }

    [Fact]
    public async Task Headers_and_query_parameters_cannot_select_another_database()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alpha/health/ready?tenantId=" + Beta.Id);
        request.Headers.Add("X-Tenant-Id", Beta.Id.ToString("D"));
        request.Headers.Add("X-Tenant", "beta");
        request.Headers.Add("X-Database", TenancyConnections.TenantDatabase(Beta.Id));
        using var response = await fixture.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Alpha.Id.ToString("D"), json.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/not-a-tenant/", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/Setup")]
    [InlineData("/admin")]
    [InlineData("/api/tenants")]
    [InlineData("/alpha/Setup")]
    [InlineData("/alpha/admin/Templates/Edit")]
    [InlineData("/alpha/api/features")]
    [InlineData("/alpha/media/upload")]
    public async Task Provisioned_host_has_no_anonymous_management_surface(string path)
    {
        using var get = await fixture.Client.GetAsync(path, Ct);
        using var post = await fixture.Client.PostAsync(path, new StringContent("{}"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Null(get.Headers.Location);
        Assert.Null(post.Headers.Location);
    }

    [Theory]
    [InlineData("CREATE TABLE [dbo].[RuntimeMustNotCreate] ([Id] int)", 262)]
    [InlineData("UPDATE [tenancy].[Identity] SET [TenantId] = NEWID()", 229)]
    [InlineData("DELETE FROM [tenancy].[Identity]", 229)]
    public async Task Customer_runtime_cannot_migrate_or_mutate_identity(string sql, int expectedNumber)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => Execute(Environment.Runtime(Alpha), sql));
        Assert.Equal(expectedNumber, error.Number);
        await AssertReady(Alpha);
    }

    [Fact]
    public async Task Customer_runtime_cannot_read_another_customer_or_platform_database()
    {
        foreach (var target in new[] { TenancyConnections.TenantDatabase(Beta.Id), TenancyConnections.PlatformDatabase })
        {
            var connection = new SqlConnectionStringBuilder(Environment.Runtime(Alpha)) { InitialCatalog = target };
            await Assert.ThrowsAsync<SqlException>(() => Scalar(connection.ConnectionString, "SELECT 1"));
        }
        var error = await Assert.ThrowsAsync<SqlException>(() => Execute(Environment.Runtime(Alpha), $"SELECT [TenantId] FROM [{TenancyConnections.TenantDatabase(Beta.Id)}].[tenancy].[Identity]"));
        Assert.Equal(916, error.Number);
    }

    [Fact]
    public async Task Immutable_identity_trigger_also_rejects_migrator_updates()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => Execute(Environment.Migrator(Alpha), "UPDATE [tenancy].[Identity] SET [TenantId] = NEWID()"));
        Assert.Equal(51000, error.Number);
        Assert.Equal(Alpha.Id, await Scalar(Environment.Runtime(Alpha), "SELECT [TenantId] FROM [tenancy].[Identity]"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Suspended_and_pending_deletion_tenants_stay_closed(int state)
    {
        await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Subscription] = @state, [RegistryVersion] = [RegistryVersion] + 1 WHERE [Id] = @id", ("@state", state), ("@id", Alpha.Id));
        try
        {
            await AssertClosed(Alpha);
            Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(Alpha), Ct));
            Assert.Equal(state, await Scalar(Environment.PlatformAdmin, "SELECT [Subscription] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", Alpha.Id)));
            await AssertReady(Beta);
        }
        finally { await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Subscription] = 1, [RegistryVersion] = [RegistryVersion] + 1 WHERE [Id] = @id", ("@id", Alpha.Id)); }
    }

    [Fact]
    public async Task Expiry_and_security_revalidation_block_readiness_without_harming_liveness()
    {
        var expiry = await Scalar(Environment.PlatformAdmin, "SELECT [ExpiresAt] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", Alpha.Id));
        try
        {
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [ExpiresAt] = DATEADD(day,-1,SYSUTCDATETIME()), [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@id", Alpha.Id));
            await AssertClosed(Alpha);
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [ExpiresAt] = @expiry, [SecurityRevalidationRequired] = 1, [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@expiry", expiry), ("@id", Alpha.Id));
            await AssertClosed(Alpha);
            Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/health/live", Ct)).StatusCode);
        }
        finally { await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [ExpiresAt] = @expiry, [SecurityRevalidationRequired] = 0, [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@expiry", expiry), ("@id", Alpha.Id)); }
    }

    [Theory]
    [InlineData("SeedVersion", "0")]
    [InlineData("Recipe", "wrong-recipe")]
    [InlineData("FeatureHash", "wrong-feature")]
    public async Task Seed_and_feature_baseline_faults_are_not_repaired_by_readiness_or_completed_retry(string column, string bad)
    {
        // Column is an in-source test fixture value, not request data.
        var original = await Scalar(Environment.CustomerAdmin(Alpha), $"SELECT [{column}] FROM [tenancy].[Baseline]");
        try
        {
            await Execute(Environment.CustomerAdmin(Alpha), $"UPDATE [tenancy].[Baseline] SET [{column}] = @bad", ("@bad", column == "SeedVersion" ? int.Parse(bad, CultureInfo.InvariantCulture) : bad));
            await AssertClosed(Alpha);
            Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(Alpha), Ct));
            Assert.Equal(bad, Convert.ToString(await Scalar(Environment.CustomerAdmin(Alpha), $"SELECT [{column}] FROM [tenancy].[Baseline]"), CultureInfo.InvariantCulture));
        }
        finally { await Execute(Environment.CustomerAdmin(Alpha), $"UPDATE [tenancy].[Baseline] SET [{column}] = @original", ("@original", original)); }
    }

    [Fact]
    public async Task Missing_EF_schema_history_keeps_the_gate_closed_without_running_migrations()
    {
        // Preserve the complete stream: later milestones add migrations to this shared fixture.
        await using var historyConnection = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        await using var historyCommand = SqlOperations.Command(historyConnection, "SELECT [MigrationId], [ProductVersion] FROM [tenancy].[__EFMigrationsHistory]");
        var history = new List<(string Id, string Version)>();
        await using (var reader = await historyCommand.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) history.Add((reader.GetString(0), reader.GetString(1)));
        Assert.NotEmpty(history);
        try
        {
            await Execute(Environment.CustomerAdmin(Alpha), "DELETE FROM [tenancy].[__EFMigrationsHistory]");
            await AssertClosed(Alpha);
            Assert.Equal(0, await Scalar(Environment.CustomerAdmin(Alpha), "SELECT COUNT(*) FROM [tenancy].[__EFMigrationsHistory]"));
        }
        finally
        {
            foreach (var row in history)
                await Execute(Environment.CustomerAdmin(Alpha), "INSERT INTO [tenancy].[__EFMigrationsHistory] ([MigrationId],[ProductVersion]) VALUES (@id,@version)", ("@id", row.Id), ("@version", row.Version));
        }
    }

    [Theory]
    [InlineData("ShellDescriptor", "SaaS.Theme.App", "Unreviewed.Feature")]
    [InlineData("DataMigrationRecord", "SaaS.Web.Orchard.FoundationMigration", "Unknown.Migration")]
    public async Task Native_Orchard_profile_or_migration_fault_is_not_automatically_repaired(string type, string originalValue, string fault)
    {
        var content = (string)(await Scalar(Environment.CustomerAdmin(Alpha), "SELECT [Content] FROM [dbo].[oc_Document] WHERE [Type] LIKE @type", ("@type", "%" + type + "%")))!;
        Assert.Contains(originalValue, content);
        try
        {
            await Execute(Environment.CustomerAdmin(Alpha), "UPDATE [dbo].[oc_Document] SET [Content] = @content WHERE [Type] LIKE @type", ("@content", content.Replace(originalValue, fault, StringComparison.Ordinal)), ("@type", "%" + type + "%"));
            await AssertClosed(Alpha);
            Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(Alpha), Ct));
            Assert.Contains(fault, (string)(await Scalar(Environment.CustomerAdmin(Alpha), "SELECT [Content] FROM [dbo].[oc_Document] WHERE [Type] LIKE @type", ("@type", "%" + type + "%")))!);
        }
        finally { await Execute(Environment.CustomerAdmin(Alpha), "UPDATE [dbo].[oc_Document] SET [Content] = @content WHERE [Type] LIKE @type", ("@content", content), ("@type", "%" + type + "%")); }
    }

    [Fact]
    public async Task Missing_identity_is_not_seeded_into_an_existing_database()
    {
        await using var database = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha));
        var identity = await database.Identity.AsNoTracking().SingleAsync(Ct);
        await Execute(Environment.CustomerAdmin(Alpha), "DISABLE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity] ON [tenancy].[Identity]; DELETE FROM [tenancy].[Identity]; ENABLE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity] ON [tenancy].[Identity];");
        try
        {
            await AssertClosed(Alpha);
            Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(Alpha), Ct));
            Assert.Equal(0, await Scalar(Environment.CustomerAdmin(Alpha), "SELECT COUNT(*) FROM [tenancy].[Identity]"));
        }
        finally { database.Identity.Add(identity); await database.SaveChangesAsync(Ct); }
    }

    [Fact]
    public async Task Wrong_runtime_target_fails_closed_and_never_falls_back_to_Default()
    {
        var key = TenancyConnections.TenantSecret(Alpha.Id, "runtime");
        var original = System.Environment.GetEnvironmentVariable(key);
        try
        {
            System.Environment.SetEnvironmentVariable(key, Environment.Runtime(Beta));
            await AssertClosed(Alpha);
            await AssertReady(Beta);
        }
        finally { System.Environment.SetEnvironmentVariable(key, original); }
    }

    [Fact]
    public async Task Missing_operator_credential_cannot_start_a_provisioning_operation()
    {
        var key = "SAAS_PLATFORM_OPERATOR_CONNECTION";
        var original = System.Environment.GetEnvironmentVariable(key);
        var tenant = Environment.NewTenant("no-operator");
        try
        {
            System.Environment.SetEnvironmentVariable(key, null);
            Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct));
            Assert.Equal(0, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", tenant.Id)));
        }
        finally { System.Environment.SetEnvironmentVariable(key, original); }
    }

    [Fact]
    public async Task Interrupted_native_warmup_resumes_the_same_operation_without_duplicate_identity()
    {
        var tenant = Environment.NewTenant("retry-warmup");
        var adapter = new ControlledOrchard { FailAfterInitialize = true };
        await Assert.ThrowsAsync<TenantContractException>(() => new TenancyProvisioning(new(), adapter).ProvisionAsync(Environment.Request(tenant), Ct));
        Assert.Equal(1, await Scalar(Environment.CustomerAdmin(tenant), "SELECT COUNT(*) FROM [tenancy].[Identity]"));
        Assert.Equal(0, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct));
        Assert.Equal(1, await Scalar(Environment.CustomerAdmin(tenant), "SELECT COUNT(*) FROM [tenancy].[Identity]"));
        Assert.Equal(1, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[OperationalAudit] WHERE [TenantId] = @id AND [Action] = 'ProvisionCompleted'", ("@id", tenant.Id)));
    }

    [Fact]
    public async Task Concurrent_retry_cannot_take_over_a_live_owner()
    {
        var tenant = Environment.NewTenant("concurrent");
        var adapter = new ControlledOrchard { PauseInitialize = true };
        var first = new TenancyProvisioning(new(), adapter).ProvisionAsync(Environment.Request(tenant), Ct);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        try { Assert.Equal(1, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct)); }
        finally { adapter.Resume.TrySetResult(); }
        await first;
        Assert.Equal(0, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct));
        Assert.Equal(1, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[OperationalAudit] WHERE [TenantId] = @id AND [Action] = 'ProvisionCompleted'", ("@id", tenant.Id)));
    }

    [Fact]
    public async Task A_newer_suspension_wins_over_inflight_provision_completion()
    {
        var tenant = Environment.NewTenant("suspended-inflight");
        var adapter = new ControlledOrchard { PauseInitialize = true };
        var task = new TenancyProvisioning(new(), adapter).ProvisionAsync(Environment.Request(tenant), Ct);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        try { await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Subscription] = 2, [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@id", tenant.Id)); }
        finally { adapter.Resume.TrySetResult(); }
        await Assert.ThrowsAsync<TenantContractException>(() => task);
        Assert.Equal(2, await Scalar(Environment.PlatformAdmin, "SELECT [Subscription] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", tenant.Id)));
        Assert.Equal(0, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[OperationalAudit] WHERE [TenantId] = @id AND [Action] = 'ProvisionCompleted'", ("@id", tenant.Id)));
        await AssertClosed(tenant);
    }

    [Fact]
    public async Task Audit_failure_cannot_publish_a_successful_checkpoint_and_retry_can_reconcile()
    {
        var tenant = Environment.NewTenant("audit-failure");
        var adapter = new ControlledOrchard { PauseVerify = true };
        var task = new TenancyProvisioning(new(), adapter).ProvisionAsync(Environment.Request(tenant), Ct);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        try
        {
            await Execute(Environment.PlatformAdmin, "DENY INSERT ON [platform].[OperationalAudit] TO [saas_operator]");
            adapter.Resume.TrySetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => task);
            Assert.Equal(0, await Scalar(Environment.PlatformAdmin, "SELECT COUNT(*) FROM [platform].[OperationalAudit] WHERE [TenantId] = @id AND [Action] = 'ProvisionCompleted'", ("@id", tenant.Id)));
            await AssertClosed(tenant);
        }
        finally { adapter.Resume.TrySetResult(); await Execute(Environment.PlatformAdmin, "REVOKE INSERT ON [platform].[OperationalAudit] FROM [saas_operator]"); }
        Assert.Equal(0, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), Ct));
    }

    [Fact]
    public async Task A_newly_provisioned_tenant_is_available_without_restarting_the_host()
    {
        // Environment-only adapter: the future tenant's runtime secret MUST precede HTTP startup.
        var tenant = Environment.NewTenant("preloaded-live");
        await using var runtime = await M1ChildHost.StartRuntimeAsync(Ct);
        await runtime.AssertReadyAsync(Beta, Ct); // Initialize the real native inventory before provisioning.
        await runtime.AssertStatusAsync($"/{tenant.Slug}/health/ready", HttpStatusCode.NotFound, Ct);
        Assert.Equal(0, await M1ChildHost.ProvisionAsync(Environment.ProvisionArguments(tenant), Ct));
        await runtime.AssertReadyAsync(tenant, Ct); // Same HTTP child; maintenance ran in a separate process.
    }

    [Fact]
    public async Task Fresh_runtime_processes_start_with_only_low_privilege_connections()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var host = Path.Combine(directory.FullName, "src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll");
        for (var restart = 0; restart < 2; restart++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.FullName };
            start.ArgumentList.Add(host);
            start.ArgumentList.Add("--urls");
            start.ArgumentList.Add("http://127.0.0.1:" + port);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SAAS_", StringComparison.OrdinalIgnoreCase)
                && key != "SAAS_SQL_SERVER" && key != "SAAS_PLATFORM_RUNTIME_CONNECTION" && !(key.StartsWith("SAAS_TENANT_", StringComparison.Ordinal) && key.EndsWith("_RUNTIME", StringComparison.Ordinal))).ToArray()) start.Environment.Remove(key);
            start.Environment.Remove("MSSQL_SA_PASSWORD");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            var error = process.StandardError.ReadToEndAsync(Ct);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(15) };
                var live = false;
                for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
                {
                    try { live = (await client.GetAsync("/health/live", Ct)).IsSuccessStatusCode; }
                    catch (HttpRequestException) { }
                    if (live) break;
                    await Task.Delay(100, Ct);
                }
                Assert.True(live, "Runtime-only process did not become live.");
                foreach (var tenant in Environment.Tenants)
                    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/{tenant.Slug}/health/ready", Ct)).StatusCode);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(Ct);
                await Task.WhenAll(output, error); // Drain output without publishing deployment configuration.
            }
        }
    }

    private async Task AssertReady(TestTenant tenant) => Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync($"/{tenant.Slug}/health/ready", Ct)).StatusCode);
    private async Task AssertClosed(TestTenant tenant) => Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.Client.GetAsync($"/{tenant.Slug}/health/ready", Ct)).StatusCode);
    private static async Task<object?> Scalar(string connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var database = await SqlOperations.OpenAsync(connection, Ct);
        return await SqlOperations.ScalarAsync(database, sql, Ct, parameters);
    }
    private static async Task Execute(string connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var database = await SqlOperations.OpenAsync(connection, Ct);
        await SqlOperations.ExecuteAsync(database, sql, Ct, parameters);
    }
}

public sealed class M1SqlFixture : IAsyncLifetime
{
    internal M1SqlEnvironment Environment { get; } = new();
    private readonly WebApplicationFactory<Program> _factory = new();
    public HttpClient Client { get; private set; } = null!;
    public async ValueTask InitializeAsync()
    {
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["bootstrap", Environment.InstallationId.ToString("D")], CancellationToken.None));
        foreach (var tenant in Environment.Tenants)
            Assert.Equal(0, await MaintenanceCommand.RunAsync(Environment.ProvisionArguments(tenant), CancellationToken.None));
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _factory.DisposeAsync();
        Environment.Dispose();
    }
}

internal sealed record TestTenant(Guid Id, Guid OperationId, string Slug);
internal sealed class M1SqlEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    public string Admin { get; }
    public string PlatformAdmin => new SqlConnectionStringBuilder(Admin) { InitialCatalog = TenancyConnections.PlatformDatabase }.ConnectionString;
    public Guid InstallationId { get; } = Guid.NewGuid();
    public TestTenant[] Tenants { get; }
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "saas-m1-" + Guid.NewGuid().ToString("N"));
    private readonly string _expiry = DateTimeOffset.UtcNow.AddYears(1).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    public M1SqlEnvironment()
    {
        Admin = System.Environment.GetEnvironmentVariable("SAAS_M1_ADMIN_CONNECTION") ?? throw new InvalidOperationException("BLOCKED: real SQL fixture required; M1 tests cannot be skipped.");
        var sql = new SqlConnectionStringBuilder(Admin);
        Set("SAAS_SQL_SERVER", sql.DataSource);
        Set("SAAS_PROVISIONER_CONNECTION", Admin);
        Set("SAAS_OPERATIONS_HOME", Home);
        foreach (var role in new[] { "runtime", "operator", "migrator" })
            Set($"SAAS_PLATFORM_{role.ToUpperInvariant()}_CONNECTION", Credential(TenancyConnections.PlatformDatabase, "saas_platform_" + role));
        Tenants = new[] { "alpha", "beta", "gamma", "delta" }.Select(NewTenant).ToArray();
    }
    public TestTenant NewTenant(string slug)
    {
        var tenant = new TestTenant(Guid.NewGuid(), Guid.NewGuid(), slug);
        Set(TenancyConnections.TenantSecret(tenant.Id, "runtime"), Credential(TenancyConnections.TenantDatabase(tenant.Id), "saas_rt_" + tenant.Id.ToString("N")));
        Set(TenancyConnections.TenantSecret(tenant.Id, "migrator"), Credential(TenancyConnections.TenantDatabase(tenant.Id), "saas_mg_" + tenant.Id.ToString("N")));
        return tenant;
    }
    private string Credential(string database, string user)
    {
        var sql = new SqlConnectionStringBuilder(Admin) { InitialCatalog = database, UserID = user, Pooling = false };
        sql[nameof(SqlConnectionStringBuilder.Password)] = string.Concat(Guid.NewGuid().ToString("N"), "Aa1!");
        return sql.ConnectionString;
    }
    public string Runtime(TestTenant tenant) => System.Environment.GetEnvironmentVariable(TenancyConnections.TenantSecret(tenant.Id, "runtime"))!;
    public string Migrator(TestTenant tenant) => System.Environment.GetEnvironmentVariable(TenancyConnections.TenantSecret(tenant.Id, "migrator"))!;
    public string CustomerAdmin(TestTenant tenant) => new SqlConnectionStringBuilder(Admin) { InitialCatalog = TenancyConnections.TenantDatabase(tenant.Id) }.ConnectionString;
    public string[] ProvisionArguments(TestTenant tenant) => ["provision", tenant.Id.ToString("D"), tenant.OperationId.ToString("D"), tenant.Slug, "Standard", _expiry];
    public ProvisionRequest Request(TestTenant tenant) => ProvisionRequest.Parse(ProvisionArguments(tenant));
    private void Set(string key, string value)
    {
        _previous.TryAdd(key, System.Environment.GetEnvironmentVariable(key));
        System.Environment.SetEnvironmentVariable(key, value);
    }
    public void Dispose()
    {
        foreach (var item in _previous) System.Environment.SetEnvironmentVariable(item.Key, item.Value);
        if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
    }
}

internal sealed class ControlledOrchard : IOrchardMaintenance
{
    private readonly OrchardMaintenance _native = new();
    public bool PauseInitialize { get; init; }
    public bool PauseVerify { get; init; }
    public bool FailAfterInitialize { get; init; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<OrchardBaseline> InitializeAsync(string shell, string prefix, Guid tenantId, string connection, CancellationToken cancellationToken)
    {
        if (PauseInitialize) { Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
        var result = await _native.InitializeAsync(shell, prefix, tenantId, connection, cancellationToken);
        if (FailAfterInitialize) throw new TenantContractException("injected_native_warmup_failure");
        return result;
    }
    public async Task<OrchardBaseline> VerifyAsync(string shell, string prefix, Guid tenantId, string connection, CancellationToken cancellationToken)
    {
        if (PauseVerify) { Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
        return await _native.VerifyAsync(shell, prefix, tenantId, connection, cancellationToken);
    }
}
