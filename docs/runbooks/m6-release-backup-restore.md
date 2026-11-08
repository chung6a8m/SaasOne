# M6 controlled release and tenant backup/restore

This is a candidate operational runbook, pending real local evidence and owner review. Use disposable rehearsal data first. Tenant operations use the existing private SQL-authenticated maintenance CLI, common same-host plus SQL locks, registry CAS/fences, audit and durable write gate. No HTTP endpoint is added. CI is not required to run these commands, and is not automatically enabled.

## Release package and trust

```powershell
pwsh -NoProfile -File scripts/build-release.ps1
```

A fresh package contains a published host, six migration bundles, six idempotent SQL scripts, a context/migration catalog and a closed file-length/SHA-256 inventory. Streams are PlatformDbContext/platform, TenantDbContext/tenancy, AccessDbContext/access, AuditDbContext/audit, OrdersDbContext/orders and ApprovalsDbContext/approvals. The host owns composition; Platform has no dependency on either business module. Both plans carry all tenant schemas; feature readiness is separate from schema readiness. Orchard native migrations are not EF bundles and are invoked/verified separately.

Use a protected release directory. Obtain the expected `release.json` SHA-256 through a trusted deployment channel independently of the candidate directory. A sidecar found next to an untrusted package is **not** provenance. `scripts/tenant-release.ps1` verifies the complete closed inventory from the trusted checkout before executing the packaged host. Do not execute a host supplied by an untrusted directory merely to ask that same host whether it is trustworthy. The operator controls the trusted checkout, keys, deployment configuration, release directory and backup store; the model does not defend against a malicious DBA/root/deployment operator.

The package RID must match the x64 Windows/Linux execution host. Bundles are framework-dependent. Do not mix bundles, idempotent SQL and host binaries from different builds. No automatic down migration exists: review SQL and use a rehearsed restore/forward-fix strategy. Do not run reviewed SQL manually as a substitute for the fenced operation runner.

## Platform first, offline

Stop and drain **all** runtime/identity/customer host processes before changing platform schema. Verify no in-flight tenant operation remains. Current M6 hosts refuse requests while a platform release audit is pending; old binaries cannot enforce a new gate, so this is not a substitute for stopping them. The runner also checks runtime SQL sessions and obtains an exclusive platform schema lock against shared tenant maintenance locks.

Set deployment connection secrets as documented by earlier runbooks. Supply `SAAS_RELEASE_MANIFEST_SHA256` from the trusted channel and explicitly set `SAAS_PLATFORM_RELEASE_ACK=all-runtime-hosts-stopped-v1`. Validate package bytes with the trusted checkout helper before launching the packaged CLI:

```powershell
. ./scripts/m6-common.ps1
$proof = Open-M6Package $package $env:SAAS_RELEASE_MANIFEST_SHA256
try {
  # Unique report path outside the closed package; operationId is an explicit UUID.
  dotnet (Join-Path $package 'host/SaaS.Web.dll') --maintenance platform-release $package $operationId $reportPath
  if ($LASTEXITCODE -ne 0) { throw 'Platform release failed; keep hosts stopped.' }
} finally { Close-M6Package $proof }
```

A failed platform release leaves its append-only pending audit barrier in place. Investigate first, then use `platform-release-resume` with the **same operation ID and manifest**. Known ordered migration history prefixes may advance; unknown/newer history is rejected. Bootstrap completion is not toggled and cannot be used to bypass this gate. Platform recovery/restore and relocation are separate operations, not implemented by the tenant restore command.

The M6 platform migration widens the ResourceOperations check constraint to allow `Migrating` (4), alongside existing kinds. Its snapshot/target model are frozen in the migration stream. It does not rewrite prior migrations or delete audit history.

## Per-tenant controlled release

Prepare a JSON array containing only `tenantId`, `operationId` and `expectedRegistryVersion` for each registered tenant. Read versions deliberately from the authoritative registry. Each operation ID is unique. Example values below are placeholders, not a discovered deployment:

```json
[{"tenantId":"11111111-1111-1111-1111-111111111111","operationId":"22222222-2222-2222-2222-222222222222","expectedRegistryVersion":42}]
```

```powershell
pwsh -NoProfile -File scripts/tenant-release.ps1 -Package $package -ManifestSha256 $trustedHash -Requests ./private/release-requests.json
```

The batch continues with independent tenants, emits per-tenant JSON/log/exit/duration results and exits nonzero if any tenant failed. It never auto-reloads a conflicting registry version. Retry explicitly with `-Resume`, the same operation ID/package digest, and a reviewed current expected version. Never replace the manifest mid-operation.

Each tenant closes/drains writes, checks obligations, runs every required bundle, verifies exact EF histories, invokes real Orchard migrations, reconciles features/entitlements, verifies native state and runtime authority, and prepares/opens only its own gate. A concurrent suspend, expiry or pending deletion cannot be overwritten by completion. Failure leaves the tenant closed; other tenants can progress. A completed rerun verifies current readiness/lifecycle instead of reopening a changed tenant.

External bundles receive their operation-tagged connection **only via a restricted child environment**, never command-line arguments. Child output is drained without provider/SQL secret logging; failure exposes a stable error code. Durable launch markers block takeover even before a child opens SQL. A missing PID/start-time after a crash is deliberately ambiguous: stop/reconcile all relevant processes and prove SQL quiescence; do not delete a marker based only on elapsed time. Keep `SAAS_OPERATIONS_HOME` durable, private and on the original operation host. Cross-host takeover requires separate operator reconciliation.

## Backup source and protected store

Configure outside customer databases:

- `SAAS_BACKUP_STORE_ACK=private-shared-store-v1`.
- `SAAS_BACKUP_ROOT`: private existing directory visible to the maintenance process.
- `SAAS_SQL_BACKUP_ROOT`: absolute SQL-visible path mapping to the **same files**.
- `SAAS_BACKUP_MANIFEST_KEY`: base64-encoded 32-byte signing key, provisioned through a secret manager. Preserve the key for backup retention; loss makes these manifests unverifiable. V1 has no automatic key rotation/key-ID protocol.

Windows ACLs must restrict the root to the operator and SQL service account. Linux permissions must be private; grant only the necessary shared group/service access. The operator must not let untrusted users mutate/replace files, directories or links. Backups contain tenant data; protect/encrypt the storage separately. Do not upload backups or HMAC material as test evidence.

V1 deliberately supports **one COPY_ONLY full backup with CHECKSUM**, one `database.bak` and one SQL backup set at position 1. Differential/log/striped/mixed chains, MOVE, tenant cloning, database relocation and platform restore are rejected, not partially supported. The authenticated manifest binds TenantId, fixed database name, BackupId, BackupSetGUID/position, seal time, length and file hash. BackupSetGUID identifies the recovery point; seal time is not a point-in-time log restore promise. The external platform audit binds the sealed manifest digest.

```powershell
dotnet ./src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll --maintenance backup $tenantId $backupId $expectedVersion $newReportPath
```

Use `backup-resume` only after reconciling the same backup operation. A sealed backup is immutable; a missing/changed sealed artifact is not silently regenerated. Incomplete unsealed bytes may be recreated only after prior-owner SQL/process quiescence. Backup is gated maintenance, not an untracked asynchronous SQL command.

## Physical restore, then current authorization

```powershell
dotnet ./src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll --maintenance restore-backup $tenantId $operationId $expectedVersion $backupId $newReportPath
```

Before overwrite the workflow validates HMAC, bytes, SQL header/checksum and source-to-destination file layout. An Alpha backup cannot be offered to Delta. The first attempt also proves the registered destination's immutable identity and drains writes. A durable source intent is recorded before beginning recovery; the current platform increments authorization generation and revokes sessions/token families outside the restored DB. Generic recovery approval/resume/restart cannot skip an unverified physical restore.

After SQL overwrite, immutable TenantId/database nonce must match **before** EF migration, Orchard initialization or ACL replacement. Missing/wrong identity is a hard stop, never seeded to match. A durable identity-verified checkpoint and a freshly closed historical write gate precede security reconciliation. A crash can be retried with `restore-backup-resume`, the same operation and source binding, and an explicit reviewed registry version. A prior verified/completed physical step is not replayed over new writes. If SQL remains RESTORING, the same validated full backup may be replayed; no failure `finally` path opens traffic.

Successful physical restore still returns **Restoring + SecurityRevalidationRequired**. Review and explicitly approve the full current ACL through the existing `ops-recovery-approve` / recovery-authority workflow, then `ops-recovery-resume`. Never copy the restored historical ACL into approval merely because it is present in the backup. The current platform authority and membership checks remain mandatory. An outstanding restored approval under Standard requires separately authorized `AllowApprovalUpgrade=true`; the child upgrade does not open traffic until all restore/security checks complete. Use the ordinary approval workflow on the restored order revision afterward.

See the M3 recovery runbook for exact ACL approval arguments. Do not manually clear `SecurityRevalidationRequired`, edit an operation checkpoint, delete an intent/audit row, or reset a write gate to force readiness. Suspension, expiry and pending deletion remain authoritative. A physical restore with a lifecycle conflict needs explicit reconciliation, not an automatic retry against a new version.

## Approved replacement recovery point after a refused Approval upgrade

When a verified restore brings back outstanding approvals under the current Standard plan and the current authority has **not** approved the Approval child upgrade, do **not** edit the original BackupId, retry it with another manifest, reset Restoring, or clear the gate manually. A separate source-supersede operation is allowed only while the old physical identity is verified, the write gate is closed, no ACL apply has started, the subscription is Active/unexpired, and an enabled current-platform recovery authority explicitly accepts the data-loss scope.

Prepare a private JSON decision file (do not place the justification in command-line arguments):

~~~json
{"lossScope":"Orders and changes after the selected older full backup will be discarded","reason":"Reviewed and approved by the current platform recovery authority"}
~~~

~~~powershell
dotnet ./src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll --maintenance restore-backup-supersede $tenantId $priorRestoreOperationId $newRestoreOperationId $expectedRegistryVersion $replacementBackupId ./private/supersede-decision.json $newReportPath
# Read RegistryVersion from the returned PENDING_PHYSICAL_RESTORE report:
dotnet ./src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll --maintenance restore-backup-resume $tenantId $newRestoreOperationId $returnedRegistryVersion $replacementBackupId $secondReportPath
~~~

The supersede transaction makes the old operation terminal as **superseded**, never successful; increments RegistryVersion/fence and security generations; revokes remaining sessions/families; audits old/new backup hashes plus the approval and loss scope; and persists the new physical-restore source intent atomically. It **does not restore SQL or open traffic**. The replacement must pass the pre-overwrite HMAC/SQL/layout checks and post-overwrite tenant identity check. A normal restore retry remains bound to its original immutable source. Once the replacement physical restore is verified, approve a fresh ACL from current platform authority and complete the recovery with the **new** operation ID. Failure or missing authorization keeps the tenant closed.

