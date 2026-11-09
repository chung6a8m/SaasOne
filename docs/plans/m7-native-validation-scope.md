# M7 native validation scope plan

Spec: roadmap revision 1.3 M7/section 8, AGENTS, approved m7-target-approval.md. Continues the owner direction recorded in Issue #12 comment 6729373692. No new latency/workload/timeout default or ADR promotion.

## Evidence and choice

Routing implementation 7e0bac659cd3e719d70ecc8237f67e8f1d5dd4a8 passed 47/47 SQL M2, including one platform/two customer-route readiness connections on successive requests and fail-closed disabled runtime logins. PR run 42541749293, artifact 13641798011 at 63c422eec7f9f87b16e8128abac07cbd08aba701 still fails all 22 cells/88 metrics: 31.090 opens/request, 9.4833 measured completions/s, per-cell p95 8.456–19.624 seconds. This is correctness evidence, not acceptance.

The separate SqlM1 diagnostic at 71e4210853c7da761a3e1fc18c4b0d0553fc408a ran equal 32-worker/256-connection batches. Artifact 13641669966 / run 42542863589: async/sync/sync/async elapsed 767.1632/636.8459/655.8389/657.1549 ms. Open means 88.046/70.698/73.685/75.620 ms. Every mode used 256 fresh unpooled opens and queries, zero errors. After warm-up the difference is small; this does NOT justify a custom synchronous production scheduler, CPU-count spoofing or an unsafe pool. Do not call the driver queue a proven dominant bottleneck.

The first diagnostic implementation at 32b8d001dc6ccd4c67b539ac8110f0abae105424 failed fixture initialization because a second class fixture tried to bootstrap the same SQL database. It did not measure either mode and remains FAIL. The correction uses the existing M1SqlTests partial class. No production runtime was changed by that correction.

Chosen next change: combine the already-consecutive native account and authorization reads in IdentityAuthority.ValidateAsync into ONE fresh Default issuer service scope/YesSQL session per validation. User and authorization are different entity types; each is still queried and checked in original order. This is not reuse across validations or requests. The later bearer access-token validation remains a separate fresh scope, including its repeated authorization read. Do not combine it here or cache any entity/Allow result.

## Files and interfaces

- src/Platform/SaaS.Identity/DefaultIssuerStore.cs: add ValidateAccountAndAuthorizationAsync(string subject, string stamp, string authorizationId, CancellationToken). Initially delegate to existing two methods for an unchanged-behavior RED baseline. After RED, extract private provider-taking check bodies; existing public methods still open their own scopes; combined method opens one scope and runs account then authorization checks. Preserve query predicates, native APIs, error codes and cancellation.
- src/Platform/SaaS.Identity/IdentityAuthority.cs: replace only the two consecutive calls in ValidateAsync with the combined method; retain all seven platform queries, all checks, web touch and owned platform connection.
- tests/Unit/SaaS.UnitTests/IdentityPoc/IdentityNativeScopeTests.cs: actual SQL/native fixture test compares the combined call to the existing two-call reference, requiring the same nonzero command count but one physical open. Repeated calls must execute fresh SQL; an invalid account must fail before an invalid grant; revoked native authorization must fail on the next call.
- tests/Unit/SaaS.UnitTests/IdentityPoc/IdentityConnectionLifetimeTests.cs: reference the combined native check, assert exactly one native open, then retain the existing one-platform-open assertion plus same-scope SQL-login disable and membership revocation tests.
- docs/verification/m7-native-validation-scope.md: append-only RED/GREEN/evidence ledger.

## Test-first gates

1. Commit adapter plus desired SQL tests, observe physical-open RED (expected 1, actual 2), not compilation/setup failure. Existing mandatory SQL inventories remain enabled.
2. Implement provider-body reuse and route only ValidateAsync through the combined method. No transaction/lock-order, model, table, permission, migration, database source or package change.
3. Run scripts/check.ps1 -Scope M0, scripts/check-m2.ps1, fresh M7 Candidate and all inherited workflows at the new SHA. Compare raw command/open counts and performance; do not infer acceptance from the focused regression.
4. Only after all approved targets pass run separate Acceptance with embedded Full, verify hashes/TRX/raw samples and obtain independent review/final sign-off. A performance failure remains FAIL and PR #14/Issue #12 remain unaccepted.

Review focus: every invocation creates a new native scope; account error precedes authorization error; the exact native authorization index predicate is unchanged; legacy public entrypoints retain ownership and behavior; a service-scoped manager/entity is never retained in a field. No cross-request or post-lock authority cache, global pooling, removed recheck, relaxed target or changed workload.

Runtime evidence comes from pinned CI; no local .NET/SQL execution is claimed. Migration owner: none added; all owner streams untouched.
