# Issue #15 — Orders lock wait/hold operation attribution (diagnostic-only P2)

**Status:** proposed experiment, no permission to modify runtime lock semantics. Base branch commit: `47eef8b3386a670c0b24b95d796f48eb419490b7` (Draft PR #16). This is **stacked** work; it must not be merged independently of the security decision on PR #16.

## Context, frozen benchmarks and question

Issue #15's initial **8-physical-open Approve objective** has been measured at the SQL/HTTPS fixture ([run 42952782818](https://github.com/chung6a8m/SaasOne/actions/runs/42952782818)), while 22/22 M7 performance cells still miss pre-approved p95/p99 limits. Stage-resolved aggregate data indicate `orders.lock.page` and `orders.lock.resource` acquisition waits, and approximately long command/page holds, but **aggregate spans overlap** and do not identify which tenant or business operation creates each wait/hold. Before changing query, index, lock order or transaction boundaries, add operation-specific diagnostic evidence.

## Affected files and interfaces

- `tests/Unit/SaaS.UnitTests/M7/M7StageProfileTests.cs`: **first RED** non-SQL behavioral tests. Verify per-operation lock wait + approximate transaction hold and reject non-allowlisted labels and private data. Previous Stages and Holds remain unchanged.
- `tests/Unit/SaaS.UnitTests/M7/M7OperationAttribution.cs` (new): sanitized **test-only** HTTP middleware context. Only a recognized `/alpha|beta|gamma|delta/api/v1/orders...` path/method may produce tenant + operation labels (create/edit/submit/approve/detail/list); GUID paths must parse but their values must never appear in reports or tags. No request headers, body, tokens, subjects, SQL, query params, cookies, addresses, raw request paths or GUIDs exported.
- `tests/Unit/SaaS.UnitTests/M7/M7SqlFixture.cs`: install test-only middleware **before** `IdentityRuntimeHost.Use`; no production middleware or authorization/DB change.
- `tests/Unit/SaaS.UnitTests/M7/M7LoadAcceptanceTests.cs`: enable test-only sanitized attribution exclusively for the unchanged load window, dispose on error. Do not mutate the workload, samples, VU, think time, target or baseline hashes.
- `tests/Unit/SaaS.UnitTests/M7/M7StageProfile.cs`: append `ByOperation` summaries to existing diagnostic snapshot; group p50/p95/p99 durations for stage wait and approximate hold by whitelisted tenant and operation. Stage durations are **NOT additive**, and hold ends at transaction Activity scope end, NOT verified server lock release. Do not export trace IDs.
- `tests/Unit/SaaS.UnitTests/M7/M7SqlProfileSession.cs`: additive `operationStageTimings` in sanitized `sql-profile.json`, with explicit interpretation and nonempty real-load gating. Original `stageTimings`, `sqlByStage`, blocking probe and acceptance metrics remain exactly as before. No SQL server privileges or runtime API added.
- This plan and issue review/verification notes. **Migration owner: none**. Runtime files: none.

## Test-first and verification

1. Commit non-SQL tests and plan only. At old code the test expects a `ByOperation` field not present, and must **fail by assertion**, not a build/fixture issue. Capture exact RED SHA and CI.
2. Implement the test-only middleware and profiler; M0 unit tests must turn GREEN without disabling tests. Real M7/Issue15 SQL+HTTPS probe must still discover seven mandatory original SQL tests plus one Approve test, with all eight PASS, and emit nonempty allowlisted operation profile for the 4-tenant load.
3. Run pinned CI M0–M6, M7 SQL/HTTPS tests with original inventories, and review SQL sample/collector evidence at an exact PR merge SHA. If native acceptance on main is blocked by `baseline_ancestor_or_history_missing`, record BLOCKED (do not alter hash or ancestry check to force PASS). No profiler-only data counts as production SLO success.
4. Identify highest wait and longest approximate hold **per tenant/operation**; do not infer a blocking session ID, exclusive SQL owner, lock lifetime or improvement from an aggregate. Only after independent review choose one separately RED→GREEN protected runtime change, if any.

## Security and ownership

No new SqlClient session, pooled connection, request-wide permission/Allow cache, privilege, transaction or lock. This is test-only runtime observation, not an authorization source. Test-only tags come from a closed literal vocabulary; no untrusted path segment is exported. On malformed/other routes attribution is absent, not a fallback to `Default`. Always retain historical M7 target failures, PR #16 Draft review boundary and production rollout block.
