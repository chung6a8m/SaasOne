# M2 — Central identity implementation plan

Baseline: `2e1dd5568c204e3422e8c9222771e7796cf0eb25` (merged M1 PR #3).
Specification: `docs/roadmap/saas-orchard-roadmap-v1.md` revision 1.3, sections 3, 4.1–4.4 and M2. ADR 0001 stays **Conditional — pending M2 PoC**. The project owner retains architecture/security acceptance and merge authority.

## Boundaries and execution order

Implement the cross-shell PoC first, on the existing pinned Orchard/YesSql/OpenIddict/.NET graph. No dependent production identity rollout before this gate passes. Do not replace Orchard Users/OpenID Server with a hand-written password/JWT endpoint. No shared tenant cookies, account linking by email, token-selected database, permissions cached between requests, or database migrations at HTTP startup. Missing runtime prerequisites are BLOCKED, not PASS.

### Task 1 — Reproducible PoC and issuer-store proof

Files: test-only identity PoC fixture under `tests/Integration`, `scripts/m2-*`, `.github/workflows/m2.yml`, this plan and `docs/verification/m2.md`. Existing hosting/tenancy contracts are read first; exact adapter signatures are recorded before their consumers are implemented.

Consumes: the M1 fixed platform connection, Orchard-routed customer shell and immutable TenantId, provisioned SQL fixtures, restricted runtime identities and readiness gates.
Produces: a source-bound PoC result with exact package versions, commands, test counts and evidence for Default + Alpha/Beta. No production registration is enabled by merely adding the fixture.

- [ ] Read ADRs, native pinned Orchard authentication implementation and M1 adapters.
- [ ] Write and execute failing behavioral tests before the adapter implementation.
- [ ] Prove Authorization Code + PKCE, central Users login and SSO, two independent prefixed callbacks/cookies, strict redirect/logout allowlists and issuer+subject mapping.
- [ ] Prove explicit JWT format, signature/issuer/audience/scope/expiry validation and Alpha token rejection at Beta.
- [ ] Prove validation uses Default's actual token/authorization store and that committed revocation blocks both API and tenant web session.
- [ ] Prove source failures close access, serialized strict refresh rotation/reuse/concurrency/lost response, with tenant-local revocation scope.
- [ ] Record PASS or BLOCKED/FAIL without promoting ADR status. A partial fixture/unit test is not this gate.

Migration owner: Orchard owns Users/OpenID/YesSql tables. PoC application persistence must have an explicitly separate owner; it must not modify Orchard-owned tables directly.

### Task 2 — Identity data and dynamic authority (blocked by task 1)

Files: `src/Platform/SaaS.Identity`, `src/Platform/SaaS.Abstractions`, the single platform-owned EF migration stream in `src/Platform/SaaS.Tenancy`, matching tests and retained snapshot. One writer owns this model/migration stream.

Provide immutable GlobalUserId and issuer+subject mapping, unique platform memberships and monotonic account/tenant/membership generations; persistent session/family revocation and issuance-time subscription bounds. Final signatures must be added here after the PoC selects the adapter. No detached implementation may bypass the gate.

Tests: same-email/different-subject never links; membership remove/re-add; suspend/reopen, expiry/renewal, restart; account lock; errors in each authoritative source. Read committed state afresh at authorization, not an earlier cross-request snapshot. Limits proposed in ADR 0001 remain proposals for owner review: access 5 minutes / 30-second tolerance; refresh idle 7 days / absolute 30 days; web idle 30 minutes / absolute 8 hours, always bounded by issuance-time ExpiresAt.

### Task 3 — Central login, tenant choice and protected probe (blocked by tasks 1–2)

Files: identity module/controllers/handlers and product-host composition; tenant selection UI and protected probe only, not M3 role management or Orders. Use real Authorization Code + PKCE; callbacks/logout and cookies remain independently prefixed and allowlisted. Tenant identity comes exclusively from the routed shell. POST mutations require antiforgery. Persist signing/encryption/Data Protection keys outside Git; no refresh token in browser storage or logs.

Tests: one account enters Alpha/Beta but not Gamma; two tabs preserve tenant context; wrong issuer/audience/expiry/scope and swapped tenant token fail; tenant logout versus global logout; API 401/403/503 and non-looping UI dependency errors with correlation identifiers.

### Task 4 — Regression and handoff

Run locked restore/Release build, `scripts/check.ps1 -Scope M0`, inherited SQL smoke and complete M1 real-SQL/Orchard suite, then the complete M2 gate. Zero discovery, missing mandatory tests or any mandatory skip fail. Retain exact tested SHA, commands/exit codes and discovered/executed/passed/failed/skipped counts. Review security boundaries, migration/snapshot consistency, dependency graph, secret hygiene and unchanged historical review records. Open a reviewable PR; do not merge or mark ADR Accepted automatically.

## Execution ledger

- Baseline read from GitHub; local editing container initially has no .NET/PowerShell and cannot resolve GitHub. Use GitHub Actions for pinned runtime evidence and a tracked-source-only artifact for the isolated editing workspace. This is an execution constraint, not evidence of M2 acceptance.
- All task checkboxes above are pending until backed by retained results.
