# M1 PR #3 review remediation plan

**Goal:** Resolve review 5802373469 findings R1–R3 without expanding M1 into identity, distributed operations or a new secret-store implementation.

**Baseline:** `dc47350631b3131f2da156f6e42323cd8f3bd858`, roadmap revision 1.3, ADR 0001–0006, M1 runbook. Work stays on `feat/m1-tenancy-provisioning`; no direct main writes. Historical review/evidence remains intact.

**Architecture:** Preserve native Orchard routing and fail-closed request validation. Treat customer configuration errors as tenant-local, while platform failures remain global. Preserve mixed native-feature/package dependency paths without adding artificial owner↔feature cycles. Retain process-environment secrets: no-restart registration requires the runtime secret to be present when the HTTP process starts; otherwise a controlled restart is mandatory. The latter is an implementation scope choice, not a claim of dynamic secret delivery.

**Stack/ownership:** Existing pinned .NET/Orchard/SQL/PowerShell graph; no package, EF model, snapshot or migration changes. Web owns shell loading; Build owns graph validation; test-only process helpers stay in tests. Identity remains Conditional pending M2 PoC.

## R1 — Isolate customer configuration failures

Files: `src/SaaS.Web/Orchard/RegistryShellSettings.cs`, `tests/Unit/SaaS.UnitTests/M1RuntimeProcessTests.cs`, partial declaration in `M1SqlTests.cs`.

Consumes the existing `IShellSettingsManager`, `TenancyConnections`, registration validation and lifecycle contracts. Public signatures do not change. Full inventory catches only customer contract failures around individual customer settings; platform query/bootstrap failures propagate. Named lookup queries only its own registration. Name enumeration must not resolve customer secrets. Nonoperational tenants are not registered.

- [x] Add real-SQL cold-process tests for missing/wrong-target/wrong-principal Alpha secrets in Active and Suspended states; Alpha must return 503, Beta and platform 200.
- [x] Add named-lookup/name-enumeration regression and invalid-customer-registration isolation; retain a negative test proving corrupt platform baseline closes all readiness.
- [x] Observe the regressions fail on baseline, implement the isolated loader, and rerun the real SQL gate.

## R2 — Preserve mixed graph paths

Files: `tools/SaaS.Build/FeatureGraph.cs`, `tests/Architecture/SaaS.ArchitectureTests/MixedFeatureGraphTests.cs`.

Consumes `DependencyNode`/`NativeFeature`; returns the same combined graph contract. Feature nodes inherit their owning node's ORIGINAL project/package dependency edges, not the expanded owner node or its sibling feature edges. Native edges remain intact. This avoids the trivial owner↔feature cycle while preserving transitive mixed paths.

- [x] Add direct/transitive feature→package→business failures for Platform, Theme and Orders→Approvals, plus a valid same-owner feature-chain case.
- [x] Observe failures before the fix, implement the missing edges, and run the entire foundation/architecture gate against real compiled metadata.

## R3 — Test and document process-environment boundaries

Files: `M1SqlTests.cs`, `M1RuntimeProcessTests.cs`, `scripts/m1-sql-fixture.ps1`, `README.md`, `docs/runbooks/m1-tenancy-provisioning.md`, `docs/verification/m1.md` and the original M1 plan.

Replace the in-process hot-registration proof with a runtime child started with a preloaded secret and a separate maintenance child. Add a negative case where a secret added to the parent AFTER runtime launch is unavailable until controlled restart. Runtime children retain only runtime SAAS settings; child output is drained but never published. These prove process/credential separation, not separate CI OS accounts.

- [x] Run both process scenarios with actual SQL and Orchard; verify selected TenantId, unaffected tenants and platform readiness.
- [x] Tighten SQL evidence inventory and counts; remove unconditional no-restart claims from current documentation without rewriting historical results. Carry the corrected contract into the PR description.

## Verification and completion

Required: locked restore/Release build; `scripts/check.ps1 -Scope M0`; both EF snapshot checks; all PowerShell parsing; `scripts/sql-fixture.ps1`; `scripts/m1-sql-fixture.ps1`. Run the whole suite, not just new tests. Capture exact head/merge-ref SHA, discovered/executed/passed/failed/skipped and CI run/artifact IDs. Missing tools are not PASS. This session uses connector-backed commits and GitHub Actions because local dotnet/pwsh/Docker are absent; no separate reviewer agent is available.

- [x] Inspect final diff and evidence, reply to each original inline finding with its fix and regression result, and record remaining limits. Merge/production deployment is not performed by this remediation plan.

## Execution ledger

Tests-first R1 commit `da97d92cb7fc02f853b8655d2ec5cbff34126f09` produced eight expected SQL failures in run `42244862367` (40 passed / 48 executed). R1 fix plus R2 regression commit `af505b13e0956968587e90e0ab545cb6f853f38d` produced six expected architecture failures in run `42245646099` (20 passed / 26 executed). Implementation `999a4f4cf9e7f5b8c7f44fade78f95f23b183763` passed exact-head run `42246212780` and PR merge-ref run `42246223331`: 74 unit + 26 architecture + 48 real SQL = 148 passed, zero failed/skipped; build, snapshots, parsing, graph, hygiene, locks and both SQL fixtures passed. The PR artifact's ZIP hash, generated JSON and actual TRX counters were inspected. See [source-bound evidence](../../verification/m1-pr3-review-fixes.md) for identifiers, hashes and operational constraints. Later documentation-only head/merge verification is recorded on the PR.
