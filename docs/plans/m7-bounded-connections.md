# M7 bounded SQL connection lifetime — measured first change

Extends m7-performance-completion.md under the owner's target/implementation approval. This plan precedes runtime implementation. No migration/model/index or entitlement change.

## Diagnosis

Unchanged-runtime profiling head d4a3182357ff6c4897d5e3231f08463bf64566ce; PR synthetic merge c6f38d6778f1e7da44d109f9f25bcd231c7158b2; run 42534520827; artifact 13639246446. Outer SHA256 f8a5c0642fef983041c82530d0ee313491490c11600a58534a483e408eb628e7; inner 7fcbc924ffed78adda7d9503bf64b972caae1ea20999bb44a472704875227f3a. All 24 inventory hashes, exact seven-case TRX, 221 non-SQL cases and raw load statistics/thresholds were independently checked. This is not independent reviewer approval.

628 successful requests, zero errors. SQL client observed 47,240 opens (75.223/request) and 122,605 commands. Sum of overlapping connection-open durations: 16,351,498.680 ms versus 933,015.169 ms for command Execute durations; average open 346.137 ms. Server logins delta 47,242 corroborates physical logon churn. SQL waits include 373,018 ms LCK_M_X and 359,177 ms LCK_M_S; these remain a secondary investigation, not dismissed. Background wait totals are not CPU utilization. Command timings exclude reader consumption. Instrumentation overhead and shared generator/application topology remain limitations.

Observed per-cell p95 27.226–55.219 seconds; all 22 cells / 88 target metrics fail. Targets are now Approved, but the run remains Candidate FAIL, not acceptance. Neither the original high-latency runs nor this instrumented run is relabeled.

Code inspection identifies repeated EF auto-open/close cycles in a single schema check and the seven fresh platform queries in IdentityAuthority.ValidateAsync. EF can retain a connection explicitly within one operation without caching its query results. The first controlled change below tests that narrower mechanism before considering wider composition changes.

## Affected files and contracts

Runtime: src/Platform/SaaS.Tenancy/Data/TenancyConnections.cs (VerifySchemaAsync only); src/Platform/SaaS.Identity/IdentityAuthority.cs (ValidateAsync only).
Tests: tests/Unit/SaaS.UnitTests/M7/M7ConnectionLifetimeSqlTests.cs (Category SqlM1); tests/Unit/SaaS.UnitTests/IdentityPoc/IdentityConnectionLifetimeTests.cs (existing SqlM2 partial suite).
Evidence: docs/verification/m7-profiling-ledger.md. M7's exact seven-case inventory and workload remain unchanged; the new tests extend inherited M1/M2 rather than replace any case.

VerifySchemaAsync(DbContext, CancellationToken): if it opens a connection, keep that connection for the existing existence/history reads and close it on success, schema error or cancellation. If a caller already owns an open connection/transaction, never close, replace or commit it. Keep exact expected/applied migration comparison and all fresh source reads; do not cache schema acceptance.

IdentityAuthority.ValidateAsync(Guid, Guid, bool, CancellationToken): explicitly open its newly owned PlatformDbContext connection once, leave every existing query, check, exception order and native issuer check intact, and dispose through the existing await-using lifetime. Each call creates a fresh context/physical login, including repeated calls in the same service scope. Keep Pooling=false. No authority reuse across requests or lock boundaries.

## Test-first steps

1. Add real-SQL tests before implementation. Expect the one-open schema test and one-platform-open authority test to fail on existing repeated opens, not compilation. Positive/negative companion cases verify external transaction survival, closed owned connection after schema error/cancellation, repeated fresh reads, SQL login disable between same-scope authority calls and membership revocation.
2. Run inherited M1/M2 jobs and retain named RED evidence. Native issuer open count is measured separately within the same fixture; authority must use that unchanged native cost plus one platform open. No hardcoded native query shortcut.
3. Implement only the two bounded lifetimes. Run syntax, M0, M1/M2 and all inherited gates. Inspect no runtime DDL, dependency, transaction lock order, source selection or query changes.
4. Run fresh M7 Candidate with the same approved targets/workload and profiling. Compare physical opens/request and measured results; do not attribute repeat noise alone to improvement. If insufficient, record remaining bottlenecks and amend scope before the next controlled change.
5. Only a separate exact-source Acceptance with embedded Full, artifact review and final sign-off can close M7. No target relaxation, timeout increase, lower load or production claim.

History: cb7b4d6 introduced a missing final class brace in the test load driver; its compile failure CS1513 remains FAIL. d4a3182 corrected the brace, then build and both collector tests passed (194 unit + 27 architecture). This correction was not a performance optimization.
