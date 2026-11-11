# Issue #15 — one native-owned SQL login per complete Platform + YesSQL validation

Status: owner-approved implementation experiment (2026-11-11); NOT security, performance, or production acceptance.

## Decision, scope, and testable hypothesis

Source baseline: `6cbd8525bd516d2ae9a63557f1c96deaa97370fe`, Issue [#15](https://github.com/chung6a8m/SaasOne/issues/15). Preserve frozen M7 workload/targets and prior FAIL history. A successful non-replayed Approve on the product API currently performs four full platform/native validations plus platform/customer routing (2), standalone customer profile (1), and caller-owned Orders transaction (1): approximately **12 unpooled physical SQL opens** on the specific code path. The global M7 observation (~12.43 opens/request) is a mixed-workload average, NOT an Approve measurement.

POC hypothesis: one NEW unpooled Default/YesSQL-owned SQL connection can be borrowed by EF Core *inside each complete validation*, changing four pairs of opens from 8 to 4, and thus one Approve from 12 to 8. No SQL session crosses a validation, HTTP request, lock boundary, or tenant. The existing native store remains YesSQL/OpenIddict; no SQL imitation or alternate authorization.

## Interfaces and ownership

- **Runtime:** `src/Platform/SaaS.Identity/{DefaultIssuerStore,IdentityAuthority}.cs` only. `DefaultIssuerStore` provides an internal, bounded, asynchronous callback that opens a fresh Default shell service scope and obtains `ISession.CreateConnectionAsync(ct)` from the actual configured Orchard/YesSQL 5.4.7 store. The native ISession owns and disposes that connection, with `Pooling=False`.
- The callback requires the Default fixed source, platform catalog, runtime SQL principal, open connection, no native transaction, and current shell settings. Fail closed for mismatched, closed, or already-transactional connections. The callback may not change DB, start a transaction, or cache a native/EF entity beyond this validation.
- `IdentityAuthority.ValidateCoreAsync` borrows the same `SqlConnection` only through `TenancyConnections.PlatformContext(SqlConnection)` with `AsNoTracking`; it reads all seven platform entities, then performs all native account/stamp/lockout/authorization/token checks, then the second current-tenant SELECT for product API. No overlapping commands. The borrowed DbContext does not own the underlying connection. Each next validation obtains a separate Default scope and physical SQL login.
- Unchanged: `TenantTransactionCoordinator`, Orders/Approvals/Audit transaction, lock order, ACL and post-lock Deny, web cookie and bearer claims, tenant routing, DB/schema migration ownership, hard connection-pool disabling, retry, and M7 targets. No unit-of-work refactor, cache, or pooling in this PR.

## Mandatory RED -> GREEN and security evidence

1. Commit **tests before runtime**: existing SqlM2 same-HTTP-context authority and SQL-login disable/membership/tenant-switch tests must now demand exactly **one** physical open per full validation (native 1, EF fresh 0). Add a supplemental real `SqlIssue15` HTTPS Approve case demanding 8 physical opens and four separate native rechecks. Keep the original SqlM7 category and its seven-case manifest exactly unchanged; run the new POC in its own SQL gate. This test-only commit must fail on old two-open runtime, not be changed to fit a result.
2. Implement only an actual YesSQL-borrowed transport. Reject fallback to an extra EF connection or pooled principal. Fail closed on non-SqlConnection, wrong source, active native transaction, cancellation, and disposal mismatch; native session remains owner. Do not cache Allow, native entities, current tenant, or reuse results across Orders lock boundaries.
3. Run pinned real SQL: `scripts/check-m2.ps1`, M3/M4/M5 regression SQL gates, `scripts/check-m7.ps1 -Mode Candidate -Targets tests/Load/m7-targets.json` and inherited Full, at exact source SHA. Preserve seven named mandatory M7 cases and all new tests; zero skips. Check double-validation in one HTTP context, disable/enable SQL runtime login between validations, membership/native token revoke, tenant switch, post-lock ACL Deny, replay, rowversion, approval and audit/receipt rollback.
4. Compare actual physical SqlClient open events, per-stage SQL commands, Approve 12 -> 8, per-tenant/operation p50/p95/p99, Orders lock wait vs hold, SQL CPU/waits, and CI-vs-representative topology. No retrospective threshold changes; no production SLO claim.
5. If the actual native connection factory/provider or security semantics cannot support the bounded callback, stop: retain tests as RED and explicitly document blocker. Do NOT adopt cross-validation shared connection, synthetic YesSQL, global pooling, or skipped authority.

Migration owner: none. A PR is REVIEW-REQUIRED; do not merge or close Issue #15 without independent security review and run-linked evidence.
