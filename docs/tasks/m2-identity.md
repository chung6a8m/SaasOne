# M2 — Central identity implementation plan

Baseline: `2e1dd5568c204e3422e8c9222771e7796cf0eb25` (merged M1 PR #3).
Specification: `docs/roadmap/saas-orchard-roadmap-v1.md` revision 1.3, sections 3, 4.1–4.4 and M2.
Branch: `feat/m2-identity-poc`; draft PR #4.

**M2 remains incomplete. ADR 0001 remains Conditional — pending the complete M2 PoC.** The project owner retains architecture/security acceptance and merge authority. A native-store subcomponent result is not an accepted identity architecture.

## Boundaries and execution order

Implement the cross-shell PoC first, on the pinned Orchard/YesSql/OpenIddict/.NET graph. No dependent production identity rollout before this gate passes. Do not replace Orchard Users/OpenID Server with a hand-written password/JWT endpoint. No shared tenant cookies, account linking by email, token-selected database, permission decisions cached between requests, or database migrations at HTTP startup. Missing runtime prerequisites or missing tests must not become PASS.

### Task 1 — Reproducible PoC and issuer-store proof (in progress)

Files: `tests/Unit/SaaS.UnitTests/IdentityPoc`, `scripts/check-m2.ps1`, `scripts/m2-sql-fixture.ps1`, `.github/workflows/m2.yml`, this plan and `docs/verification/m2.md`.

The SQL tests deliberately reuse the existing test project and M1 SQL fixture instead of introducing another dependency graph. They are selected with `Category=SqlM2`; M0 excludes both `SqlM1` and `SqlM2`. Customer databases and runtime principals are real, not EF InMemory or mocked token managers.

Consumes: the M1 fixed platform connection, private Orchard shell factory, provisioned Alpha/Beta SQL databases, separate migrator/runtime credentials and the inherited baseline gates.
Produces: a source-bound PoC report, including failed and missing mandatory cases. No production registration is enabled by adding this fixture.

- [x] Read ADRs, pinned native Orchard authentication/storage implementation and M1 contracts.
- [x] Execute the first failing PKCE HTTP test and the native-store behavioral RED cases before implementing the bridge. This does not mean all M2 acceptance tests have been implemented.
- [x] Implement a test-only native-store bridge and real-SQL regression cases; see source-bound verification history for their actual results.
- [ ] Prove Authorization Code + PKCE, central Orchard Users login and SSO, two independent prefixed callbacks/cookies, strict redirect/logout allowlists and issuer+subject mapping.
- [ ] Prove explicit JWT format, signature/issuer/audience/scope/expiry validation and Alpha token rejection at Beta.
- [ ] Integrate Default-store validation into authenticated API and web requests, and prove committed revocation blocks the next request.
- [ ] Prove all authoritative-source failures close access, serialized strict refresh rotation/reuse/concurrency/lost response, with tenant-local family/session revocation scope.
- [ ] Complete all mandatory PoC cases and present evidence for owner review. Do not promote ADR status automatically.

#### Candidate store adapter contract

`DefaultIssuerStoreProbe.ValidateAsync(IssuerEntry entry, CancellationToken cancellationToken)` returns `Valid`, `Invalid` or `Unavailable`; caller cancellation propagates as cancellation. `IssuerEntry` contains the immutable native token identifier, authorization identifier and subject. These values must eventually come from an already cryptographically validated token, not directly from untrusted request fields.

The adapter always opens a fresh `Default` shell scope. It refuses an entity-cache-enabled OpenIddict configuration, then resolves native token and authorization managers in that issuer scope. It requires a valid, unexpired access-token entry, exact subject and authorization linkage, and a valid authorization with the same subject. Missing/expired/revoked/mismatched records are invalid. Scope, store or configuration failure is unavailable, not a cached allow or invalid-password response.

**`Valid` only means these store checks passed.** This adapter does not validate JWT signatures, issuer, audience, scopes, tenant binding, account/membership status or web sessions. It is not an authentication handler. The proposed HTTP mapping of unavailable to 503 has not been implemented here.

OpenIddict 7.5 persists canonical `TokenTypeIdentifiers` URNs. Do not compare the stored type with `TokenTypeHints.AccessToken`. The native binding regression test retains the actual resolved type expectation. Orchard's native token/authorization documents belong to the `OpenId` YesSql collection, so dependency-failure injection must target that collection rather than the unrelated default document table.

#### Persistence and privilege ownership

Orchard owns all native OpenID tables and migrations. The fixture creates isolated, randomly prefixed native tables only in disposable databases and through the private maintenance shell. Test setup grants DML only on those exact tables to each existing runtime principal. Runtime gets no DDL, schema-wide DML or `db_owner`. Writes use native managers and a committed YesSql session, not hand-written updates to Orchard documents. Customer OpenID stores exist only as negative controls in this fixture; they are not a selected production topology.

No production hosting, EF model, migration, snapshot, package version, lock file, M1 descriptor or readiness fingerprint is changed by this PoC.

### Task 2 — Identity data and dynamic authority (blocked by task 1)

Files: `src/Platform/SaaS.Identity`, `src/Platform/SaaS.Abstractions`, the single platform-owned EF migration stream in `src/Platform/SaaS.Tenancy`, matching tests and retained snapshot. One writer owns this model/migration stream.

Provide immutable GlobalUserId and issuer+subject mapping, unique memberships and monotonic account/tenant/membership generations; persistent session/family revocation and issuance-time subscription bounds. Final production signatures require the full PoC outcome, not just the store adapter above.

Tests: same-email/different-subject never links; membership remove/re-add; suspend/reopen, expiry/renewal, restart; account lock; errors in each authoritative source. Read committed state afresh at authorization. Proposed ADR limits remain proposals for owner review: access 5 minutes / 30-second tolerance; refresh idle 7 days / absolute 30 days; web idle 30 minutes / absolute 8 hours, always bounded by issuance-time ExpiresAt.

### Task 3 — Central login, tenant choice and protected probe (blocked by tasks 1–2)

Files: identity module/controllers/handlers and product-host composition; tenant selection UI and protected probe only, not M3 role management or Orders. Use real Authorization Code + PKCE; callbacks/logout and cookies remain independently prefixed and allowlisted. Tenant identity comes exclusively from the routed shell. POST mutations require antiforgery. Persist signing/encryption/Data Protection keys outside Git; no refresh token in browser storage or logs.

Tests: one account enters Alpha/Beta but not Gamma; two tabs preserve context; wrong issuer/audience/expiry/scope and swapped tenant token fail; tenant logout versus global logout; API 401/403/503 and non-looping UI dependency errors with correlation identifiers.

### Task 4 — Regression and handoff (gate implemented; M2 acceptance pending)

Run `pwsh -NoProfile -File scripts/check-m2.ps1`. It runs locked restore/Release build, all M0 behavioral/architecture/hygiene/lock checks, both retained EF snapshot checks, inherited SQL smoke, the complete M1 real-SQL/Orchard suite, and then M2. Zero discovery, a mandatory skip, a failure or a missing mandatory case cannot pass. Preserve exact source SHA, commands/exit codes, image identity, discovered/executed/passed/failed/skipped counts and the mandatory-case inventory.

M2 CI runs for the work branch, `main` and PRs into `main`. The temporary source-export/offline-tooling workflows were removed; the final diff retains only verification workflows. PR #4 must remain Draft until the complete identity acceptance evidence is available. Existing review records and ADR status are unchanged.

## Execution constraints

The initial local workspace lacked the pinned .NET/PowerShell tools and could not resolve GitHub. An attempted offline workspace subsequently hit container execution errors. All retained build and SQL evidence therefore comes from source-identified GitHub Actions runs, not a synthetic local commit. This constraint is not an M2 acceptance result and does not waive any test.
