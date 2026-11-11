using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M7SqlAcceptanceTests
{
    [Fact]
    public async Task Published_independent_product_uses_runtime_only_and_fails_closed()
    {
        var connection = new TenancyConnections().Platform("runtime");
        await ProductAsync(connection, HttpStatusCode.OK);
        await ProductAsync(null, HttpStatusCode.ServiceUnavailable);
        await ProductAsync(new SqlConnectionStringBuilder(connection) { InitialCatalog = "wrong_product_database" }.ConnectionString, HttpStatusCode.ServiceUnavailable);
        await using (var sql = await SqlOperations.OpenAsync(fixture.Environment.PlatformAdmin, Ct))
        {
            await SqlOperations.ExecuteAsync(sql, "ALTER ROLE [db_owner] ADD MEMBER [saas_platform_runtime];", Ct);
            try { await ProductAsync(connection, HttpStatusCode.ServiceUnavailable); }
            finally { await SqlOperations.ExecuteAsync(sql, "ALTER ROLE [db_owner] DROP MEMBER [saas_platform_runtime];", CancellationToken.None); }
        }
        var operation = Guid.NewGuid();
        await using (var db = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin))
        {
            db.Audit.Add(new OperationalAudit { OperationId = operation, Actor = "m7-disposable-fixture", Action = PlatformReleaseBarrier.Bound, OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }
        try { await ProductAsync(connection, HttpStatusCode.ServiceUnavailable); }
        finally
        {
            await using var db = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
            db.Audit.Add(new OperationalAudit { OperationId = operation, Actor = "m7-disposable-fixture", Action = PlatformReleaseBarrier.Completed, OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await ProductAsync(connection, HttpStatusCode.OK);
        M7Evidence.Write("product-runtime.json", new
        {
            schemaVersion = 1, sourceSha = M7Evidence.SourceSha, status = "PASS", process = "Published SaaS.Portal.dll",
            cases = new[] { "runtime-only-ready-and-own-theme", "missing-source", "wrong-database", "overprivileged-runtime", "platform-release-barrier", "ready-after-barrier" },
            childPrivilegedCredentials = false, publicSetup = false, customerBusinessRoutes = false
        });
    }

    private static async Task ProductAsync(string? runtimeConnection, HttpStatusCode expected)
    {
        var dll = System.Environment.GetEnvironmentVariable("SAAS_M7_PRODUCT_DLL") ?? throw new InvalidOperationException("BLOCKED: independently published product required.");
        Assert.True(Path.IsPathFullyQualified(dll) && File.Exists(dll), "Published independent product is missing.");
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0)) { reservation.Start(); port = ((IPEndPoint)reservation.LocalEndpoint).Port; }
        var origin = new Uri("http://127.0.0.1:" + port);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(dll)! };
        start.ArgumentList.Add(dll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(origin.AbsoluteUri);
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("SAAS_", StringComparison.OrdinalIgnoreCase)
            || x.StartsWith("MSSQL_", StringComparison.OrdinalIgnoreCase) || x == "DOTNET_STARTUP_HOOKS").ToArray()) start.Environment.Remove(key);
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (runtimeConnection is not null)
        {
            start.Environment["SAAS_SQL_SERVER"] = System.Environment.GetEnvironmentVariable("SAAS_SQL_SERVER");
            start.Environment["SAAS_PLATFORM_RUNTIME_CONNECTION"] = runtimeConnection;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Independent product process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(5) };
            var live = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                Assert.False(process.HasExited, "Independent product exited before liveness.");
                try
                {
                    using var response = await client.GetAsync("/health/live", Ct);
                    if (response.StatusCode == HttpStatusCode.OK) { live = true; break; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, Ct);
            }
            Assert.True(live, "Independent product liveness timed out.");
            using (var ready = await client.GetAsync("/health/ready", Ct)) Assert.Equal(expected, ready.StatusCode);
            using var home = await client.GetAsync("/", Ct);
            Assert.Equal(expected, home.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                var html = await home.Content.ReadAsStringAsync(Ct);
                Assert.Contains("SaasOne Portal", html); Assert.Contains("Portal theme", html);
                Assert.Equal("no-store", home.Headers.CacheControl?.ToString());
                foreach (var path in new[] { "/setup", "/account/login", "/alpha/api/v1/orders", "/alpha/orders" })
                    using (var absent = await client.GetAsync(path, Ct)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            // Do not export arbitrary child output; assertions above are the evidence.
            _ = await stdout; _ = await stderr;
        }
    }
}
