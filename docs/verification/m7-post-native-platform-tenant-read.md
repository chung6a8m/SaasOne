# M7 post-native platform tenant read — RED→GREEN evidence (2026-11-09)

**Decision: correctness regression GREEN; M7 Candidate performance FAIL. PR #14 must stay Draft/unmerged, Issue #12 stays OPEN.** This is neither production capacity/SLO certification nor M7 Acceptance. Source-locked target and 100-VU workload files are unchanged.

## Scope and unchanged boundaries

Plan: `docs/plans/m7-post-native-platform-tenant-read.md`, committed at `13fe1e36902177dc2b2f52bf988676efadec2e64`. Affected runtime only:
- `src/Platform/SaaS.Identity/IdentityAuthority.cs`
- `src/Platform/SaaS.Identity/IdentityProtocol.cs`
- `src/SaaS.Web/Access/ApplicationRequestAuthority.cs`

At each *new* bearer-validation invocation, the existing unpooled, fixed-source platform `PlatformDbContext` is kept open until a **second, fresh, no-tracking post-native SELECT** of the current platform tenant. The API's current Plan/RegistryVersion/AuthorizationGeneration/RetiredAt/TenantStatus result is a minimal nonsecret DTO, not a tracked EF entity or SQL connection. The earlier identity tenant SELECT and every JWT signature, issuer, audience, scope, native account/lockout/stamp, token, authorization, invalid-access-index, membership/session, tenant-generation and claim-binding check are still executed. On every subsequent ACL or Orders post-lock recheck, a *new* platform/native/customer session is created. Web/cookie paths retain the former independent fresh plan lookup. The product still checks tenant operational status, retirement, family/registry version and current schema/application-profile plan/entitlement. No runtime migration/index/schema/role/pooling, Deny, Orders/Approval resource-lock/transaction, audit/receipt, target, 100-VU config or tenant population changes.

The only avoided operation is a redundant **physical platform connection open**: the fresh post-native tenant query now executes on the existing owned connection. There is no cross-request native/ACL cache, no cached Allow, and no promise that a concurrent external mutation after the final source read becomes retroactively visible.

## RED, original tested source

- RED test commit `921b750aca720bf32733aa9016235dc9f8ab879f`, PR synthetic merge `711213cec7010c12a789045f52129879445f87ec`.
- [M7 RED run 42582539278](https://github.com/chung6a8m/SaasOne/actions/runs/42582539278), [artifact 13657693462](https://github.com/chung6a8m/SaasOne/actions/runs/42582539278/artifacts/13657693462).
- Locked Release build **0 warnings/errors**; 27/27 architecture and 201/201 non-SQL unit PASS; M7 SQL/HTTPS **6/7 PASS**, 0 skipped. Exactly one intended RED: existing mandatory `Four_active_tenants_isolate_same_number_data_and_all_cross_token_routes` measured **two** actual `acl.authorize` stage invocations but **four** physical SQL opens (expected two). All original cross-tenant/forged request assertions were retained; no mandatory test names changed. M7 did not pass correctness, so RED is not usable as a load Candidate.
- Independently downloaded outer ZIP SHA256 `1b1aaf72f24df192911f56703c19e42b0eda874c6aa5ead6301faa7411e512f3`; inner ZIP SHA256 `d428e7f99ff962502bf837822406dafd86ccf679cd0608f84adde4268b7f4806`, matching sidecar; TRX independently recomputed **7 total, 6 passed, 1 failed, 0 skipped**. Other inherited workflow failures at this RED head were pinned PowerShell download/setup failures, not treated as behavioral RED.

## GREEN runtime and current-source Candidate

- Runtime GREEN commit `98c338c70a53733f5a9635f86286d59d18368231`; exact PR synthetic merge tested `bce84aea10fd38e3c7ecf579027ad5a699392e15`.
- [M7 GREEN run 42583352771](https://github.com/chung6a8m/SaasOne/actions/runs/42583352771), [artifact 13657862996](https://github.com/chung6a8m/SaasOne/actions/runs/42583352771/artifacts/13657862996).
- Locked Release build **0 warnings/errors**, 27/27 architecture + 201/201 non-SQL unit + **7/7 mandatory SQL/HTTPS** PASS, no mandatory skipped/failed. Product/template/policy gates PASS. 2,376 successful all-phase raw HTTP samples, 0 unexpected failures, no missing mandatory cells or integrity failures.
- Outer artifact SHA256 `7f49ea148d3ddad9e307a7be06903e6dd934cf4d86bf1404114a3c4a518e4a3c`; inner SHA256 `9323fa4adb70184621890c31c858ba33ac4fcb9a1aebda7657eeb187103c777f`. ZIP sidecar and **24/24** manifest length/SHA256 values independently verified; exact 7/7 M7 TRX and all **22 cells** raw measured-window sample counts, nearest-rank p50/p95/p99/max and successful completion throughput independently rederived with zero discrepancies.

## Source-matched performance observations

Previous clean source `77928e030f935f399fe3270cd3f9e8251174a543`, synthetic PR merge `0a8a302fbec89b7cd9f07c0dc0d623a0a0a2b3b7`, [run 42577730745](https://github.com/chung6a8m/SaasOne/actions/runs/42577730745), artifact `13656725051` (outer SHA256 `375e7e781a43c558d2cdd686c8a7d480fe80209753b538062d46039f75a0c776`; inner SHA256 `489fb7f600b12f4885fc812dce90726f7431c33a2b8de7be9e034f7d7278e22b`).

| Metric | Before | GREEN |
| --- | ---: | ---: |
| Completed all-phase HTTP samples | 1,833 | 2,376 |
| Physical SQL opens/request | 21.40098 | **17.04545** |
| Physical opens / `acl.authorize` invocation | **12,282/6,141 = 2** | **7,956/7,956 = 1** |
| Application SQL commands/request | 119.89634 | 119.83039 |
| Measured completed request throughput (across 22 cells) | 10.1917/s | 13.9667/s |
| Per-cell p95 range | 7.136–17.255s | 5.949–12.202s |
| Measured starts/cell | 47–67 | 62–90 |
| Failed approved performance cells/metrics | **22/88** | **22/88** |

Confirmed engineering effect: **exactly one fewer physical login for each current API ACL authority recheck**, supported by a real failing-before/passing-after SQL assertion and test-only stage profile. The different throughput/p95 across independent CI runners remains an observation, not a guaranteed causal capacity gain or production promise. Candidate status remains `CANDIDATE_TARGETS_FAILED_NOT_ACCEPTANCE`, exit 1. Owner-approved target SHA256 `8fc763cd4df581e9faf2a0a10ad6c4ab78857448a7f59af342aefb20f1293112`; locked workload SHA256 `694f09001e4ba2b40ba4ed07f93786daa8cb8fc0baaf7eaa9753507415f86adc` are unchanged.

## Remaining bottleneck and acceptance boundary

Source-matched GREEN stage distributions remain high: `acl.authorize` p95 **1.608s**, `orders.lock.resource` p95 **2.062s**, `orders.lock.page` p95 **3.811s**, Orders read/write p95 **7.744/7.482s**. Live sampling still records Orders APPLICATION/KEY lock contention. These are overlapping/aggregate stages, not unique CPU or transaction owner measures. Do **not** drop per-order locks, post-lock fresh authority, resource integrity, historical approval requirements, Deny, replay/audit/rollback or writegate fences merely to improve a score; any next optimization requires its own source-locked plan and security regression.

Current-head inherited M0–M6 workflow outcomes must be checked at the final source SHA; historical PASS is not substitutable. A distinct actual `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Acceptance -Targets tests/Load/m7-targets.json` with approved budget, fresh same-SHA inherited Full, independent review and explicit owner signoff is still mandatory for any merge or Issue #12 closure. **Do not mark M7 accepted while 22/88 fail.**
