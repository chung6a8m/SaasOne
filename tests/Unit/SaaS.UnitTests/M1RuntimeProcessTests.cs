using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Web.Orchard;
using Xunit;
namespace SaaS.UnitTests;

// Shares the existing real-SQL fixture and serialized collection; never bootstraps a second platform.
public sealed partial class M1SqlTests
{
    [Theory]
    [InlineData("missing", 1)]
    [InlineData("target", 1)]
    [InlineData("principal", 1)]
    [InlineData("missing", 2)]
    [InlineData("target", 2)]
    [InlineData("principal", 2)]
    public async Task Cold_start_isolates_a_customer_secret_fault(string fault, int subscription)
    {
        var original = await Scalar(Environment.PlatformAdmin, "SELECT [Subscription] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", Alpha.Id));
        try
        {
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Subscription] = @state, [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@state", subscription), ("@id", Alpha.Id));
            await using var runtime = await M1ChildHost.StartRuntimeAsync(Ct, start =>
            {
                var key = TenancyConnections.TenantSecret(Alpha.Id, "runtime");
                if (fault == "missing") start.Environment.Remove(key);
                else start.Environment[key] = fault == "target" ? Environment.Runtime(Beta)
                    : new SqlConnectionStringBuilder(Environment.Runtime(Alpha)) { UserID = "wrong_runtime_principal" }.ConnectionString;
            });
            await runtime.AssertReadyAsync(Beta, Ct);
            await runtime.AssertStatusAsync("/health/ready", HttpStatusCode.OK, Ct);
            await runtime.AssertStatusAsync("/alpha/health/ready", HttpStatusCode.ServiceUnavailable, Ct);
        }
        finally
        {
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Subscription] = @state, [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@state", original), ("@id", Alpha.Id));
        }
    }

    [Fact]
    public async Task Named_shell_lookup_and_names_do_not_resolve_unrelated_secrets()
    {
        var key = TenancyConnections.TenantSecret(Alpha.Id, "runtime");
        var original = System.Environment.GetEnvironmentVariable(key);
        try
        {
            System.Environment.SetEnvironmentVariable(key, null);
            var manager = new RegistryShellSettings(new TenancyConnections());
            Assert.Equal("beta", (await manager.LoadSettingsAsync("beta")).Name);
            Assert.Equal("Default", (await manager.LoadSettingsAsync("Default")).Name);
            Assert.Contains("beta", await manager.LoadSettingsNamesAsync());
            var inventory = (await manager.LoadSettingsAsync()).ToArray();
            Assert.Contains(inventory, settings => settings.Name == "beta");
            Assert.DoesNotContain(inventory, settings => settings.Name == "alpha");
            var error = await Assert.ThrowsAsync<TenantContractException>(() => manager.LoadSettingsAsync("alpha"));
            Assert.Equal("missing_deployment_secret", error.Code);
        }
        finally { System.Environment.SetEnvironmentVariable(key, original); }
    }

    [Fact]
    public async Task Invalid_customer_registration_does_not_block_other_shells()
    {
        var original = await Scalar(Environment.PlatformAdmin, "SELECT [Plan] FROM [platform].[Tenants] WHERE [Id] = @id", ("@id", Alpha.Id));
        try
        {
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Plan] = 'Unknown', [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@id", Alpha.Id));
            await using var runtime = await M1ChildHost.StartRuntimeAsync(Ct);
            await runtime.AssertReadyAsync(Beta, Ct);
            await runtime.AssertStatusAsync("/health/ready", HttpStatusCode.OK, Ct);
            await runtime.AssertStatusAsync("/alpha/health/ready", HttpStatusCode.ServiceUnavailable, Ct);
        }
        finally
        {
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Tenants] SET [Plan] = @plan, [RegistryVersion] = [RegistryVersion]+1 WHERE [Id] = @id", ("@plan", original), ("@id", Alpha.Id));
        }
    }

    [Fact]
    public async Task Corrupt_platform_baseline_keeps_all_readiness_closed()
    {
        var original = await Scalar(Environment.PlatformAdmin, "SELECT [BaselineVersion] FROM [platform].[Bootstrap] WHERE [Id] = 1");
        try
        {
            await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Bootstrap] SET [BaselineVersion] = 0 WHERE [Id] = 1");
            await using var runtime = await M1ChildHost.StartRuntimeAsync(Ct);
            await runtime.AssertStatusAsync("/health/ready", HttpStatusCode.ServiceUnavailable, Ct);
            await runtime.AssertStatusAsync("/beta/health/ready", HttpStatusCode.ServiceUnavailable, Ct);
            await runtime.AssertStatusAsync("/health/live", HttpStatusCode.OK, Ct);
        }
        finally { await Execute(Environment.PlatformAdmin, "UPDATE [platform].[Bootstrap] SET [BaselineVersion] = @version WHERE [Id] = 1", ("@version", original)); }
    }

    [Fact]
    public async Task Late_runtime_secret_requires_a_controlled_process_restart()
    {
        TestTenant tenant;
        await using (var runtime = await M1ChildHost.StartRuntimeAsync(Ct))
        {
            await runtime.AssertReadyAsync(Beta, Ct); // Inventory is initialized in the child before allocation.
            tenant = Environment.NewTenant("late-secret"); // Changes parent/CLI environment, NOT the running HTTP child.
            Assert.Equal(0, await M1ChildHost.ProvisionAsync(Environment.ProvisionArguments(tenant), Ct));
            await runtime.AssertStatusAsync($"/{tenant.Slug}/health/ready", HttpStatusCode.ServiceUnavailable, Ct);
            await runtime.AssertReadyAsync(Beta, Ct);
            await runtime.AssertStatusAsync("/health/ready", HttpStatusCode.OK, Ct);
        }
        await using var restarted = await M1ChildHost.StartRuntimeAsync(Ct); // Receives the updated runtime-only environment.
        await restarted.AssertReadyAsync(tenant, Ct);
        await restarted.AssertReadyAsync(Beta, Ct);
        await restarted.AssertStatusAsync("/health/ready", HttpStatusCode.OK, Ct);
    }
}

/// <summary>Test-only real process boundary. Captured output is drained, never attached to assertions or logs.</summary>
internal sealed class M1ChildHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private readonly HttpClient _client;
    private M1ChildHost(Process process, string address)
    {
        _process = process;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
    }

    public static async Task<M1ChildHost> StartRuntimeAsync(CancellationToken cancellationToken, Action<ProcessStartInfo>? configure = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = "http://127.0.0.1:" + port;
        var start = StartInfo();
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(address);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SAAS_", StringComparison.OrdinalIgnoreCase) && !IsRuntimeKey(key)).ToArray())
            start.Environment.Remove(key);
        start.Environment.Remove("MSSQL_SA_PASSWORD");
        configure?.Invoke(start);
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("SAAS_", StringComparison.OrdinalIgnoreCase) && !IsRuntimeKey(key));
        var host = new M1ChildHost(Process.Start(start) ?? throw new InvalidOperationException("Runtime child did not start."), address);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            while (!host._process.HasExited)
            {
                try
                {
                    using var response = await host._client.GetAsync("/health/live", deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK) return host;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, deadline.Token);
            }
            throw new InvalidOperationException("Runtime child exited before liveness.");
        }
        catch { await host.DisposeAsync(); throw; }
    }

    public async Task AssertStatusAsync(string path, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        Assert.False(_process.HasExited, "The original runtime child must still be running.");
        using var response = await _client.GetAsync(path, cancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    public async Task AssertReadyAsync(TestTenant tenant, CancellationToken cancellationToken)
    {
        Assert.False(_process.HasExited, "The original runtime child must still be running.");
        using var response = await _client.GetAsync($"/{tenant.Slug}/health/ready", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(tenant.Id.ToString("D"), document.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal($"/{tenant.Slug}/", document.RootElement.GetProperty("home").GetString());
    }

    public static async Task<int> ProvisionAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = StartInfo();
        start.ArgumentList.Add("--maintenance");
        foreach (var argument in arguments) start.ArgumentList.Add(argument); // IDs/slug/plan/expiry only; never credentials.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Maintenance child did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
        }
    }

    private static ProcessStartInfo StartInfo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root missing.");
        var assembly = Path.Combine(directory.FullName, "src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll");
        if (!File.Exists(assembly)) throw new InvalidOperationException("Fresh Release host build required.");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.FullName };
        start.ArgumentList.Add(assembly);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        return start;
    }

    private static bool IsRuntimeKey(string key) => key == "SAAS_SQL_SERVER" || key == "SAAS_PLATFORM_RUNTIME_CONNECTION"
        || key.StartsWith("SAAS_TENANT_", StringComparison.Ordinal) && key.EndsWith("_RUNTIME", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(_output, _error);
        }
        finally { _process.Dispose(); }
    }
}
