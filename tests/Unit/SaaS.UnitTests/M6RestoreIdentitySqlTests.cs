using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public Task Signed_backup_with_missing_database_identity_never_runs_recovery_work()
        => RejectSignedIdentityAsync("missing");

    [Fact]
    public Task Signed_backup_with_wrong_database_TenantId_never_runs_recovery_work()
        => RejectSignedIdentityAsync("tenant");

    [Fact]
    public Task Signed_backup_with_wrong_database_nonce_never_runs_recovery_work()
        => RejectSignedIdentityAsync("nonce");

    private async Task RejectSignedIdentityAsync(string corruption)
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync();
        await AuthorityAsync();
        using var store = new SqlBackupStore();
        var clean = await BackupAsync(store);
        try
        {
            var badId = await CreateSignedInvalidIdentityBackupAsync(corruption);
            var before = await CurrentAsync();
            await TenantDatabaseVerifier.VerifyIdentityAsync(before, Environment.Migrator(Alpha), Ct);
            // Prove the negative case is NOT an HMAC/header/checksum/layout failure.
            await using (var verified = await store.OpenAsync(Alpha.Id, badId, Ct))
            {
                Assert.Equal(Alpha.Id, verified.Manifest.TenantId);
                await store.VerifySqlAsync(verified, new TenancyConnections().Provisioner(), Ct);
            }
            var plan = DispatchProxy.Create<IPlanReconciler, M6ForbiddenRecoveryWork>();
            var obligations = DispatchProxy.Create<IApprovalObligationReader, M6ForbiddenRecoveryWork>();
            var acl = DispatchProxy.Create<IRecoveryAclApplier, M6ForbiddenRecoveryWork>();
            var operations = new TenantOperations(new(), plan, obligations);
            var stages = new List<PhysicalRestoreCheckpoint>();
            var operation = Guid.NewGuid();
            await Assert.ThrowsAsync<TenantContractException>(() => operations.RestoreBackupAsync(
                Alpha.Id, operation, before.RegistryVersion, badId, store, false, Ct,
                (stage, _) => { stages.Add(stage); return Task.CompletedTask; }));
            Assert.Contains(PhysicalRestoreCheckpoint.DatabaseRestored, stages);
            Assert.DoesNotContain(PhysicalRestoreCheckpoint.IdentityVerified, stages);
            var closed = await CurrentAsync();
            Assert.Equal(operation, closed.OperationId);
            Assert.Equal(OperationStatus.Restoring, closed.Operation);
            Assert.True(closed.SecurityRevalidationRequired);
            Assert.True(closed.AuthorizationGeneration > before.AuthorizationGeneration);
            var denied = await Assert.ThrowsAsync<TenantContractException>(() => operations.ResumeRecoveryAsync(
                Alpha.Id, operation, closed.RegistryVersion, acl, Ct));
            Assert.Equal("physical_restore_incomplete", denied.Code);
            Assert.Empty(((M6ForbiddenRecoveryWork)(object)plan).Calls);
            Assert.Empty(((M6ForbiddenRecoveryWork)(object)obligations).Calls);
            Assert.Empty(((M6ForbiddenRecoveryWork)(object)acl).Calls);
            await using (var customer = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha)))
            {
                var identities = await customer.Identity.AsNoTracking().ToArrayAsync(Ct);
                if (corruption == "missing") Assert.Empty(identities); // No repair/seed after restore.
                else
                {
                    var identity = Assert.Single(identities);
                    if (corruption == "tenant") Assert.NotEqual(Alpha.Id, identity.TenantId);
                    else Assert.NotEqual(before.DatabaseNonce, identity.DatabaseNonce);
                }
            }
            await using (var platform = TenancyConnections.PlatformContext(Environment.PlatformAdmin))
            {
                // Assert the external audit contract, not inaccessible implementation constants.
                Assert.True(await platform.Audit.AnyAsync(x => x.OperationId == operation
                    && x.Action == "PhysicalRestoreBound", Ct));
                Assert.False(await platform.Audit.AnyAsync(x => x.OperationId == operation
                    && (x.Action == "PhysicalRestoreIdentityVerified" || x.Action == "RecoveryAclApplyStarted"
                        || x.Action == "RecoveryCompleted"), Ct));
                var row = await platform.ResourceOperations.AsNoTracking().SingleAsync(x => x.Id == operation, Ct);
                Assert.True(row.Checkpoint <= ResourceCheckpoint.GateClosed);
                Assert.Null(row.CompletedAt);
            }
        }
        finally
        {
            // Privileged teardown of this disposable fixture ONLY, after all evidence assertions.
            // Restore a genuine clean backup, never seed/repair the failed restore's identity.
            await RestoreFixtureBackupAsync(clean.BackupId);
        }
    }

    private string FixtureSqlBackupPath(Guid backupId)
    {
        var root = TenancyConnections.Required("SAAS_SQL_BACKUP_ROOT").TrimEnd('/', '\\');
        var separator = root.StartsWith('/') ? "/" : "\\";
        return root + separator + backupId.ToString("N") + separator + "database.bak";
    }

    private async Task<Guid> CreateSignedInvalidIdentityBackupAsync(string corruption)
    {
        TenantDatabaseIdentity original;
        await using (var db = TenancyConnections.CustomerContext(Environment.CustomerAdmin(Alpha)))
            original = await db.Identity.AsNoTracking().SingleAsync(Ct);
        var backupId = Guid.NewGuid();
        var directory = Path.Combine(TenancyConnections.Required("SAAS_BACKUP_ROOT"), backupId.ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        var dataPath = Path.Combine(directory, "database.bak");
        await using (var output = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            output.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dataPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        await using var customer = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        try
        {
            // Test-admin boundary: manufacture a trusted artifact whose payload identity is bad.
            // The immutable trigger is ENABLED in the backup; production has no repair switch.
            await SqlOperations.ExecuteAsync(customer,
                "DISABLE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity] ON [tenancy].[Identity];", Ct);
            var sql = corruption switch
            {
                "missing" => "DELETE FROM [tenancy].[Identity];",
                "tenant" => "UPDATE [tenancy].[Identity] SET [TenantId] = @wrong;",
                "nonce" => "UPDATE [tenancy].[Identity] SET [DatabaseNonce] = @wrong;",
                _ => throw new ArgumentOutOfRangeException(nameof(corruption))
            };
            await SqlOperations.ExecuteAsync(customer, sql, Ct, ("@wrong", Guid.NewGuid()));
            await SqlOperations.ExecuteAsync(customer,
                "ENABLE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity] ON [tenancy].[Identity];", Ct);
            await using var backup = SqlOperations.Command(customer, """
                DECLARE @ddl nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(@database)
                    + N' TO DISK = @file WITH COPY_ONLY, INIT, CHECKSUM';
                EXEC sys.sp_executesql @ddl, N'@file nvarchar(4000)', @file = @file;
                """, ("@database", TenancyConnections.TenantDatabase(Alpha.Id)), ("@file", FixtureSqlBackupPath(backupId)));
            backup.CommandTimeout = 1800;
            await backup.ExecuteNonQueryAsync(Ct);
        }
        finally
        {
            // Reestablish the live fixture before calling the production restore coordinator.
            await SqlOperations.ExecuteAsync(customer, """
                DISABLE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity] ON [tenancy].[Identity];
                DELETE FROM [tenancy].[Identity];
                INSERT INTO [tenancy].[Identity] ([Id], [TenantId], [DatabaseNonce], [InitialOperationId], [CreatedAt])
                VALUES (1, @tenant, @nonce, @operation, @created);
                ENABLE TRIGGER [tenancy].[ImmutableTenantDatabaseIdentity] ON [tenancy].[Identity];
                """, Ct, ("@tenant", original.TenantId), ("@nonce", original.DatabaseNonce),
                ("@operation", original.InitialOperationId), ("@created", original.CreatedAt));
        }
        Guid backupSet;
        await using (var header = SqlOperations.Command(customer, "RESTORE HEADERONLY FROM DISK = @file", ("@file", FixtureSqlBackupPath(backupId))))
        await using (var reader = await header.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.True(Convert.ToBoolean(reader["HasBackupChecksums"]));
            Assert.True(Convert.ToBoolean(reader["IsCopyOnly"]));
            backupSet = (Guid)reader["BackupSetGUID"];
            Assert.False(await reader.ReadAsync(Ct));
        }
        long length;
        string digest;
        await using (var data = File.OpenRead(dataPath))
        {
            length = data.Length;
            digest = Convert.ToHexString(await SHA256.HashDataAsync(data, Ct));
        }
        var manifest = new BackupManifest(1, Alpha.Id, backupId, TenancyConnections.TenantDatabase(Alpha.Id),
            DateTimeOffset.UtcNow, [new("database.bak", length, digest, backupSet, 1)]);
        var key = Convert.FromBase64String(TenancyConnections.Required("SAAS_BACKUP_MANIFEST_KEY"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), BackupManifestCodec.Sign(manifest, key), Ct);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return backupId;
    }

    private async Task RestoreFixtureBackupAsync(Guid backupId)
    {
        // This connection addresses only the registered disposable Alpha database.
        await using var server = await SqlOperations.OpenAsync(new TenancyConnections().Provisioner(), CancellationToken.None);
        await using var restore = SqlOperations.Command(server, """
            DECLARE @ddl nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database)
                + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE ' + QUOTENAME(@database)
                + N' FROM DISK = @file WITH FILE = 1, REPLACE, RECOVERY, CHECKSUM; ALTER DATABASE '
                + QUOTENAME(@database) + N' SET MULTI_USER;';
            EXEC sys.sp_executesql @ddl, N'@file nvarchar(4000)', @file = @file;
            """, ("@database", TenancyConnections.TenantDatabase(Alpha.Id)), ("@file", FixtureSqlBackupPath(backupId)));
        restore.CommandTimeout = 1800;
        await restore.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

/// <summary>Test spy: any migration, Orchard, obligation or ACL action fails immediately.</summary>
public class M6ForbiddenRecoveryWork : DispatchProxy
{
    public List<string> Calls { get; } = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls.Add(targetMethod?.Name ?? "unknown");
        throw new InvalidOperationException("Recovery work ran before immutable database identity was verified.");
    }
}
