# M6 implementation and verification status

Status: **candidate implementation; NOT accepted, NOT production-ready**. Issue #10 is the acceptance ledger and PR #11 contains the follow-up rehearsals. M7 acceptance remains blocked until current G1–G5 evidence and the evidence-export checks have reviewed PASS results. Read `docs/runbooks/m6-issue-10-rehearsals.md` alongside the release/restore runbook.

## Implemented source

The host composes six release streams, external EF bundles, idempotent SQL, immutable manifests and real Orchard verification. Tenant release/backup/restore reuse common locks, registry CAS/fences, the write gate and current-platform authorization. Physical restore persists source intent before the security claim and verifies immutable restored identity before migration/native/ACL work. Generic recovery cannot bypass an incomplete physical restore.

The full local entry point preserves inherited M0–M5 checks, exact source/tool versions, mandatory inventories, packaging, real SQL fixtures, reports/TRX and a checksummed ZIP, including failed runs. Earlier milestone checks alone never establish M6 acceptance. The inventory contains six M6 component facts and twenty SqlM6 facts; none of the original fourteen SQL facts was removed. Independent Windows/Linux jobs exercise restricted principals, retained keys, production marker writer/crash/reopen and evidence-redaction self-tests.

## Reviewed checkpoint — 2026-11-08

Reviewed PR head: `db19c055ebada6750aabf1dbe62276a0a3496d3f`.
Tested merge tree: `3745ab82bc4566f479421846093f46d625ae7d63`.

Full run `42460717975`, attempt 1, artifact `13609973963`: all eight TRX files were downloaded and parsed, with **495/495 Passed, zero failed/skipped**, including 20/20 SqlM6. All 36 internal file lengths/hashes matched. Outer ZIP SHA-256: `a77c97bfb745e83eba94272faf9bb1e869e0a436391b9ba419453dcedb770ceb`; inner ZIP SHA-256: `b474219c6cf673e80b5b930d684bebe6a4140749763cd708dc89d69f1786a610`.

| Gate | Reviewed baseline observations |
|---|---|
| G1 | Real owned CLI PID/start time, DMV RESTORE DATABASE request, nonzero process exit 137, SQL quiescence, durable requested/bound audit, fail-closed registry/traffic, refusal of another backup/live prior-owner session, same-source authorized recovery and 32,768 restored audit events. Source and process report were compared. |
| G2 | All three valid-HMAC/checksum negative identity facts passed: missing singleton, wrong TenantId and wrong nonce. Source assertions prove no EF/native/ACL recovery, identity repair or completion audit. |
| G3 | Full offline coordinator, owned runtime stop/start, live-session and tenant-lease exclusions, actual pending-audit HTTP 503, unknown-history refusal, same-operation resume and idempotence. This is disposable-host inventory, not a production host inventory. |
| G4 | Untouched old Alpha refresh family, old cookie/JWT rejected after physical restore and recovery; fresh login/refresh use current ACL; active membership and independent Beta credentials/generation preserved. |
| G5 | Storage run `42460718088`, attempt 1, both OS reports downloaded and verified: principal positive/negative controls, wrong/missing/retained key, production marker writer killed and reopened in a fresh process, ambiguous takeover denied, cleanup PASS. |

Storage artifacts: Linux `13609466706`, outer SHA-256 `e5d522c4c1a588a56054722f0b5afc5d56ece35132702090059c12560fd83e4b`, report SHA-256 `4042FFDAE546CA03B3D12F3B1068B757F1ED6FD828B41B7DA474CDC08E8594AC`; Windows `13609179727`, outer SHA-256 `ba687caec5b492f7bcd0adda2f28076fd60ef28d5f0746770862a6ffd5a3948e`, report SHA-256 `F96F723BEA40B6FEC306FEFB236DB8B5B834295E0A5573E61D870FBE6FA23EA3`. Both match the tested merge tree, PASS/exit 0/cleanup PASS.

### Blocking export defect, not a runtime PASS override

Review of the actual full ZIP found seven disposable credential values in workflow mask-registration payloads in `m6-full.log`. GitHub console masking does not sanitize an uploaded file. The parent's known-secret snapshot cannot include credentials created by nested verification processes. The original pattern filter did not remove `::add-mask::` payloads. No credential values are reproduced here. **The historical full ZIP is not an approved sanitized/shareable acceptance bundle**, despite its valid hashes and passing tests. Fixture cleanup passed; do not infer deployment secret custody from disposable-fixture evidence.

Regression-first head `a817d105bac49f02633650c2f9a9218daac2d7a6`, merge tree `91ade060d20b10a9575ed3c0d375bf5bd5365058`: storage run `42471769393`, attempt 2, Windows artifact `13613748559` proves the existing ten self-tests PASS and `workflow_mask_eof_redacted` FAIL with exit 1. Archive SHA-256: `c0f8e13cd4cc3005d6dee8022d8c8bc81958a4014ecd1205b995dbb3e43c1831`. Attempt 1 and the Linux retry failed during pinned-toolchain download with HTTP 504 before the regression ran; those failures are infrastructure failures, not RED evidence.

The follow-up fix removes entire workflow mask payloads before persistence while preserving real line endings and all existing named-secret/JWT redaction. Tests cover EOF, LF, CRLF, prefixed and encoded commands, evidence identifier preservation and a real ZIP round trip. No restore/recovery/trust/ACL logic or SQL mandatory inventory changes. **A source fix is not GREEN evidence:** inspect fresh CI, both OS self-test reports, full M0–M6 TRX and the packaged log at the final tested SHA before closing Issue #10.

## Commands and closure

```powershell
pwsh -NoProfile -File scripts/test-m6-evidence.ps1
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
```

Review the ZIP bytes, not just the Actions console: validate archive/internal checksums, source/package identity, commands, all mandatory cases exactly once, zero failures/skips, G1 process/audit assertions, both OS storage reports and absence of mask payload values. Preserve reviewed sanitized artifacts before CI retention expires. Do not edit old archives in place or relabel a manually sanitized derivative as an original CI artifact.

The authoring container has no dotnet, PowerShell, Docker or SQL Server; local runtime execution remains BLOCKED. Prior PR #9 14-fact success (`15112b3e41c48e2b52800544e610f3d55036ee7d`, run `42452218455`) and early PR #11 compiler/parser/hygiene/marker-harness failures remain historical records, not current sign-off. A later source edit cannot inherit an older run's PASS silently.

Issue #10 must contain the final run attempts, artifact identities, reviewer comparison and explicit decision before its checklist is completed. Real host inventory, filesystem/power-loss guarantees, secret custody and backup retention remain deployment operator responsibilities. Neither these source changes nor the reviewed baseline closes roadmap risks R2-01/R3-01/R3-02/R3-03 or authorizes production deployment.
