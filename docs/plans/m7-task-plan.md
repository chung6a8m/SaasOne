# M7 implementation plan

Date: 2026-11-08. Specification: roadmap revision 1.3, M7, sections 8 and 9.1; Issue #12; AGENTS.md. Base: `7843cf0c5b38644875529ee5c55f1805d6c2da2a` (M6 closeout merged). Isolated branch: `codex/m7-acceptance`. M7 remains OPEN / NOT ACCEPTED until evidence and owner review.

## Goal and architecture

Prove four-tenant isolation/correctness under concurrent real SQL Server workloads and reuse platform in an independent second product. Keep product hosts as composition roots. Reuse native identity, authorization, readiness, transaction, release and migration gates; do not replace them with mocks or weaken them for the sample. No production capacity promise or ADR promotion.

The roadmap and Issue #12 are the implementation mandate. New implementation choices below are proposals for owner review, not previously approved defaults. Implement in this session; review/merge and milestone acceptance stay separate.

## Task 0: source provenance and detailed integration inventory

Affected files: this plan; `.github/workflows/m7-source.yml`. Consume the clean Git checkout and GitHub run metadata; provide a tracked-source archive, source SHA/tree and SHA-256 digest. Archive only `git archive HEAD`, never working-tree secrets, App_Data, connection strings or environment dumps. An archive job succeeding is NOT test or M7 acceptance.

- [ ] Inspect all ADRs, task/verification conventions, current test fixtures, host/theme composition and check/local-verify scripts from the exact source.
- [ ] Refine the concrete files and signatures below before implementing their consumers.

## Task 1: independent product and module template

Affected area: `samples/`, `templates/`, architecture tests, product smoke script and dedicated M7 CI. No edits to platform business behavior or migration streams. Consume platform project references and its existing hosting/readiness interfaces; provide a separate host plus its own platform-owned theme, with no project/package/Orchard feature/publish dependency on Orders or OrderApprovals. Provide a generated module template with explicit ownership and no automatic migration.

Failure tests first: direct/transitive project and package leak; Orchard feature leak; forbidden publish assembly; startup/readiness blocked on missing prerequisites; template invalid name/path; template generated project builds. Positive: build/publish/start the real sample, liveness and truthful readiness, restricted feature inventory. Do not label unconfigured 503 as ready.

## Task 2: four-tenant correctness and load harness

Affected area: `tests/Load/`, M7 integration tests, `scripts/` M7 runners. Consume existing SQL/native HTTP fixtures and existing public application interfaces after Task 0 signature inventory. Provide Alpha/Delta Standard, Beta/Gamma Approval; Alpha-only/shared/no-membership/operator users, Gamma role Allow + user Deny and different shared-user roles. All four remain Active during load. Delta lifecycle is a separate scenario.

Failure tests: URL/token/body tenant substitution, cross-tenant overlapping business codes, permissions/revocation, duplicate approval, concurrent update/replay, source unavailable and audit rollback inherited from M0-M6. Require real SQL, nonzero mandatory suite inventory and no mandatory skip. Distinguish expected negative/fault 4xx/503 from unexpected failures. No blanket ignoring HTTP errors.

Load protocol: default 25 VUs per tenant (100 total), explicit ramp-up, read/write/approval workload. First baseline run records distribution/throughput/errors and actual OS/CPU/RAM/SQL/tool versions. Only then record a target lock referencing the baseline artifact and source/configuration digest, before a separate acceptance run. Never derive a passing threshold from the acceptance report. Produce sanitized per-tenant/operation counts and p50/p95/p99/max, throughput, unexpected errors and bottleneck observations. Fixture/service throughput must not be mislabeled end-to-end HTTP throughput.

## Task 3: evidence, runbooks and inherited gates

Affected area: `.github/workflows/m7*.yml`, `scripts/` M7 checks, `docs/runbooks/`, `docs/verification/m7.md`, this plan and README if needed. Keep historical M0-M6 verification files unchanged. Provide product-creation guide, module ownership/template guide, load/reproduction/bottleneck runbook and exact-source verification record. Commands will include existing full verification plus dedicated product and load gates. Record exact commands, exit codes, source/tree, discovered/executed/pass/fail/skip, inventory, sanitized report/log hashes and artifact IDs. Missing prerequisites or incomplete results are BLOCKED/FAIL, never PASS.

## Migration ownership and review focus

M7 adds no production tables, model snapshots or migration streams. Existing owners remain Tenancy/Platform, AccessControl, Auditing, Orders, Approvals and Orchard. Disposable SQL fixture setup uses existing maintenance paths only. No concurrent migration writers. Review source-bound evidence integrity, target precommit ordering, zero/skipped test handling, secret export, sample dependency closure and cleanup on failed runs.

## Acceptance mapping

Issue #12 plan gate -> Task 0 and per-task refinement. Four-tenant matrix, correctness -> Task 2 plus inherited full suite. Load baseline and locked target -> Task 2 separate baseline/acceptance runs. Independent product -> Task 1 build/publish/start plus dependency assertions. Reuse/runbooks -> Tasks 1/3. Final evidence and owner decision -> Task 3; keep Issue #12 open until all are reviewed.
