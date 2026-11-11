# M7 implementation plan

Spec: roadmap revision 1.3, M7 / sections 8 and 9.1; issue #12; AGENTS.md.
Base: main `7843cf0c5b38644875529ee5c55f1805d6c2da2a` (M6 closure recorded).
Branch: `codex/m7-acceptance-reuse`. Owner reviews architecture/security, target selection and merge. No ADR promotion or production approval.

## Decisions and boundaries

Implement a read-only Portal sample with its own host and theme. It consumes the fixed platform connection and existing platform schema/release/runtime-principal guards; it exposes no tenant business, identity-management or setup endpoint. It is an independent composition example, not a second business domain. Adding it must not change `src/Platform`, `src/Modules` or the original product's guard behavior. Simply disabling Orders is rejected.

Use a dedicated native Kestrel HTTPS test fixture with real Orchard/OpenIddict and SQL Server, not a mock identity or SQLite. Credentials/certificates are disposable and private to the test process; only sanitized aggregate evidence is exported. Reuse existing controlled provisioning and migration owners, never runtime migrations. Baseline and fault/lifecycle scenarios are separate. Load defaults are proposals: 25 VUs/tenant, 100 total, ramp-up and bounded measured window; distinguish virtual sessions from the account pool.

## Task 1 — Independent product and template

Affected: `samples/Portal/{SaaS.Portal.csproj,Program.cs,packages.lock.json}`, `samples/Portal.Theme/{SaaS.Theme.Portal.csproj,Manifest.cs,PortalPage.cs,packages.lock.json}`, `tests/Architecture/SaaS.ArchitectureTests/M7ProductSourceTests.cs`, `scripts/check-m7-product.ps1`, `templates/module/**`, product/module runbook.

Consumes: `TenancyConnections.Platform("runtime")`, `TenancyConnections.PlatformContext`, `TenancyConnections.VerifySchemaAsync`, `SqlOperations.RequireRuntimeAsync`, `PlatformReleaseBarrier.DemandClearAsync`, native `IExtensionManager.GetFeatures()`. Provides: independent `SaaS.Portal.dll`, `/health/live`, guarded `/health/ready`, read-only `/`, and `--feature-graph` JSON with id/extensionId/dependencies. Own theme must be present; Orders/Approvals/original host must be absent from source/project, resolved package, native feature and publish graphs.

Test cycle: source-closure test first (missing product must fail), then product implementation. Dedicated gate performs locked restore, publish, native graph inspection and artifact inspection. SQL tests start the actual published process with only low-privilege runtime secrets and prove ready/render, missing/wrong/overprivileged source and platform-release failures. No connection values in process arguments or evidence. Template has no persistence/feature entitlement by default, no business reference, and documents a new migration owner's obligation before adding data.

## Task 2 — Four-tenant correctness

Affected: `tests/Unit/SaaS.UnitTests/M7/` fixture, matrix, concurrency, lifecycle and published-product tests; `scripts/m7-sql-fixture.ps1`, `scripts/m7-mandatory-cases.json`; `scripts/check.ps1` only excludes the new SQL category from the non-SQL job, while the dedicated gate requires its exact inventory.

Consumes: existing `M1SqlFixture`, `M1SqlEnvironment`; `IdentityInstaller.InitializeAsync/EnrollUserAsync`; `IdentityDeployment`; `IdentityRuntimeHost.Configure/Use`; `PlatformReleaseRuntimeGate.EnterAsync`; `AccessMaintenance.InstallAsync` / `Operations.ChangePlanAsync`; existing ACL fixture seeding; native HTTP `/connect/authorize`, `/connect/token`, `/{slug}/orders`, `/{slug}/api/v1/orders` and edit/submit/approve/reject commands using original expectedRevision/expectedVersion and Idempotency-Key. Provides: Category=SqlM7 TRX and sanitized matrix report; no new production API.

Tests: real Alpha/Delta Standard, Beta/Gamma Approval; same number/different data; all cross-token/URL combinations; forged body tenant; shared account with different roles and Gamma Allow plus Deny; no membership and Alpha-only; UI/API consistency; same-revision updates and approvals yield one winner; lost-response replay yields one audit/decision; Delta lifecycle separate from all-Active load; operator uses existing private guarded channel. Inherited M0-M6 suites remain mandatory for final acceptance, including restore/audit/cache/refresh/revocation failure cases.

## Task 3 — Load and evidence

Affected: `tests/Load/m7-baseline.json`, M7 load driver/metrics tests under `tests/Unit/SaaS.UnitTests/M7/`, `scripts/check-m7.ps1`, `scripts/m7-sql-fixture.ps1`, `.github/workflows/m7.yml`, `docs/verification/m7.md`, load/bottleneck runbook.

Provides: schema-versioned sanitized JSON, per-tenant/per-operation sample counts, p50/p95/p99/max latency, throughput, business failures, unexpected 5xx/transport failures and concurrency configuration. Record source SHA, clean checkout, exact commands/exit codes, hardware/OS/SDK/SQL image digest, counts and hashes. Never print raw request/response bodies, tokens or SQL/certificate secrets. Fault tests' expected 4xx/503 are not successful baseline transactions.

Failure tests: missing tenant/workload/sample, non-finite/empty statistics, zero discovery/mandatory skip, malformed/missing targets or source mismatch. Baseline mode cannot return an acceptance PASS. Acceptance mode requires a previously committed target document tied to a baseline artifact, with source/time provenance checked before starting the load; target choice is owner-reviewable and never retrofitted to the same run. A missing target blocks acceptance rather than inventing a threshold.

## Commands and acceptance mapping

- `pwsh -NoProfile -File scripts/check-powershell-syntax.ps1`.
- `pwsh -NoProfile -File scripts/check.ps1 -Scope M0` (includes architecture and non-SQL tests).
- `pwsh -NoProfile -File scripts/check-m7-product.ps1` (independent restore/publish/feature/artifact gate).
- `pwsh -NoProfile -File scripts/m7-sql-fixture.ps1 -Mode Baseline` (disposable real SQL and mandatory M7 correctness/load inventory).
- `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Baseline` and later `-Mode Acceptance -Targets <committed-file>`; final gate additionally runs inherited `scripts/local-verify.ps1 -Scope Full` at the same source.

Issue #12 mapping: plan=all task contracts above; matrix/isolation=task 2 plus inherited suites; load=task 3; product=task 1; reuse/runbooks=tasks 1/3; final dossier=exact-source evidence and owner decision. Only the last decision closes M7. Main and historical M6 evidence remain unchanged during implementation.

## Migration owner and execution ledger

No new table/model/snapshot/migration. Tenancy, Orchard/YesSql, AccessControl, Auditing, Orders and Approvals keep their existing exclusive streams. Fixture administrators only seed disposable data through the existing fixture patterns.

Source archive from planning SHA `765a81cafd2d211fe05d8c579a248ced63128c20` was downloaded and SHA256 verified for inspection. Local snapshot SHA is not remote runtime provenance. Local dotnet/pwsh are unavailable, so runtime evidence is collected on the pinned CI runner. Product source-contract RED is the next gate; no implementation or M7 PASS is claimed by this plan.
