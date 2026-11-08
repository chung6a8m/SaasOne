using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
using static SaaS.UnitTests.M4HttpTests;

namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public async Task Killed_OS_process_during_observed_SQL_RESTORE_requires_quiescence_and_same_source_retry()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync();
        await AuthorityAsync();
        using var browser = setup.Http.NewBrowser();
        await LoginAsync(setup.Http, browser);
        var old = setup.Http.CapturedTokens["alpha"];
        using var store = new SqlBackupStore();
        var clean = await BackupAsync(store);
        var operation = Guid.NewGuid();
        var connections = new TenancyConnections();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SaasOne.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var evidencePath = Path.Combine(root.FullName, "artifacts", "verification", "m6-restore-process.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var evidence = new Dictionary<string, object?> { ["status"] = "FAIL", ["operationId"] = operation,
            ["tenantId"] = Alpha.Id, ["startedUtc"] = DateTimeOffset.UtcNow };
        Process? child = null;
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        try
        {
            // Allocate real pages in the existing append-only audit schema, not a probe
            // table: the production closed-table inventory must still reject unknown tables.
            // These valid JSON events survive the restore/recovery assertions unchanged.
            await using (var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct))
            await using (var fill = SqlOperations.Command(sql, """
                SET NOCOUNT ON;
                CREATE TABLE #M6RestoreAuditPayload ([Json] nvarchar(max) NOT NULL);
                INSERT INTO #M6RestoreAuditPayload ([Json])
                SELECT TOP (32768) CONVERT(nvarchar(max), N'{"drill":"')
                    + CONVERT(varchar(4096), CRYPT_GEN_RANDOM(2048), 2) + N'"}'
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b;
                SET NOCOUNT OFF;
                INSERT INTO [audit].[Events]
                    ([Id], [TenantId], [ActorId], [Action], [PayloadHash], [SecurityGeneration],
                     [ResultRevision], [OccurredAt], [CorrelationId], [ChangesJson])
                SELECT NEWID(), @tenant, @actor, 'M6RestoreCrashFixture',
                    CONVERT(varchar(64), HASHBYTES('SHA2_256', [Json]), 2), @generation,
                    1, SYSUTCDATETIME(), @correlation, [Json]
                FROM #M6RestoreAuditPayload;
                """, ("@tenant", Alpha.Id), ("@actor", setup.Http.UserId),
                ("@generation", (await CurrentAsync()).AuthorizationGeneration), ("@correlation", operation.ToString("N"))))
            { fill.CommandTimeout = 180; Assert.Equal(32768, await fill.ExecuteNonQueryAsync(Ct)); }
            var source = await BackupAsync(store);
            evidence["backupId"] = source.BackupId;
            await using (var verified = await store.OpenAsync(Alpha.Id, source.BackupId, Ct))
            {
                evidence["manifestSha256"] = verified.ManifestSha256;
                evidence["backupSha256"] = verified.Manifest.Parts[0].Sha256;
                evidence["backupLength"] = verified.Manifest.Parts[0].Length;
                Assert.True(verified.Manifest.Parts[0].Length > 200L * 1024 * 1024, "A substantial real restore is required; never skip an unobserved kill.");
            }
            await using var package = await M6ReleaseSqlTests.PackageAsync();
            evidence["sourceSha"] = package.Manifest.SourceSha;
            var report = Path.Combine(TenancyConnections.Required("SAAS_OPERATIONS_HOME"), operation.ToString("N") + ".restore.json");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = root.FullName };
            start.ArgumentList.Add(package.FilePath("host/SaaS.Web.dll"));
            foreach (var argument in new[] { "--maintenance", "restore-backup", Alpha.Id.ToString("D"), operation.ToString("D"),
                (await CurrentAsync()).RegistryVersion.ToString(CultureInfo.InvariantCulture), source.BackupId.ToString("D"), report })
                start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new InvalidOperationException("Owned restore process did not start.");
            stdout = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            stderr = child.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            evidence["processId"] = child.Id;
            evidence["processStartUtc"] = child.StartTime.ToUniversalTime();
            await using (var observer = await SqlOperations.OpenAsync(connections.Provisioner(), Ct))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                var observed = false;
                while (!child.HasExited)
                {
                    await using var query = SqlOperations.Command(observer, """
                        SELECT r.session_id, r.request_id, r.command, r.percent_complete, s.program_name
                        FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
                        WHERE s.host_process_id = @pid AND s.program_name LIKE @tag
                          AND r.command = 'RESTORE DATABASE' AND r.percent_complete < 100;
                        """, ("@pid", child.Id), ("@tag", "SaaS.Op." + operation.ToString("N") + ".%"));
                    await using (var reader = await query.ExecuteReaderAsync(deadline.Token))
                    {
                        if (await reader.ReadAsync(deadline.Token))
                        {
                            evidence["sqlSessionId"] = Convert.ToInt32(reader[0]);
                            evidence["sqlRequestId"] = Convert.ToInt32(reader[1]);
                            evidence["sqlCommand"] = Convert.ToString(reader[2]);
                            evidence["percentCompleteObserved"] = Convert.ToDouble(reader[3]);
                            evidence["operationTag"] = Convert.ToString(reader[4]);
                            evidence["observedUtc"] = DateTimeOffset.UtcNow;
                            observed = true;
                        }
                    }
                    if (observed) break;
                    await Task.Delay(10, deadline.Token);
                }
                Assert.True(observed, "No live SQL RESTORE request was observed; this rehearsal must fail, not skip or substitute a checkpoint exception.");
                Assert.False(child.HasExited, "Owned process exited before OS termination.");
                child.Kill(entireProcessTree: true);
                evidence["killRequestedUtc"] = DateTimeOffset.UtcNow;
                await child.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                evidence["processExitCode"] = child.ExitCode;
                Assert.NotEqual(0, child.ExitCode);
                Assert.False(File.Exists(report), "A completed CLI report cannot stand in for an interrupted restore.");
            }
            var closed = await CurrentAsync();
            Assert.Equal(operation, closed.OperationId);
            Assert.Equal(OperationStatus.Restoring, closed.Operation);
            Assert.True(closed.SecurityRevalidationRequired);
            Assert.NotNull(closed.OwnerId);
            var owner = closed.OwnerId.Value;
            await WaitForRestoreQuiescenceAsync(operation);
            evidence["quiescentUtc"] = DateTimeOffset.UtcNow;
            string bound;
            await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            {
                Assert.True(await platform.Audit.AnyAsync(x => x.OperationId == operation && x.Action == "PhysicalRestoreRequested", Ct));
                bound = (await platform.Audit.SingleAsync(x => x.OperationId == operation && x.Action == "PhysicalRestoreBound", Ct)).Details;
                Assert.True(bound.Contains(source.BackupId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                Assert.False(await platform.Audit.AnyAsync(x => x.OperationId == operation && x.Action == "PhysicalRestoreIdentityVerified", Ct));
            }
            var maintenance = new AccessMaintenance(connections);
            var generic = await Assert.ThrowsAsync<TenantContractException>(() => maintenance.ResumeRecoveryAsync(Alpha.Id, operation, closed.RegistryVersion, Ct));
            Assert.Equal("physical_restore_incomplete", generic.Code);
            var competing = await Assert.ThrowsAsync<TenantContractException>(() => maintenance.InstallAsync(Alpha.Id, Guid.NewGuid(), closed.RegistryVersion, Ct));
            Assert.Equal("physical_restore_incomplete", competing.Code);
            using (var denied = await ApiAsync(setup.Http, old.AccessToken, "/alpha/api/v1/orders"))
                Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable);
            var changedSource = await Assert.ThrowsAsync<TenantContractException>(() => maintenance.Operations.RestoreBackupAsync(
                Alpha.Id, operation, closed.RegistryVersion, clean.BackupId, store, true, Ct));
            Assert.Equal("backup_manifest_changed", changedSource.Code);
            // Even with the OS owner gone, a tagged SQL session must prevent takeover.
            await using (var prior = await SqlOperations.OpenAsync(TenancyConnections.ForOperation(connections.Provisioner(), operation, owner), Ct))
            {
                var active = await Assert.ThrowsAsync<TenantContractException>(() => maintenance.Operations.RestoreBackupAsync(
                    Alpha.Id, operation, closed.RegistryVersion, source.BackupId, store, true, Ct));
                Assert.Equal("prior_owner_still_active", active.Code);
            }
            await WaitForRestoreQuiescenceAsync(operation);
            var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation,
                (await CurrentAsync()).RegistryVersion, source.BackupId, store, true, Ct);
            Assert.True((await CurrentAsync()).SecurityRevalidationRequired);
            var approved = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion,
                AclDocument.Empty, "Current authority after observed OS-process termination and SQL quiescence", false, Ct);
            var completed = await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct);
            Assert.Equal(ResourceCheckpoint.Completed, completed.Checkpoint);
            await TenantDatabaseVerifier.VerifyIdentityAsync(await CurrentAsync(), Environment.Migrator(Alpha), Ct);
            await using (var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct))
            {
                var rows = Convert.ToInt32(await SqlOperations.ScalarAsync(sql, """
                    SELECT COUNT(*) FROM [audit].[Events]
                    WHERE [TenantId] = @tenant AND [Action] = 'M6RestoreCrashFixture'
                      AND [CorrelationId] = @correlation AND ISJSON([ChangesJson]) = 1
                      AND [PayloadHash] = CONVERT(varchar(64), HASHBYTES('SHA2_256', [ChangesJson]), 2);
                    """, Ct, ("@tenant", Alpha.Id), ("@correlation", operation.ToString("N"))));
                Assert.Equal(32768, rows);
                evidence["restoredAuditEvents"] = rows;
            }
            await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
                Assert.Equal(bound, (await platform.Audit.SingleAsync(x => x.OperationId == operation && x.Action == "PhysicalRestoreBound", Ct)).Details);
            evidence["completedUtc"] = DateTimeOffset.UtcNow;
            evidence["status"] = "PASS";
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                child.Dispose();
            }
            try
            {
                await WaitForRestoreQuiescenceAsync(operation);
                // Restore only the original disposable fixture, including after RESTORING.
                await using var sql = await SqlOperations.OpenAsync(connections.Provisioner(), CancellationToken.None);
                await using var cleanup = SqlOperations.Command(sql, """
                    DECLARE @ddl nvarchar(max) = CASE WHEN EXISTS (SELECT 1 FROM sys.databases WHERE name = @database AND state = 1)
                        THEN N'' ELSE N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;' END
                        + N'RESTORE DATABASE ' + QUOTENAME(@database) + N' FROM DISK = @file WITH FILE = 1, REPLACE, RECOVERY, CHECKSUM;'
                        + N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET MULTI_USER;';
                    EXEC sys.sp_executesql @ddl, N'@file nvarchar(4000)', @file = @file;
                    """, ("@database", TenancyConnections.TenantDatabase(Alpha.Id)), ("@file", FixtureSqlBackupPath(clean.BackupId)));
                cleanup.CommandTimeout = 1800;
                await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
                evidence["fixtureCleanup"] = "PASS";
            }
            catch { evidence["status"] = "FAIL"; evidence["fixtureCleanup"] = "FAIL"; throw; }
            finally
            {
                await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            }
        }
    }

    private static async Task WaitForRestoreQuiescenceAsync(Guid operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var sql = await SqlOperations.OpenAsync(new TenancyConnections().Provisioner(), deadline.Token);
        while (Convert.ToInt32(await SqlOperations.ScalarAsync(sql,
            "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name LIKE @tag", deadline.Token,
            ("@tag", "SaaS.Op." + operation.ToString("N") + ".%"))) != 0)
            await Task.Delay(50, deadline.Token);
    }
}
