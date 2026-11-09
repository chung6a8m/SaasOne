# M7 — stage-resolved SQL/login and Orders-lock profiling (2026-11-09)

> Diagnostic-only checkpoint. Do **not** merge PR #14, close Issue #12 or treat a Candidate run as Acceptance. Owner-approved 100-VU workload, 22 cells/88 performance criteria and existing hashes are immutable.

## Measured problem and hypothesis

At head `c8947636c897f30341f19efde08cffeb1d24a754`, M7 Candidate run 42563633872 FAILS all 22 performance cells (88 metrics), despite 7/7 mandatory M7 SQL cases and inherited M0–M6 workflows passing. The raw SQL observer reports ~26.727 unpooled physical opens and ~119.735 application commands/request. SQL blocking sampling sees Orders application/KEY contention but it cannot determine the owner or how long a lock remains held. Client SQL command timings also exclude reader consumption and cannot attribute phases.

**Hypothesis (not yet proved):** tenant routing/native authorization and repeated current ACL checks incur independent login costs; Orders locks may cover more of the end-to-end transaction than the SQL lock-acquisition wait itself. A global pool or cached Allow is *not* an authorized fix.

## Task contract

**Affected source files:** `src/Platform/SaaS.Tenancy/Operations/SaaSStageActivity.cs` (new activity boundary), `TenantTransactionCoordinator.cs`, `TenantWriteGate.cs`, `src/Platform/SaaS.Identity/{IdentityAuthority,DefaultIssuerStore}.cs`, `src/Platform/SaaS.AccessControl/AccessService.cs`, `src/Modules/SaaS.Orders/OrderService.cs`, `src/SaaS.Web/Orchard/IdentityRuntimeHost.cs`.

**Affected tests:** `tests/Unit/SaaS.UnitTests/M7/{M7SqlProfile,M7SqlProfileSession,M7SqlProfileTests,M7StageProfileTests}.cs`. Output is the existing `sql-profile.json` M7 evidence file with additive `stageTimings` and `sqlByStage`, not a changed acceptance target.

**Interfaces consumed:** only existing `ActivitySource`/`ActivityListener`, current caller-owned SQL connection/transaction, existing SQL diagnostic events and M7 evidence writer. **Provided:** named fixed stages: routing, identity.fresh, identity.native, acl.authorize, acl.snapshot, orders.read, orders.write, tenant.transaction, lock.wait.command/resource/page/acl/write-gate. SQL events use the ambient stage; a lock's successful acquisition event is matched to its transaction end to approximate hold duration. Never export source SQL, resource keys, GUIDs, tenant IDs, principals, tokens, exception payloads or traces.

**Invariants:** no database migration, schema, index, query, isolation level, SQL pooling, security guard, authority lifetime, ACL Deny precedence, audit, idempotency or transaction-ownership change. An Activity is optional and never controls authorization or transaction commit. The lock hold sample is measured until transaction scope ends (after commit/disposal), not SQL-server-confirmed release duration; it is not a lock-owner proof. SQL per-stage totals overlap nested spans and are NOT additive; `other` includes framework and unattributed work. SQL Command timing excludes reader consumption. Observe only disposable four-tenant SQL/HTTPS fixture.

## RED–GREEN, validation and decision

1. Write behavioral tests first: fake SqlClient diagnostic payload that throws when sensitive fields are accessed; tagged open/command are counted only for their active stage; lock acquires produce an acquired-to-transaction-end hold sample; no lock hold for a failed acquisition; collector stops after disposal. Verify tests fail for unimplemented stage collection without changing mandatory M7 inventory.
2. Add narrowly scoped Activities to the specified existing boundaries; preserve every return/throw/commit path. In particular, post-lock ACL rechecks, Order Revisions/Receipts/audit and current issuer validation remain fresh; do not move locks outside the SQL transaction.
3. Run `dotnet test` for stage tests and M3/M4/M5/M7 mandatory cases, `pwsh -NoProfile -File scripts/check-powershell-syntax.ps1`, inherited Full and `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Candidate` on the same source SHA. Record reported RED/GREEN, no false pass or skip; observe a source-matched CI artifact before any runtime optimization.
4. Compare per-stage open/command counts, p50/p95/p99 stage timings and Orders lock *wait* versus approximate *hold* across create/edit/submit/approve/detail/list. This aggregate is **not** yet per-tenant or per-operation attribution. Match raw samples, SQL waits and blocking buckets to identify a dominant stage. The next optimization must have its own narrowed plan and security/transaction RED tests. Do not choose pooled credentials, remove fresh authority reads or weaken lock scope to fit the existing target.
5. PR #14 remains Draft, M7 Candidate remains a failed performance rehearsal until measured otherwise, and Issue #12 remains open. Separate Acceptance with current-head inherited Full, approved target and independent reviewer/owner sign-off is required for promotion.
