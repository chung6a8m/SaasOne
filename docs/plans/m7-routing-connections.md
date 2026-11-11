# M7 routing verification connection plan

> Execute inline, test-first, on codex/m7-acceptance-reuse. This plan precedes runtime implementation.

Goal: continue the owner-approved performance work without relaxing the M7 budgets. Spec: roadmap revision 1.3 M7 and section 8, AGENTS.md, m7-performance-completion.md and m7-target-approval.md. Starting head: 4d84ad9cbd44abe330d511b1e13e7ccc54304ce8. Owner continuation/approval is recorded in Issue #12 comment 6729373692; this is not final acceptance or ADR promotion.

## Diagnosis and design

The last same-workload PR measured 43.142 physical opens/request and 15.677–32.079-second per-cell p95, with correctness PASS and all performance targets FAIL. The two prior bounded lifetimes are retained. Source tracing identifies independent opens for bootstrap, principal check, schema, native foundation, identity installation and registry selection in IdentityRuntimeBoundary. Its tenant branch separately opens for identity, native foundation, repeated identity, principal, schema and baseline. These are sequential queries within one pre-routing verification pass, not distinct post-lock authorizations.

Chosen design: one unpooled platform connection and, for a customer route, one unpooled customer connection for this pass. Every existing query and its ordering remains. Return the verified shell selection only after disposing both connections; no transport flows into Orchard/downstream business processing. Each next request establishes fresh SQL logins. Standalone string-taking helpers remain compatible and own their connections; new connection-taking helpers borrow and never dispose an already-open connection. There is no ambient/static connection registry or authority cache.

Rejected: global pooling (changes login-disable/source-failure guarantees); skipping repeated checks or caching a ready result (changes authority); changing target/load/deadline (not an optimization). This step does not alter native issuer validation, current-actor callbacks, transaction boundaries, post-lock checks or lock ordering.

## Global constraints

Keep approved targets and tests/Load/m7-baseline.json byte-identical. Preserve Pooling=false, fixed platform and shell-selected customer sources, least privilege, bootstrap/identity/schema/profile/release checks and fail-closed behavior. No runtime DDL, dependency/model/index/migration changes. All prior failures remain historical. Missing mandatory tests, skipped mandatory tests or missing prerequisites are failures, not successful skips.

## Task 1 — behavioral RED

Affected: tests/Unit/SaaS.UnitTests/IdentityPoc/IdentityRoutingConnectionTests.cs (existing SqlM2 collection/partial class).

- [ ] Exercise the actual product host with real SQL. Warm native shell activation separately, then require platform readiness to use one physical open and customer readiness two on EACH successive request; require nonzero fresh SQL commands and no pending/error events.
- [ ] Assert fresh SQL source failure after disabling/re-enabling the customer and platform runtime logins on the disposable fixture. Restore logins in finally. The next request must not reuse a prior connection or ready decision.
- [ ] Retain inherited startup, wrong-target/identity, schema/profile, callback, membership/native revocation and lifecycle tests. Observe the open-count test fail against existing production code, not compilation.

## Task 2 — bounded pre-routing lifetimes

Affected runtime files:
- src/Platform/SaaS.Tenancy/Data/TenancyConnections.cs: add CustomerContext(SqlConnection) mirroring existing PlatformContext(SqlConnection); borrowed transport.
- src/Platform/SaaS.Tenancy/Operations/SqlOperations.cs: add RequireRuntimeAsync(SqlConnection, CancellationToken); string wrapper owns/disposes and delegates.
- src/Platform/SaaS.Tenancy/Operations/TenantDatabaseVerifier.cs: add SqlConnection overloads for VerifyIdentityAsync and VerifyAsync; same registration validation, SQL checks and error ordering; string wrappers own/dispose and delegate.
- src/SaaS.Web/Orchard/FoundationProfile.cs: add ReadSnapshotAsync(SqlConnection, CancellationToken); preserve original query/parsing code and string wrapper.
- src/SaaS.Web/Orchard/IdentityInstaller.cs: internal VerifyAsync(PlatformDbContext, IdentityDeployment, CancellationToken), and migration-hash reader borrowing its connection; retain string-based maintenance compatibility.
- src/SaaS.Web/Orchard/IdentityRuntimeHost.cs: extract selection into a private method whose owned connections are disposed before native shell routing/downstream execution. Return null only for existing unknown-tenant 404 behavior. Preserve callback checks and every source query.

- [ ] Implement only after RED. Run all inherited workflows and fresh M7 Candidate at the new SHA.
- [ ] Check source selection, disposal on success/error/cancellation, unknown tenant, repeated requests and disabled logins. No borrower may close or replace an external transaction/connection.

## Review focus

1. Disposed/closed or wrong-database borrowed handle must fail closed, never select a fallback.
2. New scope must end before next(context), not retain transport across current-authority rechecks.
3. Every public string overload must still dispose on success/error/cancellation; preserve validation before SQL where it previously occurred.
4. Shell reload, native activation, 404 and callback-error handling must remain unchanged.
5. Full source-bound evidence is required even if the focused SQL regression passes; standalone source snapshots are not runtime evidence.

## Task 3 — evidence and next decision

Commands: scripts/check-powershell-syntax.ps1; scripts/check.ps1 -Scope M0; scripts/check-m2.ps1; scripts/check-m7.ps1 -Mode Candidate -Targets tests/Load/m7-targets.json; inherited Full via scripts/local-verify.ps1 -Scope Full. Runtime execution uses pinned CI because this editing environment has no dotnet/pwsh/Docker. Download/verify artifact hashes and exact TRX inventory, compare opens/request and recompute raw load statistics. Keep PR and branch evidence distinct. Record results in PR #14 / Issue #12 and an append-only verification ledger.

Only after all approved targets pass, execute separate Acceptance with embedded inherited Full at its own SHA, artifact verification, independent review and final sign-off. If insufficient, report the remaining measured bottleneck and scope the next change before implementation. No optimization is assumed to meet all targets merely because its regression passes.

Migration owner: none added; all existing owner streams untouched.
