using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public async Task Physical_checkpoint_retries_preserve_source_identity_and_require_explicit_ACL()
    {
        foreach (var crash in new[] { PhysicalRestoreCheckpoint.Bound, PhysicalRestoreCheckpoint.DatabaseRestored, PhysicalRestoreCheckpoint.IdentityVerified })
        {
            await using var setup = await new M4HttpTests(fixture).HttpAsync(); await AuthorityAsync();
            using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
            var maintenance = new AccessMaintenance(new()); var operation = Guid.NewGuid();
            var before = await CurrentAsync();
            await Assert.ThrowsAsync<IOException>(() => maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation,
                before.RegistryVersion, backup.BackupId, store, false, Ct,
                (stage, _) => stage == crash ? throw new IOException("M6 physical crash") : Task.CompletedTask));
            var current = await CurrentAsync(); Assert.True(current.SecurityRevalidationRequired);
            var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation, current.RegistryVersion, backup.BackupId, store, true, Ct);
            await TenantDatabaseVerifier.VerifyIdentityAsync(await CurrentAsync(), Environment.Migrator(Alpha), Ct);
            var approved = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion, AclDocument.Empty, "Current authority after retry", false, Ct);
            await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct);
        }
    }

    [Fact]
    public async Task Forged_manifest_or_changed_backup_bytes_fail_before_registry_or_database_overwrite()
    {
        await using var setup = await new M4HttpTests(fixture).HttpAsync();
        using var store = new SqlBackupStore(); var backup = await BackupAsync(store); var before = await CurrentAsync();
        var folder = Path.Combine(TenancyConnections.Required("SAAS_BACKUP_ROOT"), backup.BackupId.ToString("N"));
        var manifestPath = Path.Combine(folder, "manifest.json"); var original = await File.ReadAllBytesAsync(manifestPath, Ct);
        try
        {
            await File.WriteAllTextAsync(manifestPath, System.Text.Encoding.UTF8.GetString(original).Replace(Alpha.Id.ToString("D"), Environment.Tenants[1].Id.ToString("D"), StringComparison.OrdinalIgnoreCase), Ct);
            await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).Operations.RestoreBackupAsync(Alpha.Id, Guid.NewGuid(), before.RegistryVersion, backup.BackupId, store, false, Ct));
        }
        finally { await File.WriteAllBytesAsync(manifestPath, original, Ct); }
        var dataPath = Path.Combine(folder, "database.bak");
        byte first;
        await using (var file = File.Open(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { first = (byte)file.ReadByte(); file.Position = 0; file.WriteByte((byte)(first ^ 1)); file.Flush(true); }
        try
        {
            await Assert.ThrowsAsync<TenantContractException>(() => new AccessMaintenance(new()).Operations.RestoreBackupAsync(Alpha.Id, Guid.NewGuid(), before.RegistryVersion, backup.BackupId, store, false, Ct));
        }
        finally
        {
            await using var file = File.Open(dataPath, FileMode.Open, FileAccess.Write, FileShare.None); file.WriteByte(first); file.Flush(true);
        }
        Assert.Equal(before.RegistryVersion, (await CurrentAsync()).RegistryVersion);
        await TenantDatabaseVerifier.VerifyIdentityAsync(before, Environment.Migrator(Alpha), Ct);
    }
}
