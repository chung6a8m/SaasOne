# M6 implementation and verification status

Status: **candidate implementation; NOT accepted, NOT production-ready**. Issue #10 is the acceptance gate and PR #11 contains the follow-up rehearsals. M7 acceptance remains blocked until G1–G5 have reviewed PASS evidence. Read `docs/runbooks/m6-issue-10-rehearsals.md` alongside the release/restore runbook.

## Implemented source

The host composes six release streams, external EF bundles, idempotent SQL, immutable manifests and real Orchard verification. Tenant release/backup/restore reuse common locks, registry CAS/fences, the write gate and current-platform authorization. Physical restore persists source intent before the security claim and verifies immutable restored identity before migration/native/ACL work. Generic recovery cannot bypass an incomplete physical restore.

The full local entry point preserves inherited M0–M5 checks, exact source/tool versions, mandatory inventories, packaging, real SQL fixtures, sanitized reports/TRX and a checksummed ZIP, including failed runs. Earlier milestone checks alone never establish M6 acceptance.

The inventory now contains **six M6 component facts and twenty SqlM6 facts**. Six additional SQL facts cover the real process-kill rehearsal, three signed-invalid-identity variants, the full offline platform coordinator and old HTTP refresh credentials after restore. Two OS storage jobs separately exercise restricted principals, retained keys and marker writer/crash/reopen behavior. Written tests and workflow definitions are not execution evidence.

## Evidence boundaries

The historical PR #9 merge-tree `15112b3e41c48e2b52800544e610f3d55036ee7d` passed its existing 14 SqlM6 facts in run `42452218455`, as recorded in Issue #10. That run does not cover the new rehearsals and is not current sign-off.

The authoring container has no dotnet, PowerShell, Docker or SQL Server. Local runtime execution is BLOCKED; fresh CI results at the reviewed SHA are required. Early PR #11 iterations exposed a test visibility error, PowerShell parser/hygiene failures and a marker-writer harness failure. Failed or cancelled attempts remain failure history, never acceptance proof. Current results and reviewed artifact identities belong in Issue #10 so a later source edit cannot silently inherit an older run's PASS.

| Gate | Added source coverage | Evidence required before closure |
|---|---|---|
| G1: process death during RESTORE | Real maintenance CLI, DMV observation, OS termination, SQL quiescence, immutable binding and authorized retry | Mandatory TRX plus `m6-restore-process.json`, audit/state assertions and source/package identities |
| G2: signed invalid identity | Missing singleton, wrong TenantId and wrong nonce after physical restore; strict recovery-work spies | All three mandatory facts pass; HMAC/header/checksum verified first; no repair or recovery bypass |
| G3: offline platform release | Owned runtime stop/start, live-session and tenant-lease exclusion, unknown history, actual pending audit/HTTP 503, resume/idempotence | Real SQL/host assertions pass; distinguish disposable host inventory from deployment host inventory |
| G4: old HTTP credentials | Untouched old refresh after recovery, old cookie/JWT, fresh-login current ACL and independent Beta credentials | Mandatory HTTP/SQL fact passes without revoking the tested family beforehand |
| G5: storage/runbook | Windows/Linux ACL negative and positive controls, wrong/missing/retained key, actual marker writer plus fresh-process reopen | Both OS reports and hashes pass, cleanup passes, operational limits reviewed; no implied power-loss or production certification |

All 20 mandatory SQL facts must be discovered/executed exactly once, with zero failures/skips. Validate the inherited gates, manifest trust, six EF bundles, native verification, lifecycle conflicts, approved alternate-source restore, current ACL and restored PendingApproval workflow as well as the new cases. Do not reduce the old inventory to make a new case pass.

## Commands and owner review

```powershell
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
```

Review `artifacts/local-verify/<run-id>.zip` and `.sha256`, exact tested SHA, commands, logs/TRX/audit assertions and G1 evidence. Review the independent Windows/Linux `m6-storage-drill.json` and hashes from the storage workflow. Component-only scope cannot establish runtime acceptance.

Issue #10 must link the final run attempts and retained artifacts with a reviewer decision before its checklist is completed. A green CI run still does not authorize arbitrary production deployment: real host inventory, filesystem guarantees, secret custody and backup retention remain operator responsibilities. M6 and roadmap risks R2-01/R3-01/R3-02/R3-03 are not closed by adding this source.
