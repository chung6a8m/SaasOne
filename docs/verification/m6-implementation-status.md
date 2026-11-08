# M6 implementation and verification status

Status: **G1–G5 technical evidence reviewed PASS at head `9208cc7052ddbe194a05886842650dd0d447af90`, tested merge `5fe8d07d414c33c8d6f6f3c583faf56b5e066112`; owner review/integration/milestone sign-off pending; NOT production-ready.** Issue #10 is the acceptance ledger and PR #11 contains the rehearsals. M7 acceptance has not started and remains gated on the M6 owner decision. Read the [final evidence review](m6-issue-10-evidence-review.md) and `docs/runbooks/m6-issue-10-rehearsals.md` together.

## Implemented source

The host composes six release streams, external EF bundles, idempotent SQL, immutable manifests and real Orchard verification. Tenant release/backup/restore reuse common locks, registry CAS/fences, the write gate and current-platform authorization. Physical restore persists source intent before the security claim and verifies immutable restored identity before migration/native/ACL work. Generic recovery cannot bypass an incomplete physical restore.

The full local entry point preserves inherited M0–M5 checks, exact source/tool versions, mandatory inventories, packaging, real SQL fixtures, reports/TRX and a checksummed ZIP, including failed runs. Earlier milestone checks alone never establish M6 acceptance. The inventory contains six M6 component facts and twenty SqlM6 facts; none of the original fourteen SQL facts was removed. Independent Windows/Linux jobs exercise restricted principals, retained keys, production marker writer/crash/reopen and evidence-redaction self-tests.

## Current reviewed evidence — 2026-11-08

Full run `42472314240`, attempt 1, artifact `13614852100`: actual downloaded ZIP and all 36 internal lengths/hashes verified; eight TRX files total **495/495 Passed, zero failed/skipped**. All 20 SqlM6 mandatory names and six M6 component facts occur exactly once. Source is clean and unchanged; inherited reports/commands/cleanup pass. Full ZIP SHA-256: `37996cfaee19e5f1286e074b5dcc82ce386ac42dbe1ea28f1000a5586d136cbe`; inner ZIP SHA-256: `6cd72cba45e1dd0fe7e6a4ddffad7721bbc7e087931c43417a4d4fbb7d60b655`.

Storage run `42472314238`: Linux attempt 1/artifact `13614255928`, Windows attempt 4/artifact `13613184961`. Both actual ZIP/report hashes, tested source, positive/negative principal controls, retained-key recovery, production marker crash/fresh-reader/fail-closed assertions and cleanup verify PASS. Each OS and the full run report **17/17 evidence self-tests PASS**. The current full log has 13 workflow mask commands, all with only `[REDACTED]` payloads. The earlier export blocker is resolved for this new artifact, not for historical archives.

| Gate | Current reviewed result |
|---|---|
| G1 | PASS: owned process killed during DMV-observed RESTORE DATABASE; exit 137, SQL quiescence, durable audit/source binding, closed traffic/registry, same-source authorized retry, 32,768 restored audit events. |
| G2 | PASS: three checksum/HMAC-valid invalid-identity backups refuse recovery without EF/native/ACL work or identity repair. |
| G3 | PASS: offline coordinator, owned runtime stop/start, session/tenant-operation barriers, unknown history, pending-audit HTTP 503, resume and idempotence. |
| G4 | PASS: untouched old refresh family and old cookie/JWT rejected after physical restore/recovery; fresh credentials use current ACL; active membership and Beta unaffected. |
| G5 | PASS: Windows/Linux protected-store and key controls; production marker process kill/fresh-reader reopen, ambiguous takeover refused, cleanup PASS. |

The final review records all artifact/report hashes, commands, source comparisons and interpretation limits. Exact test inventory comes from intact TRX names/definitions; generic JSON redaction elides ten long method names. SQL review copies omit the UTF-8 BOM; adding only those three bytes reproduces each original manifest hash. The evidence ZIP is not a deployable release package.

This documentation-only closeout does not claim CI ran on its later commit or certify a new package. Check the diff from the reviewed head and use fresh applicable evidence for any subsequent executable/test/workflow/dependency change. Original CI `ownerReview: PENDING` fields are not rewritten; this agent review is not independent human approval.

## Historical checkpoint — not the accepted export

Reviewed PR head: `db19c055ebada6750aabf1dbe62276a0a3496d3f`.
Tested merge tree: `3745ab82bc4566f479421846093f46d625ae7d63`.

Full run `42460717975`, attempt 1, artifact `13609973963`: all eight TRX files were downloaded and parsed, with **495/495 Passed, zero failed/skipped**, including 20/20 SqlM6. All 36 internal file lengths/hashes matched. Outer ZIP SHA-256: `a77c97bfb745e83eba94272faf9bb1e869e0a436391b9ba419453dcedb770ceb`; inner ZIP SHA-256: `b474219c6cf673e80b5b930d684bebe6a4140749763cd708dc89d69f1786a610`.

The earlier source/process comparison covered G1 real CLI/SQL termination and bound retry, all three G2 invalid identities, G3 offline coordinator and G4 untouched old credentials. Storage run `42460718088`, attempt 1, both reports were downloaded and verified PASS/exit 0/cleanup PASS at that tested merge.

Storage artifacts: Linux `13609466706`, outer SHA-256 `e5d522c4c1a588a56054722f0b5afc5d56ece35132702090059c12560fd83e4b`, report SHA-256 `4042FFDAE546CA03B3D12F3B1068B757F1ED6FD828B41B7DA474CDC08E8594AC`; Windows `13609179727`, outer SHA-256 `ba687caec5b492f7bcd0adda2f28076fd60ef28d5f0746770862a6ffd5a3948e`, report SHA-256 `F96F723BEA40B6FEC306FEFB236DB8B5B834295E0A5573E61D870FBE6FA23EA3`.

### Export defect and regression-first fix history

Review of that historical full ZIP found seven disposable credential values in workflow mask-registration payloads in `m6-full.log`. GitHub console masking does not sanitize an uploaded file. The parent's known-secret snapshot cannot include credentials created by nested verification processes. The original pattern filter did not remove `::add-mask::` payloads. No credential values are reproduced here. **The historical full ZIP is not an approved sanitized/shareable acceptance bundle**, despite its valid hashes and passing tests. Fixture cleanup passed; do not infer deployment secret custody from disposable-fixture evidence.

Regression-first head `a817d105bac49f02633650c2f9a9218daac2d7a6`, merge tree `91ade060d20b10a9575ed3c0d375bf5bd5365058`: storage run `42471769393`, attempt 2, Windows artifact `13613748559` proves the existing ten self-tests PASS and `workflow_mask_eof_redacted` FAIL with exit 1. Archive SHA-256: `c0f8e13cd4cc3005d6dee8022d8c8bc81958a4014ecd1205b995dbb3e43c1831`. Attempt 1 and the Linux retry failed during pinned-toolchain download with HTTP 504 before the regression ran; those failures are infrastructure failures, not RED evidence.

Fix `9208cc7052ddbe194a05886842650dd0d447af90` removes entire workflow mask payloads before persistence while preserving real line endings and all existing named-secret/JWT redaction. Tests cover EOF, LF, CRLF, prefixed and encoded commands, evidence identifier preservation and a real ZIP round trip. No restore/recovery/trust/ACL logic or SQL mandatory inventory changes. Current GREEN evidence is the newly downloaded full and OS artifacts above, not merely the existence of the fix.

## Commands, review and closure

```powershell
pwsh -NoProfile -File scripts/test-m6-evidence.ps1
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
```

Review the ZIP bytes, not just the Actions console: validate archive/internal checksums, source/package identity, commands, all mandatory cases exactly once, zero failures/skips, G1 process/audit assertions, both OS storage reports and absence of mask payload values. Preserve reviewed sanitized artifacts before CI retention expires on 2026-11-15. Do not edit old archives in place or relabel a manually sanitized derivative as an original CI artifact.

The authoring container has no dotnet, PowerShell, Docker or SQL Server; local runtime execution remains BLOCKED. Prior PR #9 14-fact success (`15112b3e41c48e2b52800544e610f3d55036ee7d`, run `42452218455`) and early PR #11 compiler/parser/hygiene/marker-harness failures remain historical records, not current sign-off. A later source edit cannot inherit an older run's PASS silently.

Issue #10 now has the exact-source technical comparison. Its checklist records technical evidence, not owner approval. Owner adoption, PR integration and M6 milestone closure remain to be recorded before M7 acceptance. Real host inventory, filesystem/power-loss guarantees, secret custody and backup retention remain deployment operator responsibilities. This record does not promote ADRs, automatically close roadmap risks R2-01/R3-01/R3-02/R3-03 or authorize production deployment.
