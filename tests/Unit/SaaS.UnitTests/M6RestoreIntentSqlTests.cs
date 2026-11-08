using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
using SaaS.AccessControl;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M6BackupSqlTests
{
    [Fact]
    public async Task Durable_intent_survives_crashes_before_and_after_recovery_claim_without_bypass()
    {
        foreach (var crash in new[] { PhysicalRestoreCheckpoint.Requested, PhysicalRestoreCheckpoint.RecoveryBegun })
        {
            await using var setup = await new M4HttpTests(fixture).HttpAsync(); await AuthorityAsync();
            using var store = new SqlBackupStore(); var backup = await BackupAsync(store);
            var before = await CurrentAsync(); var operation = Guid.NewGuid(); var maintenance = new AccessMaintenance(new());
            await Assert.ThrowsAsync<IOException>(() => maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation,
                before.RegistryVersion, backup.BackupId, store, false, Ct, (stage, _) =>
                    stage == crash ? throw new IOException("M6 intent checkpoint fault") : Task.CompletedTask));
            var current = await CurrentAsync();
            if (crash == PhysicalRestoreCheckpoint.RecoveryBegun)
            {
                Assert.True(current.SecurityRevalidationRequired);
                Assert.Equal("physical_restore_incomplete", (await Assert.ThrowsAsync<TenantContractException>(() =>
                    maintenance.ApproveAsync(Alpha.Id, operation, current.RegistryVersion, AclDocument.Empty, "Cannot bypass requested physical restore", false, Ct))).Code);
            }
            else Assert.Equal(OperationStatus.None, current.Operation);
            var restored = await maintenance.Operations.RestoreBackupAsync(Alpha.Id, operation, current.RegistryVersion,
                backup.BackupId, store, true, Ct);
            var approved = await maintenance.ApproveAsync(Alpha.Id, operation, restored.RegistryVersion,
                AclDocument.Empty, "Explicit current replacement after physical restore", false, Ct);
            Assert.Equal(ResourceCheckpoint.Completed, (await maintenance.ResumeRecoveryAsync(Alpha.Id, operation, approved.RegistryVersion, Ct)).Checkpoint);
        }
    }
}
