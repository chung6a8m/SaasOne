# M6 Issue #10 acceptance gates

Status: **technical work packages G1–G5 completed and evidence reviewed PASS at head `9208cc7052ddbe194a05886842650dd0d447af90` / tested merge `5fe8d07d414c33c8d6f6f3c583faf56b5e066112`; owner review/integration/M6 sign-off pending.** Specification: Issue #10, roadmap revision 1.3 §§5.3–5.6 and M6, ADR 0005. M7 acceptance depends on M6 closure, not merely on checked technical tasks or merging this branch. See [the exact-source review](../verification/m6-issue-10-evidence-review.md).

## Constraints and approach

Extend the existing real-SQL M6 suite and evidence pipeline; do not introduce another recovery implementation. Use an isolated branch and disposable fixture databases only. No migration or schema-owner changes are planned. Platform remains authoritative for recovery approval, identity generations, operation ownership and audit. Existing 14-fact CI evidence is historical and does not prove the five missing rehearsals.

The editing container has no dotnet, pwsh, Docker or SQL Server, and cannot clone GitHub over its network. Repository access and CI are through the GitHub connector. Local runtime RED/GREEN execution is BLOCKED; source is not execution evidence. Runtime outcomes below come from the exact-source CI artifacts, downloaded and checked separately. The documentation-only closeout does not relabel those results as a run on its later commit.

## Work packages

### G1 — OS-process death during SQL RESTORE

Affected area: M6 SQL process tests, private maintenance CLI, OperationLease and SqlBackupStore. Consume the existing restore CLI, durable Requested/Bound audit and prior-owner SQL checks. Observe an actual RESTORE DATABASE request through SQL Server DMVs before terminating the owned OS process; a checkpoint exception, ALTER DATABASE wait or completed restore is not sufficient. Record PID/start time, SQL session/request, operation and backup identity, source SHA, timestamps and quiescence. Assert fail-closed registry, immutable source binding, no competing operation/traffic, and successful authorized retry only after quiescence. Never add production bypass switches to manufacture this proof.

- [x] Add and execute the live-process rehearsal with bounded failure, never successful skip.
- [x] Review sanitized process/SQL/audit evidence and retry outcome: full run `42472314240`, PID 86327, SQL RESTORE session 74/request 0, exit 137, quiescence and same-source recovery; 32,768 restored audit events.

### G2 — Validly signed backup with invalid database identity

Files: `tests/Unit/SaaS.UnitTests/M6RestoreIdentitySqlTests.cs`, `scripts/m6-mandatory-cases.json`.
Consume SqlBackupStore.OpenAsync/VerifySqlAsync, BackupManifestCodec.Sign, TenantOperations.RestoreBackupAsync and the existing M6 fixture. A privileged test-only artifact builder creates real COPY_ONLY/CHECKSUM backups containing missing singleton, wrong TenantId or wrong nonce, with a correct target TenantId and valid HMAC in the external manifest. Restore live source identity before invoking production restore. Prove the artifact reaches DatabaseRestored, never IdentityVerified, never generic migration/native/ACL recovery, and leaves Restoring/SecurityRevalidationRequired. Clean up only the owned disposable fixture; do not add an application repair/seed path.

- [x] Write three mandatory negative tests and run against real SQL Server.
- [x] Verify external audit, registry and immutable restored identity; retain TRX. All three exact cases pass in the current full artifact.

### G3 — Offline platform release

Affected area: PlatformReleaseMaintenance, PlatformReleaseRuntimeGate and M6 SQL/HTTP tests. Consume the real release package and coordinator. Prove runtime-session refusal, a durable pending audit barrier producing HTTP 503, competing tenant operations denied, unknown migration history fail-closed, successful offline application, resume and idempotence. A manually inserted audit row or an in-memory-only test is not a substitute for the full coordinator rehearsal. Runtime-host stop/start inventory must be distinguished from the operator acknowledgement.

- [x] Add and execute coordinator/session/concurrency/HTTP rehearsals.
- [x] Review owned-host stop/start and SQL/session assertions against the passing coordinator fact. This covers disposable fixture hosts, not production fleet inventory.

### G4 — Old HTTP credentials after physical restore

Files: focused `tests/Unit/SaaS.UnitTests/M6RestoreRefreshSqlTests.cs`, mandatory case inventory.
Consume the existing real physical backup/restore, HttpBrowser.RefreshAsync, current-platform ACL approval and fresh-login flow. Preserve old Alpha cookie/JWT/refresh and independent Beta credentials before restore. Use two Alpha refresh families so refusal while recovery is pending cannot itself invalidate the credential used for the post-recovery assertion. Assert exact OAuth invalid_grant, no returned tokens, old cookie/JWT rejection, fresh-login current ACL, and Beta API/cookie/refresh unaffected. Keep membership active.

- [x] Write and execute the HTTP regression with real SQL restore.
- [x] Review credential-safe TRX/logs, untouched refresh family, current-ACL assertions and unaffected Beta checks.

### G5 — Protected storage and operator crash drill

Files: M6 release/restore runbook, implementation status and acceptance evidence ledger.
Review Windows ACLs and Linux ownership/modes, operations marker write/flush/reopen/crash semantics, ambiguous/live marker reconciliation and signing-key retention. Record actual OS/filesystem, principals, negative unauthorized access, process results and key-version metadata (never key material). Process kill is not a power-loss durability test. Do not delete an ambiguous marker or recreate identity to make recovery succeed.

- [x] Execute Windows and Linux storage/marker drills and retained-key recovery: run `42472314238`, Linux attempt 1 and Windows attempt 4.
- [x] Record technical reviewer comparison and update runbook from observed outcomes. Both report/source/hash/cleanup checks pass; each OS has 17/17 evidence self-tests PASS. Owner adoption is not implied.

## Commands and technical completion

Run from a clean checkout: `pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full`. Inspect mandatory case names, discovered/executed/passed/failed/skipped counts, exit codes and artifact SHA-256. Missing, duplicate, zero or skipped mandatory tests fail the gate. Attach run ID, tested source/merge-tree SHA, sanitized logs/TRX/audit and reviewer decision to Issue #10. Runbook steps or a started workflow are not evidence of a successful rehearsal.

Full artifact `13614852100` proves 495/495 TRX PASS, 20/20 exact mandatory SqlM6 facts and 36/36 internal hash entries. All 13 workflow-mask payloads in the new exported full log are redacted. The unsafe historical artifact and genuine RED regression remain recorded in the implementation status; no failure history is erased.

Final review: agent source/artifact self-review; no independent reviewer was dispatched. The scope ruling is to complete technical evidence without impersonating owner architecture/security/adoption approval. The cost is an explicit remaining owner review/integration/milestone decision before M7. The long-name JSON redaction limitation is documented; intact TRX is authoritative and was verified exactly. SQL review-copy BOM conversion is accounted for explicitly rather than ignoring hash differences. No runtime code or migration is changed by this closeout.

Keep Issue #10 open for the owner closure decision, M6 not formally accepted and M7 acceptance blocked until owner review/adoption and PR integration are recorded. These checked technical work packages do not authorize production deployment or change conditional ADR statuses.
