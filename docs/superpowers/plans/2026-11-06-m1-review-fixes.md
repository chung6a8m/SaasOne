# M1 PR #3 review remediation plan

**Goal:** Resolve review 5802373469 findings R1–R3 without expanding M1 into identity, distributed operations or a new secret-store implementation.

**Baseline:** `dc47350631b3131f2da156f6e42323cd8f3bd858`, roadmap revision 1.3, ADR 0001–0006, M1 runbook. Work stays on `feat/m1-tenancy-provisioning`; no direct main writes. Historical review/evidence remains intact.

**Architecture:** Preserve native Orchard routing and fail-closed request validation. Treat customer configuration errors as tenant-local, while platform failures remain global. Preserve mixed native-feature/package dependency paths without adding artificial owner↔feature cycles. Retain process-environment secrets: no-restart registration requires the runtime secret to be present when the HTTP process starts; otherwise a controlled restart is mandatory. The latter is an implementation scope choice, not a claim of dynamic secret delivery.

**Stack/ownership:** Existing pinned .NET/Orchard/SQL/PowerShell graph; no package, EF model, snapshot or migration changes. Web owns shell loading; Build owns graph validation; test-only process helpers stay in tests. Identity remains Conditional pending M2 PoC.

## R1 — Isolate customer configuration failures

Files: `src/SaaS.Web/Orchard/RegistryShellSettings.cs`, `tests/Unit/SaaS.UnitTests/M1RuntimeProcessTests.cs`, partial declaration in `M1SqlTests.cs`.

Consumes the existing `IShellSettingsManager`, `TenancyConnections`, registration validation and lifecycle contracts. Public signatures do not change. Full inventory catches only customer contract failures around individual customer settings; platform query/bootstrap failures propagate. Named lookup queries only its own registration. Name enumeration must not resolve customer secrets. Nonoperational tenants are not registered.

- [ ] Add real-SQL cold-process tests for missing/wrong-target/wrong-principal Alpha secrets in Active and Suspended states; Alpha must return 503, Beta and platform 200.
- [ ] Add named-lookup/name-enumeration regression and invalid-customer-registration isolation; retain a negative test proving corrupt platform baseline closes all readiness.
- [ ] Observe the regressions fail on baseline, implement the isolated loader, and rerun the real SQL gate.

## R2 — Preserve mixed graph paths

Files: `tools/SaaS.Build/FeatureGraph.cs`, `tests/Architecture/SaaS.ArchitectureTests/MixedFeatureGraphTests.cs`.

Consumes `DependencyNode`/`NativeFeature`; returns the same combined graph contract. Feature nodes inherit their owning node's ORIGINAL project/package dependency edges, not the expanded owner node or its sibling feature edges. Native edges remain intact. This avoids the trivial owner↔feature cycle while preserving transitive mixed paths.

- [ ] Add direct/transitive feature→package→business failures for Platform, Theme and Orders→Approvals, plus a valid same-owner feature-chain case.
- [ ] Observe failures before the fix, implement the missing edges, and run the entire foundation/architecture gate against real compiled metadata.

## R3 — Test and document process-environment boundaries

Files: `M1SqlTests.cs`, `M1RuntimeProcessTests.cs`, `scripts/m1-sql-fixture.ps1`, `README.md`, `docs/runbooks/m1-tenancy-provisioning.md`, `docs/verification/m1.md` and the original M1 plan.

Replace the in-process hot-registration proof with a runtime child started with a preloaded secret and a separate maintenance child. Add a negative case where a secret added to the parent AFTER runtime launch is unavailable until controlled restart. Runtime children retain only runtime SAAS settings; child output is drained but never published. These prove process/credential separation, not separate CI OS accounts.

- [ ] Run both process scenarios with actual SQL and Orchard; verify selected TenantId, unaffected tenants and platform readiness.
- [ ] Tighten SQL evidence inventory and counts; remove unconditional no-restart claims from current documentation/PR description without rewriting historical results.

## Verification and completion

Required: locked restore/Release build; `scripts/check.ps1 -Scope M0`; both EF snapshot checks; all PowerShell parsing; `scripts/sql-fixture.ps1`; `scripts/m1-sql-fixture.ps1`. Run the whole suite, not just new tests. Capture exact head/merge-ref SHA, discovered/executed/passed/failed/skipped and CI run/artifact IDs. Missing tools are not PASS. This session uses connector-backed commits and GitHub Actions because local dotnet/pwsh/Docker are absent; no separate reviewer agent is available.

- [ ] Inspect final diff and evidence, reply to each original inline finding with its fix and regression result, and record remaining limits. Merge/production deployment is not performed by this remediation plan.
