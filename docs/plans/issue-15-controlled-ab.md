# Issue #15 — controlled, no-profiler Orders List P3 A/B

**Status:** experimental test-harness-only branch; do NOT merge this harness into production or the official M7 acceptance gate. Workload and frozen approved SLO unchanged. PR #16 and PR #17 merged as conditional functional acceptance; PR #18 stays Draft.

## Controlled comparison

The question is whether P3's grouped-history single SQL query improves end-to-end List p95 relative to the original three post-lock SELECTs. Earlier independent CI runs contradicted each other, and a deep-history SQL test showed similar logical reads but slightly higher CPU with P3. Command count alone is not sufficient.

The workflow selects two pinned **production source** versions and swaps only src/Modules/SaaS.Orders/OrderService.cs on a single pinned ubuntu-22.04 runner:

- A: e7aab2c07c3d8807ed99cb8f515c2f84638b49d2 (post-merge PR #16+#17 control).
- B: 37d3d9fd42881ce1476a1e891331d8fff30598fd (PR #18 candidate).

The benchmark harness is identical in both arms. It sets a **test-only** environment variable SAAS_M7_AB_NO_PROFILER=1 to prevent M7SqlProfileSession, Activity listener and DMV sampling during measurement. Production behavior is not changed; neither SQL connection/authorization nor Orders locking is disabled.

The test runs the **original** real Kestrel HTTPS, Orchard/OpenIddict and SQL Server 100-VU/4-tenant load: 25 VU per tenant, 30s ramp, 120s measured, 250ms think and the same five/six operations. Each trial uses a fresh pinned SQL Server instance and a newly seeded but identically shaped four-tenant fixture. All six trials run sequentially on one runner in A B B A A B order, three samples per arm. The application/test harness binary is identical except for that one production OrderService file. Trial manifests capture the exact source commit and file SHA-256; load.json.sourceSha refers to the unchanged benchmark harness, not to the swapped runtime variant.

## Evidence and decision

Capture native HTTPS load.json, measured request samples, 22 cell distributions, process CPU, active VUs, errors and a per-trial source manifest. Repetitions on one CI runner reduce—but do not eliminate—source-independent scheduling and stochastic-dataset variance. The run cannot claim identical row IDs, production representative topology or hardware A/B beyond its explicit conditions.

Use the median of three run-level List p95s for each tenant, plus List throughput and process CPU. If ANY original 22 operation cells misses the approved minimum **100 measured requests**, classify the result as inconclusive, not a passing SLO. If P3 worsens List p95 by >=5% in two or more tenants, or increases CPU >=10%, recommend REVERT. If it improves p95 by >=5% in >=3 tenants, none worsens >=5%, and CPU increase <=5%, keep as an experiment requiring representative production validation. Otherwise default to INCONCLUSIVE/REVERT rather than assert a speculative speedup. These engineering A/B decision heuristics DO NOT modify original M7 frozen target thresholds.

All unexpected transport, authorization, application and integrity errors invalidate a trial. Missing/zero results, modified workload, source mismatch, or profiler enabled fail the gate. Preserve raw evidence and never label supplemental result M7 official Acceptance. The official M7 Candidate remains provenance-blocked and Issue #15 remains Open for performance/topology debt.

## Interfaces and migration owner

Only test-only M7LoadAcceptanceTests.cs, scripts/m7-sql-fixture.ps1's new explicitly opt-in AbLoadOnly mode, scripts/issue15-ab-evaluate.ps1 and a dedicated Actions workflow are involved. Default M7 mandatory inventory, original seven-case test/Issue15 7+1 suites, release barrier, SQL/tenant/ACL authorization, Orders transactions and schema are unchanged. Migration owner: none.