# M6 implementation and verification status

Status: **M6 ACCEPTED / Issue #10 CLOSED (completed), 2026-11-08; M7 acceptance OPEN in Issue #12, NOT yet accepted; NOT production-ready.** The repository owner explicitly requested completion of review, documentation, integration, M6 closure and M7 handoff. The agent executed that instruction; this is not an independent human review or a promotion of conditional ADRs. Decision: [Issue #10 closure record](https://github.com/chung6a8m/SaasOne/issues/10#issuecomment-6721533400). M7 gates: [Issue #12](https://github.com/chung6a8m/SaasOne/issues/12).

## Final current-head evidence and integration

Reviewed PR #11 head: `ac871c5bc76f0d7ad353501024f5847d304ed7f1`.
Tested PR merge commit: `c9490e6bc877edc3339ac8a8260cdcc979377aac`.
Actual merge into main: `317438513e5c726227076d378078478690c995be`, at 2026-11-08 12:31:36 UTC.
Both tested and actual merge commits have Git tree **`649443d0957aad5c8179bd0611540ac280d10c80`**. The merge did not change the tested source. All 12 PR-triggered workflows at the final head completed successfully; final review found no blocking discrepancy and no unresolved review threads or REQUEST_CHANGES reviews.

Full run **42477780518**, attempt **1**, artifact **13616029596** was downloaded and parsed independently. All **36/36** internal file lengths/hashes and exact inventory membership match. Eight TRX files contain **495/495 Passed, 0 failed, 0 skipped**, including **20/20 exact mandatory SqlM6 facts** and six M6 component facts, each once. M0 has 213 tests (26 architecture + 187 unit), M1 48, M2 44, M3 91, M4 46, M5 33 and M6 20. Source is clean/unchanged, commands and mandatory reports PASS. Full command: `pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full`, exit 0; SDK 10.0.403, PowerShell 7.6.9, Ubuntu 22.04.5 X64, Docker 28.0.4.

G1 current process report records PID **86308**, SQL session **53**/request **0**, DMV `RESTORE DATABASE` at **0%**, process exit **137**, SQL quiescence, cleanup PASS and **32,768** restored audit events. The unchanged source assertions and current TRX prove durable source binding, fail-closed behavior, source/prior-owner exclusion and authorized same-source retry. All G2 invalid-identity facts, G3 offline coordinator and G4 untouched refresh/current-ACL/Beta-isolation facts also pass at this source.

Storage run **42477780510**: Linux attempt **2**/artifact **13616716317** and Windows attempt **1**/artifact **13615543621**. Both downloaded ZIP/report hashes, source, principal/key/production-marker controls and cleanup PASS. Full, Linux and Windows each report **17/17 evidence self-tests PASS**. All **13** mask commands in the actual full ZIP log contain only `[REDACTED]`; no raw mask payload or JWT-shaped value was found in the exported files.

| Evidence | SHA-256 ZIP | SHA-256 inner ZIP / storage report |
|---|---|---|
| Full `13616029596` | `be2bda7106483a1f46a643e6e69cea86076beb838a1263fe4e8eb2d8b0168a0c` | `40911196f88afbdb35e5c56b6048d40270a369e2eb94db47b54a5854f8c05b25` |
| Linux `13616716317` | `47498cb89aca08c801691fb08dba91cbf7deedef9a4def45d040e66a95f1e374` | `9cb60db8d8d29377504dc81e8817900e2e63eb21df7e73c72ca8329fd8d48052` |
| Windows `13615543621` | `072cd6c6346890919e4311970fe7293e0b98b865d957e77ff69d527b6e654c8a` | `4457a54ad847b0c033e1b103fe29660ca20c0094dc02294fa86bb05702223a9d` |

This final status update changes documentation only. It does not claim the preceding runs executed a later documentation commit, certify a newly built package, or count a newly started post-merge workflow as PASS. The accepted implementation baseline is the exact merged tree above. Original CI `ownerReview: PENDING` fields remain unchanged snapshots from before the separate owner-directed closure decision.

## Implemented source

The host composes six release streams, external EF bundles, idempotent SQL, immutable manifests and real Orchard verification. Tenant release/backup/restore reuse common locks, registry CAS/fences, the write gate and current-platform authorization. Physical restore persists source intent before the security claim and verifies immutable restored identity before migration/native/ACL work. Generic recovery cannot bypass an incomplete physical restore.

The full local entry point preserves inherited M0–M5 checks, exact source/tool versions, mandatory inventories, packaging, real SQL fixtures, reports/TRX and a checksummed ZIP, including failed runs. Earlier milestone checks alone never establish M6 acceptance. The inventory contains six M6 component facts and twenty SqlM6 facts; none of the original fourteen SQL facts was removed. Independent Windows/Linux jobs exercise restricted principals, retained keys, production marker writer/crash/reopen and evidence-redaction self-tests.

## Earlier reviewed evidence — preserved checkpoint

This section records head `9208cc7052ddbe194a05886842650dd0d447af90`, tested merge `5fe8d07d414c33c8d6f6f3c583faf56b5e066112`, before the final current-head run and integration above. Read the [exact-source review](m6-issue-10-evidence-review.md) and `docs/runbooks/m6-issue-10-rehearsals.md` together. Statements about pending owner review in that earlier review describe its historical checkpoint, not the final decision.

Full run `42472314240`, attempt 1, artifact `13614852100`: actual downloaded ZIP and all 36 internal lengths/hashes verified; eight TRX files total **495/495 Passed, zero failed/skipped**. All 20 SqlM6 mandatory names and six M6 component facts occur exactly once. Source is clean and unchanged; inherited reports/commands/cleanup pass. Full ZIP SHA-256: `37996cfaee19e5f1286e074b5dcc82ce386ac42dbe1ea28f1000a5586d136cbe`; inner ZIP SHA-256: `6cd72cba45e1dd0fe7e6a4ddffad7721bbc7e087931c43417a4d4fbb7d60b655`.

Storage run `42472314238`: Linux attempt 1/artifact `13614255928`, Windows attempt 4/artifact `13613184961`. Both actual ZIP/report hashes, tested source, positive/negative principal controls, retained-key recovery, production marker crash/fresh-reader/fail-closed assertions and cleanup verify PASS. Each OS and the full run report **17/17 evidence self-tests PASS**. That full log has 13 workflow mask commands, all with only `[REDACTED]` payloads. The earlier export blocker is resolved for the new artifact, not for historical unsafe archives.

| Gate | Reviewed result, retained in final current-head TRX |
|---|---|
| G1 | PASS: owned process killed during DMV-observed RESTORE DATABASE; exit 137, SQL quiescence, durable audit/source binding, closed traffic/registry, same-source authorized retry, 32,768 restored audit events. |
| G2 | PASS: three checksum/HMAC-valid invalid-identity backups refuse recovery without EF/native/ACL work or identity repair. |
| G3 | PASS: offline coordinator, owned runtime stop/start, session/tenant-operation barriers, unknown history, pending-audit HTTP 503, resume and idempotence. |
| G4 | PASS: untouched old refresh family and old cookie/JWT rejected after physical restore/recovery; fresh credentials use current ACL; active membership and Beta unaffected. |
| G5 | PASS: Windows/Linux protected-store and key controls; production marker process kill/fresh-reader reopen, ambiguous takeover refused, cleanup PASS. |

Exact test inventory comes from intact TRX names/definitions; generic JSON redaction elides ten long method names. SQL review copies omit the UTF-8 BOM; adding only those three bytes reproduces each original manifest hash. Both interpretation boundaries were checked again in the final current-head artifact. The evidence ZIP is not a deployable release package. The historical agent review is not an independent human approval.

## Historical checkpoint — not the accepted export

Reviewed PR head: `db19c055ebada6750aabf1dbe62276a0a3496d3f`.
Tested merge tree: `3745ab82bc4566f479421846093f46d625ae7d63`.

Full run `42460717975`, attempt 1, artifact `13609973963`: all eight TRX files were downloaded and parsed, with **495/495 Passed, zero failed/skipped**, including 20/20 SqlM6. All 36 internal file lengths/hashes matched. Outer ZIP SHA-256: `a77c97bfb745e83eba94272faf9bb1e869e0a436391b9ba419453dcedb770ceb`; inner ZIP SHA-256: `b474219c6cf673e80b5b930d684bebe6a4140749763cd708dc89d69f1786a610`.

The earlier source/process comparison covered G1 real CLI/SQL termination and bound retry, all three G2 invalid identities, G3 offline coordinator and G4 untouched old credentials. Storage run `42460718088`, attempt 1, both reports were downloaded and verified PASS/exit 0/cleanup PASS at that tested merge.

Storage artifacts: Linux `13609466706`, outer SHA-256 `e5d522c4c1a588a56054722f0b5afc5d56ece35132702090059c12560fd83e4b`, report SHA-256 `4042FFDAE546CA03B3D12F3B1068B757F1ED6FD828B41B7DA474CDC08E8594AC`; Windows `13609179727`, outer SHA-256 `ba687caec5b492f7bcd0adda2f28076fd60ef28d5f0746770862a6ffd5a3948e`, report SHA-256 `F96F723BEA40B6FEC306FEFB236DB8B5B834295E0A5573E61D870FBE6FA23EA3`.

### Export defect and regression-first fix history

Review of that historical full ZIP found seven disposable credential values in workflow mask-registration payloads in `m6-full.log`. GitHub console masking does not sanitize an uploaded file. The parent's known-secret snapshot cannot include credentials created by nested verification processes. The original pattern filter did not remove `::add-mask::` payloads. No credential values are reproduced here. **The historical full ZIP is not an approved sanitized/shareable acceptance bundle**, despite its valid hashes and passing tests. Fixture cleanup passed; do not infer deployment secret custody from disposable-fixture evidence.

Regression-first head `a817d105bac49f02633650c2f9a9218daac2d7a6`, merge tree `91ade060d20b10a9575ed3c0d375bf5bd5365058`: storage run `42471769393`, attempt 2, Windows artifact `13613748559` proves the existing ten self-tests PASS and `workflow_mask_eof_redacted` FAIL with exit 1. Archive SHA-256: `c0f8e13cd4cc3005d6dee8022d8c8bc81958a4014ecd1205b995dbb3e43c1831`. Attempt 1 and the Linux retry failed during pinned-toolchain download with HTTP 504 before the regression ran; those failures are infrastructure failures, not RED evidence.

Fix `9208cc7052ddbe194a05886842650dd0d447af90` removes entire workflow mask payloads before persistence while preserving real line endings and all existing named-secret/JWT redaction. Tests cover EOF, LF, CRLF, prefixed and encoded commands, evidence identifier preservation and a real ZIP round trip. No restore/recovery/trust/ACL logic or SQL mandatory inventory changes. GREEN is established by downloaded full and OS artifacts, not merely the existence of the fix.

## Commands, closure and M7 handoff

```powershell
pwsh -NoProfile -File scripts/test-m6-evidence.ps1
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
```

Review the ZIP bytes, not just the Actions console: validate archive/internal checksums, source/package identity, commands, all mandatory cases exactly once, zero failures/skips, G1 process/audit assertions, both OS storage reports and absence of mask payload values. Preserve reviewed sanitized artifacts before CI retention expires on 2026-11-15. Do not edit old archives in place or relabel a manually sanitized derivative as an original CI artifact.

The authoring container has no dotnet, PowerShell, Docker or SQL Server; local runtime execution remains BLOCKED. Prior PR #9 14-fact success (`15112b3e41c48e2b52800544e610f3d55036ee7d`, run `42452218455`) and early PR #11 compiler/parser/hygiene/marker-harness failures remain historical records, not current sign-off. A later executable/test/workflow/dependency edit requires fresh applicable evidence; a source edit cannot silently inherit an older run's PASS.

Issue #10 now records both technical evidence and the owner-directed closure decision. PR #11 is integrated and M6 no longer blocks M7. Issue #12 tracks M7's four-tenant correctness/load, measured latency/throughput/error, bottleneck report, module template and a second product without Orders/OrderApprovals in its dependency/feature/publish graph. Its acceptance gates remain unchecked until M7-specific evidence exists. The proposed 25 virtual users per tenant (100 total) is an experiment, not a production capacity promise; latency targets must be fixed after baseline and before the acceptance run.

Real host inventory, filesystem/power-loss guarantees, secret custody and backup retention remain deployment operator responsibilities. This record does not promote ADRs, automatically close roadmap risks R2-01/R3-01/R3-02/R3-03 or authorize production deployment.
