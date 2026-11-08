using System.Net;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Access;
using SaaS.Web.Release;
using Xunit;

namespace SaaS.UnitTests;

[Collection("M6 SQL")]
[Trait("Category", "SqlM6")]
public sealed class M6PlatformReleaseSqlTests(M1SqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Offline_platform_release_blocks_live_sessions_pending_HTTP_and_tenant_work_then_resumes_once()
    {
        // Reset the disposable tenant lifecycle, then STOP the real in-process HTTP fixture.
        await using (var setup = await new M4HttpTests(fixture).HttpAsync()) { }
        await using var package = await M6ReleaseSqlTests.PackageAsync();
        var connections = new TenancyConnections();
        var service = new PlatformReleaseMaintenance(connections, package);
        var operation = Guid.NewGuid();
        var previousAck = Environment.GetEnvironmentVariable("SAAS_PLATFORM_RELEASE_ACK");
        var unknown = "99999999999999_M6FixtureUnknown_" + operation.ToString("N");
        try
        {
            Environment.SetEnvironmentVariable("SAAS_PLATFORM_RELEASE_ACK", null);
            var noAck = await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(operation, false, Ct));
            Assert.Equal("platform_offline_ack_required", noAck.Code);
            Environment.SetEnvironmentVariable("SAAS_PLATFORM_RELEASE_ACK", "all-runtime-hosts-stopped-v1");
            // A real runtime process is alive; an explicit runtime SQL session makes the
            // session barrier observable rather than relying on a transient request window.
            await using (var runtime = await M1ChildHost.StartRuntimeAsync(Ct))
            {
                await runtime.AssertStatusAsync("/health/ready", HttpStatusCode.OK, Ct);
                await using var session = await SqlOperations.OpenAsync(connections.Platform("runtime"), Ct);
                var live = await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(operation, false, Ct));
                Assert.Equal("runtime_sql_sessions_active", live.Code);
                await using var platform = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
                Assert.False(await platform.Audit.AnyAsync(x => x.OperationId == operation, Ct));
            } // M1ChildHost kills only its owned child and awaits OS exit; session is disposed.
            await AssertNoRuntimeSessionsAsync();

            // Real concurrent resource ownership excludes offline platform schema mutation.
            var tenant = fixture.Environment.Tenants[0];
            await using (var lease = await OperationLease.AcquireAsync(connections.Platform("operator"),
                "tenant-" + tenant.Id.ToString("N"), Ct))
            {
                var busy = await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(operation, false, Ct));
                Assert.Equal("platform_release_busy", busy.Code);
            }
            await using (var sql = await SqlOperations.OpenAsync(fixture.Environment.PlatformAdmin, Ct))
                await SqlOperations.ExecuteAsync(sql,
                    "INSERT INTO [platform].[__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (@id, 'fixture');", Ct, ("@id", unknown));
            var mismatch = await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(operation, false, Ct));
            Assert.Equal("platform_release_history_mismatch", mismatch.Code);
            await using (var platform = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin))
            {
                Assert.Single(await platform.Audit.Where(x => x.OperationId == operation && x.Action == PlatformReleaseBarrier.Bound).ToArrayAsync(Ct));
                Assert.False(await platform.Audit.AnyAsync(x => x.OperationId == operation && x.Action == PlatformReleaseBarrier.Completed, Ct));
                Assert.Contains(unknown, await platform.Database.GetAppliedMigrationsAsync(Ct));
            }
            // Start an actual runtime process against the durable audit barrier; liveness
            // must work but readiness and tenant business paths must return HTTP 503.
            await using (var runtime = await M1ChildHost.StartRuntimeAsync(Ct))
            {
                await runtime.AssertStatusAsync("/health/live", HttpStatusCode.OK, Ct);
                foreach (var path in new[] { "/health/ready", "/alpha/health/ready", "/alpha/orders", "/alpha/api/v1/orders" })
                    await runtime.AssertStatusAsync(path, HttpStatusCode.ServiceUnavailable, Ct);
                await using var platform = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
                var current = await platform.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenant.Id, Ct);
                var denied = await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(connections)
                    .InstallAsync(tenant.Id, Guid.NewGuid(), current.RegistryVersion, Ct));
                Assert.Equal("platform_release_incomplete", denied.Code);
                var newRelease = await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(Guid.NewGuid(), false, Ct));
                Assert.Equal("platform_release_incomplete", newRelease.Code);
                var noResume = await Assert.ThrowsAsync<TenantContractException>(() => service.ApplyAsync(operation, false, Ct));
                Assert.Equal("platform_release_resume_required", noResume.Code);
            }
            await AssertNoRuntimeSessionsAsync();
            // Revert ONLY the unknown-history row injected by this disposable test. This
            // is not a production repair recipe: the coordinator itself never deletes it.
            await RemoveInjectedHistoryAsync(unknown);
            var result = await service.ApplyAsync(operation, true, Ct);
            Assert.True(result.Completed);
            Assert.Equal(package.ManifestSha256, result.ManifestSha256);
            Assert.Equal(result, await service.ApplyAsync(operation, true, Ct));
            await using (var platform = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin))
            {
                Assert.Single(await platform.Audit.Where(x => x.OperationId == operation && x.Action == PlatformReleaseBarrier.Bound).ToArrayAsync(Ct));
                Assert.Single(await platform.Audit.Where(x => x.OperationId == operation && x.Action == PlatformReleaseBarrier.Completed).ToArrayAsync(Ct));
                await TenancyConnections.VerifySchemaAsync(platform, Ct);
                Assert.False(await platform.Tenants.AnyAsync(x => x.Operation != OperationStatus.None, Ct));
            }
            await using (var restarted = await M1ChildHost.StartRuntimeAsync(Ct))
            {
                await restarted.AssertStatusAsync("/health/ready", HttpStatusCode.OK, Ct);
                foreach (var item in fixture.Environment.Tenants) await restarted.AssertReadyAsync(item, Ct);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAAS_PLATFORM_RELEASE_ACK", previousAck);
            await RemoveInjectedHistoryAsync(unknown);
            // On assertion failure, remove only this test's external audit barrier so a
            // shared disposable fixture cannot contaminate unrelated tests. Never mark it
            // Completed or report this cleanup as successful coordinator recovery.
            await using var platform = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
            if (!await platform.Audit.AnyAsync(x => x.OperationId == operation && x.Action == PlatformReleaseBarrier.Completed, CancellationToken.None))
                await platform.Audit.Where(x => x.OperationId == operation).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private async Task RemoveInjectedHistoryAsync(string migration)
    {
        await using var sql = await SqlOperations.OpenAsync(fixture.Environment.PlatformAdmin, CancellationToken.None);
        await SqlOperations.ExecuteAsync(sql, "DELETE FROM [platform].[__EFMigrationsHistory] WHERE [MigrationId] = @id;",
            CancellationToken.None, ("@id", migration));
    }

    private async Task AssertNoRuntimeSessionsAsync()
    {
        await using var server = await SqlOperations.OpenAsync(new TenancyConnections().Provisioner(), Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var count = Convert.ToInt32(await SqlOperations.ScalarAsync(server,
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name = 'saas_platform_runtime' OR login_name LIKE 'saas[_]rt[_]%'", deadline.Token));
            if (count == 0) return;
            await Task.Delay(100, deadline.Token);
        }
    }
}
