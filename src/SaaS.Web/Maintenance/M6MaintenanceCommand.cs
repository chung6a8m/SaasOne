using System.Globalization;
using System.Text.Json;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Tenancy.Release;
using SaaS.Web.Access;
using SaaS.Web.Release;
namespace SaaS.Web.Maintenance;

internal static class M6MaintenanceCommand
{
    public static bool Handles(string command) => command is "release-catalog" or "release-check" or "release-apply" or "release-resume"
        or "release-verify" or "platform-release" or "platform-release-resume" or "backup" or "backup-resume"
        or "restore-backup" or "restore-backup-resume" or "restore-backup-supersede";
    public static async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        var connections = new TenancyConnections();
        if (args is ["release-catalog", var runtime, var inventory])
        { await WriteAsync(inventory, ReleaseCatalog.Describe(runtime), ct); return; }
        if (args.Length >= 2 && args[0].StartsWith("release-", StringComparison.Ordinal)
            || args.Length >= 2 && args[0].StartsWith("platform-release", StringComparison.Ordinal))
        {
            await using var package = await ReleasePackage.OpenAsync(args[1], TenancyConnections.Required("SAAS_RELEASE_MANIFEST_SHA256"), ct);
            ReleaseCatalog.DemandCompatible(package);
            if (args is ["release-check", _, var checkReport])
                await WriteAsync(checkReport, new { status = "PASS", manifestSha256 = package.ManifestSha256, package.Manifest.SourceSha }, ct);
            else if (args is ["release-verify", _, var verifyTenant, var verifyOutput])
            {
                await new ReleaseMaintenance(connections, package).VerifyAsync(Id(verifyTenant), ct);
                await WriteAsync(verifyOutput, new { status = "PASS", tenantId = Id(verifyTenant), manifestSha256 = package.ManifestSha256 }, ct);
            }
            else if (args is [var action, _, var releaseTenant, var releaseOperation, var releaseVersion, var outputFile] && action is "release-apply" or "release-resume")
                await WriteAsync(outputFile, await new ReleaseMaintenance(connections, package).ApplyAsync(Id(releaseTenant), Id(releaseOperation), Version(releaseVersion), action == "release-resume", ct), ct);
            else if (args is [var platformAction, _, var platformOperation, var platformOutput] && platformAction is "platform-release" or "platform-release-resume")
                await WriteAsync(platformOutput, await new PlatformReleaseMaintenance(connections, package).ApplyAsync(Id(platformOperation), platformAction.EndsWith("-resume", StringComparison.Ordinal), ct), ct);
            else throw new TenantContractException("invalid_maintenance_command");
            return;
        }
        using var store = new SqlBackupStore();
        var operations = new AccessMaintenance(connections).Operations;
        if (args is [var backupAction, var tenant, var backup, var version, var output] && backupAction is "backup" or "backup-resume")
            await WriteAsync(output, await operations.BackupAsync(Id(tenant), Id(backup), Version(version), store, backupAction == "backup-resume", ct), ct);
        else if (args is [var restoreAction, var restoreTenant, var operation, var expected, var backupId, var report]
            && restoreAction is "restore-backup" or "restore-backup-resume")
            await WriteAsync(report, await operations.RestoreBackupAsync(Id(restoreTenant), Id(operation), Version(expected), Id(backupId), store,
                restoreAction == "restore-backup-resume", ct), ct);
        else if (args is ["restore-backup-supersede", var sourceTenant, var priorOperation, var newOperation,
                 var sourceVersion, var newBackup, var decisionFile, var supersedeReport])
        {
            var file = new FileInfo(Path.GetFullPath(decisionFile));
            if (!file.Exists || file.Length is < 1 or > 8192)
                throw new TenantContractException("recovery_supersede_decision_invalid");
            RestoreSupersedeDecision decision;
            try
            {
                decision = JsonSerializer.Deserialize<RestoreSupersedeDecision>(
                    await File.ReadAllBytesAsync(file.FullName, ct), ReleasePackage.Json)
                    ?? throw new JsonException();
            }
            catch (JsonException) { throw new TenantContractException("recovery_supersede_decision_invalid"); }
            await WriteAsync(supersedeReport, await operations.SupersedeRecoverySourceAsync(
                Id(sourceTenant), Id(priorOperation), Id(newOperation), Version(sourceVersion),
                Id(newBackup), store, decision, ct), ct);
        }
        else throw new TenantContractException("invalid_maintenance_command");
    }
    private static Guid Id(string text) => Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty
        ? value : throw new TenantContractException("invalid_operation_id");
    private static long Version(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
        ? value : throw new TenantContractException("invalid_registry_version");
    private static async Task WriteAsync(string path, object value, CancellationToken ct)
    {
        // Unique output names are mandatory; do not accidentally overwrite older evidence or secrets.
        await using var output = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, value, ReleasePackage.Json, ct);
        await output.FlushAsync(ct); output.Flush(flushToDisk: true);
    }
}
