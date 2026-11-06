using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using SaaS.Web.Maintenance;
using Xunit;
namespace SaaS.UnitTests;

[CollectionDefinition("M1 SQL", DisableParallelization = true)]
public sealed class M1SqlCollection { }

[Collection("M1 SQL")]
[Trait("Category", "SqlM1")]
public sealed class M1SqlTests
{
    [Fact]
    public async Task Four_real_customer_databases_are_provisioned_and_routed_through_Orchard()
    {
        using var environment = new M1SqlEnvironment();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["bootstrap", environment.InstallationId.ToString("D")], ct));
        foreach (var tenant in environment.Tenants)
            Assert.Equal(0, await MaintenanceCommand.RunAsync(environment.ProvisionArguments(tenant), ct));
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var tenant in environment.Tenants)
        {
            using var response = await client.GetAsync($"/{tenant.Slug}/health/ready", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(tenant.Id.ToString("D"), document.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal($"/{tenant.Slug}/", document.RootElement.GetProperty("home").GetString());
            await using var connection = new SqlConnection(environment.Runtime(tenant));
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [TenantId] FROM [tenancy].[Identity] WHERE [Id] = 1";
            Assert.Equal(tenant.Id, await command.ExecuteScalarAsync(ct));
        }
    }
}

internal sealed record TestTenant(Guid Id, Guid OperationId, string Slug);

internal sealed class M1SqlEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private readonly string _admin;
    public Guid InstallationId { get; } = Guid.NewGuid();
    public TestTenant[] Tenants { get; } = [new(Guid.NewGuid(), Guid.NewGuid(), "alpha"), new(Guid.NewGuid(), Guid.NewGuid(), "beta"), new(Guid.NewGuid(), Guid.NewGuid(), "gamma"), new(Guid.NewGuid(), Guid.NewGuid(), "delta")];
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "saas-m1-" + Guid.NewGuid().ToString("N"));
    public M1SqlEnvironment()
    {
        _admin = Environment.GetEnvironmentVariable("SAAS_M1_ADMIN_CONNECTION") ?? throw new InvalidOperationException("BLOCKED: real SQL fixture required; M1 tests cannot be skipped.");
        var sql = new SqlConnectionStringBuilder(_admin);
        Set("SAAS_SQL_SERVER", sql.DataSource);
        Set("SAAS_PROVISIONER_CONNECTION", _admin);
        Set("SAAS_OPERATIONS_HOME", Home);
        foreach (var role in new[] { "runtime", "operator", "migrator" })
            Set($"SAAS_PLATFORM_{role.ToUpperInvariant()}_CONNECTION", Credential("saas_platform", "saas_platform_" + role));
        foreach (var tenant in Tenants)
        {
            Set(Key(tenant, "RUNTIME"), Credential("saas_t_" + tenant.Id.ToString("N"), "saas_rt_" + tenant.Id.ToString("N")));
            Set(Key(tenant, "MIGRATOR"), Credential("saas_t_" + tenant.Id.ToString("N"), "saas_mg_" + tenant.Id.ToString("N")));
        }
    }
    private string Credential(string database, string user)
    {
        var sql = new SqlConnectionStringBuilder(_admin) { InitialCatalog = database, UserID = user, Pooling = false };
        sql[nameof(SqlConnectionStringBuilder.Password)] = string.Concat(Guid.NewGuid().ToString("N"), "Aa1!");
        return sql.ConnectionString;
    }
    private static string Key(TestTenant tenant, string role) => $"SAAS_TENANT_{tenant.Id:N}_{role}".ToUpperInvariant();
    public string Runtime(TestTenant tenant) => Environment.GetEnvironmentVariable(Key(tenant, "RUNTIME"))!;
    public string[] ProvisionArguments(TestTenant tenant) => ["provision", tenant.Id.ToString("D"), tenant.OperationId.ToString("D"), tenant.Slug, "Standard", "2027-11-06T00:00:00+00:00"];
    private void Set(string key, string value)
    {
        _previous.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }
    public void Dispose()
    {
        foreach (var item in _previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
        if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
    }
}
