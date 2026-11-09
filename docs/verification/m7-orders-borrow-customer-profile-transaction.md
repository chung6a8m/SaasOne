# M7 borrowed Orders transaction — source-locked RED/GREEN and Candidate verification (2026-11-09)

**Decision:** scoped correctness GREEN; M7 Candidate still **FAIL 22/22 cells, 44/88 performance metrics**. PR #14 stays Draft/unmerged; Issue #12 stays open. No actual Acceptance or final owner/reviewer signoff. Owner-approved 100-VU workload and thresholds remain unchanged.

## Bounded change and contract

See `docs/plans/m7-orders-borrow-customer-profile-transaction.md`, committed at `49f81bb909751b9bcd51d4131ab9615d39158c94`. Runtime GREEN `76382d00125560fde02bc82ee04eec01b197d86e` changes only `src/SaaS.Web/Access/{ApplicationAccessProfile,ApplicationRequestAuthority}.cs` and `src/SaaS.Web/Orders/OrdersEndpoints.cs`. In each trusted Orders `TransactionAccessReader` authority callback, `ApplicationRequestAuthority.ReadAsync` still verifies fresh native/EF family/account/membership/tenant/client and current plan. The complete five-owner EF migration-history SQL UNION, ApprovalHistory columns and **fresh** current `tenancy.ApplicationProfile` read now execute **inside the caller-owned Orders transaction on the already-selected customer SQL connection**, not on a separate unpooled customer login. `TenantDbContext.Database.UseTransactionAsync`, SQL history `DbCommand.Transaction` and `ApprovalHistoryVerifier.DemandColumnsAsync(..., transaction)` all bind to that transaction. No owned connection/transaction commit, rollback, close or reuse across operations. The separate pre-transaction initial authority and other web/access endpoint callers continue opening and closing their own source connections.

Borrowing is restricted to the caller-selected customer connection: open SQL state, exact connection+transaction object identity, fixed server/catalog and runtime principal plus pooling mode are checked against the Orchard-routed connection. A mismatched connection fails closed. No token/header/body selects a DB. Original current ACL Deny/resource and post-lock source checks, security generation, rowversion, app locks, 30s timeout, idempotent receipts, historical approval obligations and audit/rollback still execute. Native Default issuer remains in its own **fresh per-validation scope**; no cross-request pooled principal, cached Allow, schema/migration/index change or new SQL role.

**Review risk:** profile metadata reads now enlist in the Orders SQL transaction and could alter server lock contention; hence lock-wait/hold stage metrics are explicitly included. A source-revocation race after the final authoritative read is not magically eliminated. No M7 thresholds relaxed.

## RED — behavior proved on real SQL Server before runtime

RED test commit `b65e3e1b85fb24daa3e05ee2b53d0db6772d63b6`, exact [M7 run 42594132037](https://github.com/chung6a8m/SaasOne/actions/runs/42594132037), artifact `13662428862`: Release build 0 warnings/errors; architecture **27/27** PASS, unit **201/201** PASS; M7 SQL/HTTPS **6/7 PASS, one deliberate RED, 0 skipped**. Existing mandatory `Four_active_tenants_isolate_same_number_data_and_all_cross_token_routes` asserted zero new customer opens in each `acl.authorize` stage while requiring the existing SQL command counts and keeping every cross-tenant/token/forged-path/body assertion. Actual **2** opens for **2** ACL stages (expected **0**). This is intended FAIL, not a performance Candidate.

In the same RED source, M4 SQL components [run 42594132175](https://github.com/chung6a8m/SaasOne/actions/runs/42594132175) PASS, including the expanded existing mandatory `Native_required_authority_sources_fail_closed_before_returning_a_stored_result`. The test temporarily corrupts the application's ManifestHash via the private fixture, requires **HTTP 503 for both API and web** and restores the exact original hash in `finally` before confirming healthy authorized replay. No mandatory test name/count additions; this negative coverage is retained in the GREEN source.

RED artifact outer ZIP SHA256 `c19ab6237c673a9887045bd04ffefbb550a6fdb111996dedf9f05923eac042bb`. The failing M7 test is not mislabeled a pass.

## GREEN — real SQL/HTTPS and measured change

Runtime HEAD `76382d00125560fde02bc82ee04eec01b197d86e`; GitHub **push-triggered** [M7 run 42594892786](https://github.com/chung6a8m/SaasOne/actions/runs/42594892786), [artifact 13663356769](https://github.com/chung6a8m/SaasOne/actions/runs/42594892786/artifacts/13663356769) ran on **exact HEAD** (not a synthetic PR merge). Source and policy jobs SUCCESS, verified Release build 0 warnings/errors, architecture **27/27**, unit **201/201**, mandatory real SQL/HTTPS **7/7 PASS, 0 skipped/failed**. Locked 100 VUs/four active tenants: **4,086/4,086** all-phase HTTP requests successful, no unexpected failure/integrity error. File ZIP outer SHA256 `ce31d79b9ca2159e92ac5aeba591e2bcb4a30cadd4d1d3264e2f97b0811ad0e1`; inner ZIP SHA256 `06c02eeee9eaf36a3f4e65a9e72c9479d3878e1133546d2a549603a52bba95e4`. Independently confirmed exact inner sidecar, **24/24** inventory file sizes/hashes, seven passing mandatory TRX, all 4,086 successful raw samples, every 22-cell measured-window nearest-rank p50/p95/p99 and throughput calculation and all 88 target comparisons, **zero discrepancies**.

Before reference: [M7 PR run 42589726144](https://github.com/chung6a8m/SaasOne/actions/runs/42589726144), [artifact 13660888081](https://github.com/chung6a8m/SaasOne/actions/runs/42589726144/artifacts/13660888081), PR synthetic tested merge `acec1001418fabdcd1a6f9104bfea5d097218ef3`; 2,665/2,665 successful requests, same approved target/workload.

| Metric | Before | GREEN push |
| --- | ---: | ---: |
| SQL physical opens/request | 15.1467167 | **12.4302496** |
| `acl.authorize` new opens | **7,237/7,237 stages** | **0/11,094 stages** |
| Application SQL commands/request | 107.8116323 | 107.7897699 |
| Successful HTTP requests, all phases | 2,665 | **4,086** |
| Completed-window throughput | 15.8750/s | **25.2000/s** |
| Per-cell p95 range | 5.516–12.633 s | **4.036–7.680 s** |
| Measured starts/cell | 76–100 | **121–157** |
| `acl.authorize` p95 | 1.482 s | 0.952 s |
| Orders page/resource lock wait p95 | 3.577/2.046 s | 1.812/1.030 s |
| Orders command-lock hold p95 (approx.) | 4.676 s | 2.511 s |
| Failed approved cells/metrics | **22/86** | **22/44** |

**Measured engineering mechanism:** every `acl.authorize` call still executes **4 SQL client commands** attributable to this stage (44,376 commands/11,094 invocations), but has **zero new physical customer opens**, proving that the full profile reads were not deleted. Runtime lock measures and throughput/p95 across two runner executions are **observations**, not production-SLO or isolated causal capacity claims; profiler has overhead and overlapping spans.

**Approved M7 Candidate FAIL:** all **22 p95** and **22 p99** metrics fail; all **22 throughput** and **22 measured sample-floor** metrics PASS, so **44/88** FAIL and all 22 cells remain failed. Candidate exits 1 `CANDIDATE_TARGETS_FAILED_NOT_ACCEPTANCE`; do not call it Accepted. Target SHA256 `8fc763cd4df581e9faf2a0a10ad6c4ab78857448a7f59af342aefb20f1293112`, workload SHA256 `694f09001e4ba2b40ba4ed07f93786daa8cb8fc0baaf7eaa9753507415f86adc` unchanged.

## Remaining gates

At runtime GREEN HEAD, GitHub has reported the **push M7 Candidate** but the matching 12 inherited M0–M6 **pull-request workflows have not yet appeared**; old HEAD inherited 12/12 SUCCESS cannot replace same-source final Full. This evidence-ledger documentation commit itself will change the branch source SHA and requires fresh source-matched CI; no acceptance claim for the new HEAD. Before merge: protect the post-lock native/ACL Deny and transaction integrity, review code and locks independently, get all approved 22-cell p95/p99/throughput/sample requirements to PASS, run **separate actual** `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Acceptance -Targets tests/Load/m7-targets.json`, fresh inherited Full, artifact verification and explicit owner/reviewer signoff. Keep PR Draft and Issue #12 OPEN.

**Next blocker:** ~12.43 nonpooled SQL logins/request and remaining Orders p95 4.04–7.68s at 100 VU. Measured routing + framework currently accounts for ~4 physical opens/request, and current native+platform validation another ~7.43. Further optimization must be separately planned, regression-proven and never weaken fresh authority or SQL transaction consistency merely to fit targets.
