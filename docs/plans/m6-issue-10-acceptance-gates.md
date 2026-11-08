# M6 Issue #10 acceptance gates

Status: IN PROGRESS, NOT accepted. Specification: Issue #10, roadmap revision 1.3 §§5.3–5.6 and M6, ADR 0005. M7 acceptance depends on closure of all five gates, not on merging this branch.

## Constraints and approach

Extend the existing real-SQL M6 suite and evidence pipeline; do not introduce another recovery implementation. Use an isolated branch and disposable fixture databases only. No migration or schema-owner changes are planned. Platform remains authoritative for recovery approval, identity generations, operation ownership and audit. Existing 14-fact CI evidence is historical and does not prove the five missing rehearsals.

The editing container has no dotnet, pwsh, Docker or SQL Server, and cannot clone GitHub over its network. Repository access and CI are through the GitHub connector. Local RED/GREEN execution is BLOCKED; new source is not execution evidence. CI outcomes must be inspected at an exact SHA before claiming a gate passes.

## Work packages

### G1 — OS-process death during SQL RESTORE

Affected area: M6 SQL process tests, private maintenance CLI, OperationLease and SqlBackupStore. Consume the existing restore CLI, durable Requested/Bound audit and prior-owner SQL checks. Observe an actual RESTORE DATABASE request through SQL Server DMVs before terminating the owned OS process; a checkpoint exception, ALTER DATABASE wait or completed restore is not sufficient. Record PID/start time, SQL session/request, operation and backup identity, source SHA, timestamps and quiescence. Assert fail-closed registry, immutable source binding, no competing operation/traffic, and successful authorized retry only after quiescence. Never add production bypass switches to manufacture this proof.

- [ ] Add and execute the live-process rehearsal with bounded failure, never successful skip.
- [ ] Review sanitized process/SQL/audit evidence and retry outcome.

### G2 — Validly signed backup with invalid database identity

Files: `tests/Unit/SaaS.UnitTests/M6RestoreIdentitySqlTests.cs`, `scripts/m6-mandatory-cases.json`.
Consume SqlBackupStore.OpenAsync/VerifySqlAsync, BackupManifestCodec.Sign, TenantOperations.RestoreBackupAsync and the existing M6 fixture. A privileged **test-only** artifact builder creates real COPY_ONLY/CHECKSUM backups containing missing singleton, wrong TenantId or wrong nonce, with a correct target TenantId and valid HMAC in the external manifest. Restore live source identity before invoking production restore. Prove the artifact reaches DatabaseRestored, never IdentityVerified, never generic migration/native/ACL recovery, and leaves Restoring/SecurityRevalidationRequired. Clean up only the owned disposable fixture; do not add an application repair/seed path.

- [ ] Write three mandatory negative tests and run against real SQL Server.
- [ ] Verify external audit, registry and immutable restored identity; retain TRX.

### G3 — Offline platform release

Affected area: PlatformReleaseMaintenance, PlatformReleaseRuntimeGate and M6 SQL/HTTP tests. Consume the real release package and coordinator. Prove runtime-session refusal, a durable pending audit barrier producing HTTP 503, competing tenant operations denied, unknown migration history fail-closed, successful offline application, resume and idempotence. A manually inserted audit row or an in-memory-only test is not a substitute for the full coordinator rehearsal. Runtime-host stop/start inventory must be recorded separately from the operator acknowledgement.

- [ ] Add and execute coordinator/session/concurrency/HTTP rehearsals.
- [ ] Review host inventory and SQL/session evidence.

### G4 — Old HTTP credentials after physical restore

Files: `tests/Unit/SaaS.UnitTests/M6RestoreHttpSqlTests.cs` (or focused partial test file), mandatory case inventory.
Consume the existing real physical backup/restore, HttpBrowser.RefreshAsync, current-platform ACL approval and fresh-login flow. Preserve old Alpha cookie/JWT/refresh and independent Beta credentials before restore. Use two Alpha refresh families so refusal while recovery is pending cannot itself invalidate the credential used for the post-recovery assertion. Assert exact OAuth invalid_grant, no returned tokens, old cookie/JWT rejection, fresh-login current ACL, and Beta API/cookie/refresh unaffected. Keep membership active.

- [ ] Write and execute the HTTP regression with real SQL restore.
- [ ] Review credential-safe TRX/logs and current-ACL assertions.

### G5 — Protected storage and operator crash drill

Files: M6 release/restore runbook, implementation status and acceptance evidence ledger.
Review Windows ACLs and Linux ownership/modes, operations marker write/flush/reopen/crash semantics, ambiguous/live marker reconciliation and signing-key retention. Record actual OS/filesystem, principals, negative unauthorized access, process results and key-version metadata (never key material). Process kill is not a power-loss durability test. Do not delete an ambiguous marker or recreate identity to make recovery succeed.

- [ ] Execute Windows and Linux storage/marker drills and key-retention recovery.
- [ ] Record reviewer decision and update runbook from observed outcomes.

## Commands and closure

Run from a clean checkout: `pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full`. Inspect mandatory case names, discovered/executed/passed/failed/skipped counts, exit codes and artifact SHA-256. Missing, duplicate, zero or skipped mandatory tests fail the gate. Attach run ID, tested source/merge-tree SHA, sanitized logs/TRX/audit and reviewer decision to Issue #10. Runbook steps or a started workflow are not evidence of a successful rehearsal. Keep Issue #10 open, M6 unaccepted and M7 acceptance blocked until every work package has reviewed PASS evidence.
