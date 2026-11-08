# M6 Issue #10 acceptance rehearsals

This supplements `m6-release-backup-restore.md`; it is not production sign-off. Keep Issue #10 open, M6 unaccepted and M7 acceptance blocked until all five gates have reviewed PASS evidence. PR #11 adds the rehearsals. An added test, a started workflow, a component-only pass or the historical 14-fact result is insufficient.

## Real SQL and HTTP gates

Run a clean checkout with the pinned SDK, PowerShell and Docker:

```powershell
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
```

The existing entry point preserves all M0–M5 gates, builds the immutable release package and executes the SqlM6 fixture. `scripts/m6-mandatory-cases.json` now names 20 facts; every name must be discovered and executed exactly once with no failure or skip. Loop variants in a fact do not inflate the fact count. Review TRX, `m6-poc.json`, `m6-acceptance.json`, package/backup manifests and the checksummed local-verification ZIP at the tested source SHA.

| Gate | Mandatory rehearsal | Required observations |
|---|---|---|
| G1 | `Killed_OS_process_during_observed_SQL_RESTORE_requires_quiescence_and_same_source_retry` | Real owned CLI PID/start time; DMV `RESTORE DATABASE`, session/request and progress before kill; nonzero OS exit; absent completion report; SQL quiescence; durable requested/bound audit; closed registry; wrong source and live prior-owner SQL refused; same-source retry and explicit current ACL complete. Inspect `m6-restore-process.json`; FAIL or absent evidence blocks closure. |
| G2 | Three `Signed_backup_with_*_database_*` identity facts | Correct HMAC/target/header/checksum and completed physical overwrite, then missing singleton/wrong TenantId/wrong nonce refusal; no IdentityVerified audit, migration/native/ACL spy calls or completed recovery; Restoring/SecurityRevalidationRequired remains. Test-admin identity manipulation is fixture fabrication only, never a repair procedure. |
| G3 | `Offline_platform_release_blocks_live_sessions_pending_HTTP_and_tenant_work_then_resumes_once` | Owned runtime hosts stopped and awaited; explicit runtime SQL session refusal; tenant lease excludes platform release; actual coordinator persists pending audit on unknown-history refusal; real host returns 503 and refuses tenant work; same-operation resume with verified package; repeat application is idempotent. Removing the fixture-only injected history row is not a production unknown-history recovery recipe. |
| G4 | `Physical_restore_rejects_old_refresh_cookie_and_JWT_after_recovery_without_revoking_Beta` | A separate Alpha family is refused while pending, leaving the original refresh token untouched until the post-recovery assertion. Old Alpha refresh/cookie/JWT remain rejected, current-ACL fresh login/refresh cannot recover historical permissions, membership stays active, Beta cookie/API/refresh remain usable and generation/version unchanged. |

G1 deliberately fails when no in-flight restore can be observed; do not convert this to a successful skip or replace it with a checkpoint exception. A killed CLI can leave SQL running. Preserve the platform barrier, process identity, signed source and audit until both process and SQL are proven quiescent. Never clear an ambiguous marker because enough time has passed.

The G3 host inventory covers the owned disposable fixture processes only. For an actual deployment, enumerate and stop **every** runtime replica and verify its exit before using `SAAS_PLATFORM_RELEASE_ACK`. A runtime-session count of zero does not prove that idle hosts have stopped. Older binaries might not implement the pending-audit request barrier.

## Windows/Linux protected-storage drill

The workflow `M6 Windows and Linux storage rehearsal` runs independent Windows Server 2022 and Ubuntu 22.04 jobs with repository-pinned .NET and PowerShell 7.6.9. On a disposable elevated Windows host or a disposable Linux host with passwordless sudo:

```powershell
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build src/SaaS.Web/SaaS.Web.csproj --configuration Release --no-restore
pwsh -NoProfile -File scripts/check-powershell-syntax.ps1
pwsh -NoProfile -File scripts/m6-storage-drill.ps1 -DisposableHostAcknowledged
```

Check each command's exit code; stop on failure. Never run the drill against deployment paths. It creates uniquely named local fixture principals and temporary directories, then removes only those owned resources. A cleanup failure fails the drill.

The positive control first proves that each restricted principal can read/write a public fixture. The protected directory must allow the operator and SQL fixture principal, deny the unauthorized principal, and deny **both** restricted principals access to the signing-key directory. Review Windows SDDL or Linux mode evidence, not just successful operator access.

The key-retention case rejects a wrong key and a missing key, then reopens the authenticated historical artifact with the retained original key. No key material, account password or environment dump is exported. This artifact is explicitly a byte fixture, not a physical SQL backup; G1/G2 and the existing SqlM6 suite supply physical-backup evidence. V1 has one configured HMAC key and no automatic key-ID/rotation protocol. Retain the matching protected key throughout the backup retention/recovery period; an arbitrary replacement key cannot authenticate retained backups.

The marker case invokes the actual production writer, kills its owned OS process after durable intent creation, reopens in a fresh process and proves an ambiguous no-PID marker blocks automatic takeover and remains on disk. The drill writer launches no mutating child or SQL connection, so its final fixture cleanup is an explicitly bounded reconciliation. Production reconciliation additionally requires child process and tagged SQL-session evidence as documented in the main runbook. A process-kill rehearsal **does not** establish power-loss, directory-fsync, storage-controller or network-filesystem guarantees.

Review `m6-storage-drill.json` and `.sha256` from **both OS jobs**, including source SHA, OS/tool versions, principal controls, key-retention result, process/start/kill data, fresh-reader marker hash and cleanup result. `test-m6-marker-writer.ps1` is a separate secret-free diagnostic regression; its success cannot replace the complete storage report.

## Sign-off record and M7 handoff

Record the exact reviewed PR head and tested merge-tree SHA, workflow/run attempt and artifact IDs/checksums, commands, 20-fact inventory, G1 process report, both storage reports and any sanitized failure history in Issue #10. Reviewers must explicitly distinguish fixture evidence from deployment approval. A pending, failed, cancelled, expired/missing or unreviewed artifact is not PASS. Retain reviewed artifacts outside the short-lived CI store before expiration.

The authoring container has no runtime tools, so its source edits cannot supply local runtime evidence. The release baseline and tests must be checked in CI; reviewer decision remains separate from automated gate status. Update the issue checklist only from reviewed artifacts. Merge/close M6 and begin M7 acceptance only after that record covers G1–G5 with no unresolved failure. Do not mark production-ready merely because the code has merged.
