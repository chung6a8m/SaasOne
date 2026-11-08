# M6 Issue #10 — reviewed acceptance evidence

Review date: 2026-11-08. **Technical result: G1–G5 and evidence-export safety PASS for the exact tested source below.** This is an agent source/artifact review, not an independent human approval, a production deployment authorization or M7 acceptance. Owner review, integration and milestone sign-off remain separate under `AGENTS.md` and roadmap §10. No conditional ADR is promoted by this record.

Issue record: https://github.com/chung6a8m/SaasOne/issues/10#issuecomment-6721096660

## Source, execution and provenance

- Reviewed PR #11 head: `9208cc7052ddbe194a05886842650dd0d447af90`.
- Tested PR merge commit: `5fe8d07d414c33c8d6f6f3c583faf56b5e066112`, against main `a68a6a3c1fb32017f98c82f845119d4f086bf2e1`.
- Full run: `42472314240`, attempt 1, artifact `13614852100`; completed successfully. All 12 PR-triggered workflows at the reviewed head were completed/success when checked.
- Command: `pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full`, invoking `scripts/check-m6.ps1`; exit 0. SDK 10.0.403, PowerShell 7.6.9, Ubuntu 22.04.5 X64, Docker Server 28.0.4. Source clean and unchanged before/after execution.
- Storage run: `42472314238`; Linux attempt 1/artifact `13614255928`, Windows attempt 4/artifact `13613184961`. Earlier Windows tool-download failures are not test passes or expected RED results.

The full and both storage ZIPs were downloaded and examined as bytes. CI supplied runtime execution; the authoring container has no dotnet, PowerShell, Docker or SQL Server. Local Python verification rechecked downloaded evidence, not runtime behavior.

This record and the accompanying status/runbook/plan edits are documentation-only closeout of that tested implementation. They do not relabel the tested SHA as the later documentation commit, certify a newly built package, or turn a subsequently started workflow into PASS. Verify the documentation-only diff explicitly. Any later runtime, test, workflow, script, dependency or package change requires fresh applicable evidence.

## Artifact identities

| Artifact | ZIP SHA-256 |
|---|---|
| Full `13614852100` | `37996cfaee19e5f1286e074b5dcc82ce386ac42dbe1ea28f1000a5586d136cbe` |
| Linux `13614255928` | `0e2648304bda8fe8767ec17671838e08011ee02bfebacfa75be212d6eff5b342` |
| Windows `13613184961` | `33713da0f0003cf0badda1fc81edadbc6d026958fdaf1c349f0f3d30e7635963` |

The full artifact contains `20261108T103921584Z-2ebd82e0e0404bd8a57d3e840d984ea1.zip`. Its SHA-256 is `6cd72cba45e1dd0fe7e6a4ddffad7721bbc7e087931c43417a4d4fbb7d60b655`, matching the accompanying checksum. All **36/36** internal lengths/digests agree with `checksums.json`; no inventory files are missing or extra.

Linux storage report SHA-256: `8402853f6d1874d3fb9d9e97d207edad5fe413e9abfee0aa70d1004dfc4e759e`.
Windows storage report SHA-256: `17765f0e18ec3d7906d9ee04405f1836b1112f786311b2d3db0877d8e7d9d9ee`.
Both report hashes match their `.sha256` files and their source matches the tested merge commit.

Full-artifact retention expires 2026-11-15 at 11:06:17 UTC; the storage artifacts expire earlier that day. Preserve the original reviewed ZIPs, their identities and this review outside the short-lived CI store. A preservation bundle or manually modified derivative is not the original CI artifact and needs its own provenance/checksum.

## Test inventory and export review

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| M0 architecture + unit | 26 + 187 | 0 | 0 |
| M1 SQL | 48 | 0 | 0 |
| M2 | 44 | 0 | 0 |
| M3 | 91 | 0 | 0 |
| M4 | 46 | 0 | 0 |
| M5 | 33 | 0 | 0 |
| M6 SQL | 20 | 0 | 0 |
| Total, eight TRX files | **495** | **0** | **0** |

All 20 SqlM6 names match `scripts/m6-mandatory-cases.json` exactly once, including TRX test IDs and class/method definitions. All six mandatory M6 component facts also occur once and pass. Inherited reports, commands, fixture cleanup and source identities were checked, not just aggregate counters.

Full, Linux and Windows each report **17/17 evidence self-tests PASS**, including actual ZIP round-trip coverage. The actual new `m6-full.log` contains **13 workflow mask commands, each with only `[REDACTED]` as payload**. No raw mask payload or JWT-shaped value was found in the exported files. Backup, key and release-binary files are excluded. This is verification of the reviewed export, not proof against every possible secret representation.

Two export interpretation details must not be hidden:

1. Generic JSON long-value redaction elides ten long SqlM6 method names. TRX names/definitions remain intact; exact-case verification used TRX and the source inventory, not the lossy JSON or reconstructed names.
2. `Copy-SafeEvidence` writes SQL review copies as UTF-8 without BOM. Adding only the three-byte UTF-8 BOM reproduces all six original SQL lengths/hashes in the release manifest. Exported bytes separately match `checksums.json`. Release manifest source and SHA match the build report: `dfd51ba8326537866ae171135283bb3d3f9f46a92450eaf4b6234a098d9af008`. The evidence ZIP is not the deployable release package and contains no release binaries.

The expected M1 wrong-target negative control has `wrongTargetExitCode=1`; its containing gate and cleanup pass. It is not an unexpected full-run failure.

## G1 — process death during real SQL RESTORE: PASS

Compared `M6RestoreProcessKillSqlTests.cs`, the passing mandatory TRX fact, `m6-restore-process.json` and the backup inventory in `m6-poc.json`.

- Owned PID 86327, start `2026-11-08T11:00:05.4006715Z`.
- SQL session 74/request 0; DMV observed `RESTORE DATABASE`, progress 0%, at `2026-11-08T11:00:10.0243689Z`.
- OS kill requested `2026-11-08T11:00:10.0979899Z`, process exit 137; SQL quiescence `2026-11-08T11:00:10.1343926Z`.
- Operation `bb45cda2-3e18-454c-92d2-a939927f1496`, tenant `f2e70edd-89f0-4ebd-bd27-cb016fab97a3`, backup `079ad3bf-b250-494f-9487-367cea5b19e5`. The 288,501,760-byte backup, its digest and signed manifest digest agree with the inventory.

Source assertions prove durable Requested/Bound audit, closed registry/traffic, no completed CLI report, refusal of another backup and a live prior-owner SQL session, same-source authorized retry, explicit current ACL and unchanged backup binding. All 32,768 restored audit payloads/hashes verify; fixture cleanup PASS. Process report SHA-256: `2bc938a92d59ceac9f7ece31959050dbcc3e14a4395afb5bca446c8eced69b6d`.

## G2 — signed backup with invalid database identity: PASS

All three mandatory facts in `M6RestoreIdentitySqlTests.cs` pass. Actual COPY_ONLY/CHECKSUM backups and valid HMAC target manifests reach physical DatabaseRestored, then missing singleton, wrong TenantId or wrong nonce prevents IdentityVerified. Migration/native/obligation/ACL spies remain unused, no identity repair or recovery-completion audit occurs, and Restoring/SecurityRevalidationRequired remains. Privileged test fabrication/teardown is not a production identity-repair path.

## G3 — offline platform release: PASS

The real coordinator fact in `M6PlatformReleaseSqlTests.cs` passes with owned runtime stop/start and awaited exit, explicit live SQL-session refusal, tenant-lease exclusion, unknown-history refusal, durable pending-audit HTTP 503 and tenant-operation barriers. Same-operation resume succeeds and repeated application is idempotent. Removal of the injected fixture history row is not a production recovery recipe. This proves the owned disposable host inventory, not all replicas in a deployed fleet.

## G4 — old HTTP credentials after physical recovery: PASS

`M6RestoreRefreshSqlTests.cs` keeps the post-recovery Alpha refresh family untouched until its assertion. Old refresh returns `invalid_grant`, old cookie/JWT return 401, while fresh login/refresh see current ACL and return 403 for the removed permission. Membership stays active. Beta cookie/API/refresh remain usable and its registry/version/generation remain unchanged.

## G5 — Windows/Linux storage and marker durability: PASS

Both reports show PASS/exit 0/cleanup PASS on the tested source. Restricted-principal positive controls work; unauthorized backup access and both restricted principals' signing-key access are denied. Wrong/missing keys fail; the retained key succeeds. The production marker writer is killed after intent creation, a fresh reader parses the retained marker and refuses ambiguous takeover. Windows SDDL and Linux ownership/modes are recorded.

This is a disposable-host principal/authenticated-byte/key-retention rehearsal, not certification of a deployed SQL service or secret manager. Process death does not establish power-loss, directory-fsync, storage-controller or network-filesystem durability.

## Decision and M7 handoff boundary

The technical evidence gaps tracked by G1–G5 are resolved at the reviewed source. The previous unsafe export and its regression history remain documented in `m6-implementation-status.md`; they are not retroactively approved. CI `ownerReview: PENDING` is unchanged. No independent reviewer or owner sign-off is invented.

Owner review/adoption, PR integration and M6 milestone closure must be recorded before M7 acceptance. Actual production host inventory, filesystem guarantees, deployed secret custody and backup retention remain operator responsibilities; roadmap risk/ADR approval is not automatically changed by this technical result.

After that M6 decision, M7 carries forward the roadmap's four-tenant isolation/concurrency tests, measured latency/throughput/error baseline on recorded hardware, and a second-product published dependency graph without Orders/OrderApprovals. The proposed 25 virtual users per tenant/100 total is a test configuration, not a production capacity commitment. Latency targets must be fixed after baseline and before trial acceptance. None of these M7 load or product-reuse gates has been executed by this review.
