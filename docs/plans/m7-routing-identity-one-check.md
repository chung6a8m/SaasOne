# M7 — remove duplicate customer identity SELECT in fresh HTTP tenant routing

**Date:** 2026-11-09. **Decision boundary:** candidate profiling only, never M7 Acceptance. PR #14 remains Draft, Issue #12 remains OPEN. Keep approved 100 VUs, four Active tenants, 22-cell/88-metric targets, tenant and credential boundaries, `Pooling=False`, exact hashes unchanged.

## Measured source and defect

At source `07e057600bb75e62befe28e33c671464737c345a`, M7 Candidate run `42607478106` attempts **1 and 2** use the same PR synthetic merge `974199a73073204be55d4dbe9408e2e420d203d8`. Both report ~**12.4317–12.4332 physical opens/request** and ~**83.79–83.82 commands/request**; all 22 cells fail, with 45/88 and 88/88 failed metrics respectively due to p95/p99/throughput variance. Correctness 7/7 SQL/HTTPS, 201/201 unit, 27/27 architecture, no unexpected HTTP or integrity failures; inherited M0–M6 **12/12 SUCCESS** on the same head.

Both diagnostic `routing` stages attribute exactly **2 physical SQL opens and 11 commands/request**: a fresh platform runtime connection and a fresh customer runtime connection. Current `IdentityRuntimeBoundary.SelectAsync` on the trusted customer connection invokes:
1. `TenantDatabaseVerifier.VerifyIdentityAsync(tenant, customerSql)` — checks registration, one-row `[tenancy].[Identity]`/nonce/initial operation against current platform registry.
2. `FoundationProfile.ReadSnapshotAsync(customerSql)` — reads current Orchard feature/migration hashes.
3. `TenantDatabaseVerifier.VerifyAsync(tenant, customerSql, snapshot)` — **calls `VerifyIdentityAsync` a second time on the same connection**, then verifies nonprivileged runtime principal, actual EF schema migrations and current baseline/hash.

The second identity SELECT is redundant because `VerifyAsync` reads it immediately after the snapshot using the same connection, without changing the reference platform tenant or routing. However, simply deleting step 1 would read the Orchard snapshot **before checking customer database identity**, violating the important trust order.

**Narrow hypothesis:** move the snapshot read between *one* mandatory pre-snapshot identity validation and the unchanged post-snapshot runtime/schema/baseline validation, using a callback within the verified customer connection. Save **one SQL command/request** while preserving both source data and all independently enforced security gates. This does **not** reduce the physical opens, remove a migration/feature check, cache a tenant, or solve the much larger p95/p99 budget on its own.

## Source/test contracts

**Production files:** `src/Platform/SaaS.Tenancy/Operations/TenantDatabaseVerifier.cs`, `src/SaaS.Web/Orchard/IdentityRuntimeHost.cs` only. **Test:** `tests/Unit/SaaS.UnitTests/M7/M7SqlAcceptanceTests.cs` and a small test-only `M7TenantRoutingIdentityCounter.cs`. M1/M2/M4/M5/M6 mandatory case list remains exact. No schema migration, SQL DDL, row/receipt modification, routing status/URL/tenant source change, permission/ACL/native/token/revocation change, pooled connection or cross-request session reuse.

**Consumed:** `TenantDatabaseVerifier.VerifyIdentityAsync`, `SqlOperations.RequireRuntimeAsync`, `TenancyConnections.VerifySchemaAsync`, `FoundationProfile.ReadSnapshotAsync`, existing `TenantRegistration` and `OrchardBaseline`, caller-owned fixed customer `SqlConnection`.

**Provided:** a new `TenantDatabaseVerifier.ReadAndVerifyAsync(TenantRegistration, SqlConnection, Func<SqlConnection,CancellationToken,Task<OrchardBaseline>>, CancellationToken)` method which checks registration and tenant identity *once*, invokes the snapshot callback *only after* a successful identity check on the already-open connection, and then executes the existing complete principal/schema/baseline validation. Factor a private `VerifyAfterIdentityAsync` so old public `VerifyAsync` still rechecks identity exactly once for other callers. Return no current trusted metadata to the HTTP user; only a successful verification flow. The verification method never opens/commits/disposes the caller-owned SQL connection.

**Negative gates:** wrong `DatabaseNonce` and wrong target/registration still fail *before* snapshot callback; wrong Orchard feature hash, corrupted baseline row, missing/wrong EF migration and overprivileged runtime login fail closed *after* the snapshot (exact errors preserved). The HTTP routing must not fall into Default or authorize another tenant. Real signed token, Deny, native revocation and tenant lifecycle are still rechecked downstream on separate fresh connections. No source freshness change across requests.

## RED → GREEN → evidence

1. In the existing mandatory four-tenant M7 SQL/HTTPS isolation test, wrap one successful, real authenticated Orders GET with a test-only `DiagnosticListener` observer that counts EF `CommandExecuted` SELECTs against **`[tenancy].[Identity]` by `TenantDbContext` and exact routed customer database**. Assert **1**, while preserving previous identity/ACL stage profile, all forged token/tenant/resource/body assertions and exact M7 case inventory. Current source must build and fail real SQL with **actual 2 vs expected 1**, not a missing test/setup failure. Save RED run/TRX/artifact.
2. Implement only `ReadAndVerifyAsync` and swap two separate `VerifyIdentityAsync`/snapshot/`VerifyAsync` calls to one verified callback. Run mandatory M7 7/7, M4/M2 live native security, M1 corrupted nonce/feature/schema, M6 physical-restore/recovery, source-matched inherited Full; independently confirm correct SQL counter 1. Verify no skipped mandatory cases or stale/incorrect target accepted.
3. Compare fresh same-source M7 Candidate artifact: `routing` SQL commands/request **11→10**, routing opens stay **2**, SQL commands/request lower by ~1; check p95/p99/throughput, Orders locks, all 22/88 approved budgets. No causal latency/production-SLO guarantee from two shared-runner samples.
4. If any correctness gate fails or no identity SELECT saved, stop/revert the isolated change. M7 remains NOT ACCEPTED until 22/88 approved metrics PASS, **separate actual** `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Acceptance -Targets tests/Load/m7-targets.json`, final-source inherited 12/12 Full, independent reviewer/evidence verification and explicit owner signoff before Ready/merge and Issue #12 closure.

**Review risk:** customer identity is still validated before reading Orchard snapshot; a source-change race between initial identity and snapshot existed before this change and is not made serializable. Runtime/registration sources remain fresh per request, connection is never borrowed across requests or Orchard downstream; no risk is “mitigated” by caching a successful verification.
