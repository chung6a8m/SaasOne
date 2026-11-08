using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M7 product SQL")]
[Trait("Category", "SqlM7")]
[Trait("M7Suite", "Product")]
public sealed class M7ProductSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Published_independent_product_uses_four_restricted_SQL_shells_and_truthful_readiness()
    {
        var binary = System.Environment.GetEnvironmentVariable("SAAS_M7_PRODUCT_DLL");
        Assert.True(binary is not null && File.Exists(binary), "BLOCKED: publish the independent product first.");
        using var environment = new M1SqlEnvironment();
        await using (var closed = await ProductProcess.StartAsync(binary!, environment, configured: false))
        {
            await StatusAsync(closed.Client, "/health/ready", HttpStatusCode.ServiceUnavailable);
            await StatusAsync(closed.Client, "/setup", HttpStatusCode.NotFound);
        }
        await MaintenanceAsync(binary!, ["bootstrap", environment.InstallationId.ToString("D")]);
        foreach (var tenant in environment.Tenants) await MaintenanceAsync(binary!, environment.ProvisionArguments(tenant));
        await using var product = await ProductProcess.StartAsync(binary!, environment, configured: true);
        await StatusAsync(product.Client, "/health/ready", HttpStatusCode.OK);
        foreach (var tenant in environment.Tenants)
        {
            using var ready = await product.Client.GetAsync("/" + tenant.Slug + "/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            using var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
            Assert.Equal(tenant.Id, json.RootElement.GetProperty("tenantId").GetGuid());
            using var home = await product.Client.GetAsync("/" + tenant.Slug + "/", Ct);
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.Contains("<h1>Workspace</h1>", await home.Content.ReadAsStringAsync(Ct));
            foreach (var suffix in new[] { "setup", "admin", "api/v1/orders", "orders", "features", "media" })
                await StatusAsync(product.Client, "/" + tenant.Slug + "/" + suffix, HttpStatusCode.NotFound);
            await using var sql = await SqlOperations.OpenAsync(environment.CustomerAdmin(tenant), Ct);
            Assert.Equal(0, Convert.ToInt32(await SqlOperations.ScalarAsync(sql,
                "SELECT COUNT(*) FROM sys.schemas WHERE name IN ('orders','approvals')", Ct)));
            await using var runtime = await SqlOperations.OpenAsync(environment.Runtime(tenant), Ct);
            Assert.Equal(0, Convert.ToInt32(await SqlOperations.ScalarAsync(runtime,
                "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE')", Ct)));
        }
        // Fixture-only registry/DB identity mismatch; do not rewrite the immutable customer identity.
        var alpha = environment.Tenants.Single(x => x.Slug == "alpha");
        await using (var db = TenancyConnections.PlatformContext(environment.PlatformAdmin))
        {
            var registration = await db.Tenants.SingleAsync(x => x.Id == alpha.Id, Ct);
            registration.DatabaseNonce = Guid.NewGuid(); registration.RegistryVersion++;
            await db.SaveChangesAsync(Ct);
        }
        await StatusAsync(product.Client, "/alpha/health/ready", HttpStatusCode.ServiceUnavailable);
        await StatusAsync(product.Client, "/alpha/", HttpStatusCode.ServiceUnavailable);
        await StatusAsync(product.Client, "/beta/health/ready", HttpStatusCode.OK);
        await StatusAsync(product.Client, "/health/live", HttpStatusCode.OK);
    }
    private static async Task StatusAsync(HttpClient client, string path, HttpStatusCode status)
    {
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(status, response.StatusCode);
    }
    private static ProcessStartInfo Info(string binary)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(binary)! };
        info.ArgumentList.Add(binary);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        return info;
    }
    private static async Task MaintenanceAsync(string binary, string[] args)
    {
        var info = Info(binary);
        info.ArgumentList.Add("--maintenance");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start maintenance.");
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(3), Ct);
            await Task.WhenAll(stdout, stderr);
            Assert.True(process.ExitCode == 0, "Independent maintenance must complete. Provider/credential text is not exported.");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    private sealed class ProductProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        public HttpClient Client { get; }
        private ProductProcess(Process process, Uri address)
        {
            _process = process; _stdout = process.StandardOutput.ReadToEndAsync(); _stderr = process.StandardError.ReadToEndAsync();
            Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
        }
        public static async Task<ProductProcess> StartAsync(string binary, M1SqlEnvironment environment, bool configured)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var address = new Uri("http://127.0.0.1:" + port);
            var info = Info(binary);
            foreach (var key in info.Environment.Keys.Where(x => x.StartsWith("SAAS_", StringComparison.Ordinal) || x.StartsWith("MSSQL_", StringComparison.Ordinal)).ToArray())
                info.Environment.Remove(key);
            info.Environment["ASPNETCORE_URLS"] = address.AbsoluteUri;
            if (configured)
            {
                foreach (var key in new[] { "SAAS_SQL_SERVER", "SAAS_PLATFORM_RUNTIME_CONNECTION" }.Concat(environment.Tenants.Select(x => TenancyConnections.TenantSecret(x.Id, "runtime"))))
                    info.Environment[key] = System.Environment.GetEnvironmentVariable(key);
            }
            var result = new ProductProcess(Process.Start(info) ?? throw new InvalidOperationException("Cannot start product."), address);
            try
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    Assert.False(result._process.HasExited, "Published product exited before liveness.");
                    try { using var response = await result.Client.GetAsync("/health/live", Ct); if (response.StatusCode == HttpStatusCode.OK) return result; }
                    catch (HttpRequestException) { }
                    await Task.Delay(250, Ct);
                }
                throw new InvalidOperationException("Published product did not become live.");
            }
            catch { await result.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(_stdout, _stderr);
            _process.Dispose();
        }
    }
}
[CollectionDefinition("M7 product SQL", DisableParallelization = true)]
public sealed class M7ProductSqlCollection;
