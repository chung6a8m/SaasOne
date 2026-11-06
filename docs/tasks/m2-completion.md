# M2 completion implementation plan

Baseline: `d71bca7ee077003dd47a641f9a37369ed2fba1f5`, PR #4. Specification: roadmap revision 1.3 sections 3, 4.1–4.4 and M2; ADR 0001 remains Conditional until owner acceptance. This plan supplements `m2-identity.md`; it does not weaken the complete mandatory-case gate.

## Architecture and boundaries

The native Orchard Users/OpenID Server and native OpenIddict/YesSql stores remain at Default. Customer shells use ASP.NET Core's OpenIdConnect handler with PKCE and independent secure cookies; they do not enable Orchard Users or link by email. A deployment-owned `IOpenIdServerService` supplies immutable settings and persisted signing/encryption certificates to the native server; no hand-written JWT/password grant. Native protocol validation, code redemption, signing and token storage are retained. Explicit application endpoints supply tenant-checked principals.

PlatformDbContext remains the only EF migration owner of platform identity data. Identity entities use the `identity` schema to avoid granting writes to the read-only tenant registry. Native Users/OpenID tables remain Orchard-owned. Identity code references Tenancy and Abstractions, never business modules. No customer database is selected from credentials. Production registration is added only after the isolated native HTTP PoC passes.

Lifetimes selected for this implementation, pending owner security review: access 5 minutes, clock tolerance 30 seconds; refresh idle 7 days / absolute 30 days; tenant and issuer web sessions idle 30 minutes / absolute 8 hours. Every tenant grant/session is bounded by subscription expiry captured at issuance. No dynamic Allow cache across requests.

## Locked interfaces and ownership

- `IdentityDeployment`: canonical HTTPS issuer, persisted signing/encryption certificates and Data Protection directory, explicit client registrations keyed by routed shell. Secrets are environment/file references only. A host with missing/invalid identity configuration stays closed.
- `IdentityAuthority`: fresh fixed-platform EF contexts for exact issuer+subject mapping, memberships, accounts, sessions, grant families and hashed refresh consumption. `ValidateAsync(Guid familyId, Guid routedTenantId, bool web, CancellationToken)` returns an explicit decision. `ConsumeRefreshAsync(string token, string clientId, string? requestedTenant, CancellationToken)` serializes by durable family lock, consumes once and revokes only that family/session on reuse. Raw refresh tokens are never stored.
- `DefaultIssuerStore`: native token/authorization checks in fresh Default shell scope with entity caching disabled. This follows cryptographic authentication and is never itself an authenticator.
- `IdentityEndpoints`: central native Users login, tenant selection, authorization-code/token passthrough, tenant account UI and bearer-only `api/v1/identity` probe. UI mutations use antiforgery; OAuth errors follow protocol semantics. Credential failure=401, membership/tenant policy=403, mandatory dependency failure=503 with correlation id.
- `IdentityHttpFixture`: isolated native SQL/Orchard HTTP host; no fake auth handler or seeded bearer principal. Browser helper follows real cookies, redirects, PKCE and code redemption. Candidate wiring is test-only until the full PoC is demonstrated.
- Private maintenance commands own membership/lifecycle changes and identity bootstrap; audit is committed with authoritative EF changes. No public setup/admin/membership management shortcut. M3 owns full management UI and dynamic business roles.

## Tasks

1. **Native HTTP PoC.** Add failing HTTP behavior tests, then native Users/server/client wiring and persisted key configuration. Files: `tests/Unit/SaaS.UnitTests/IdentityPoc/*`, candidate `src/Platform/SaaS.Identity/*`. Prove central login, two-shell SSO, cookies, callback allowlists and code/PKCE. Keep native-store regression cases.
2. **Durable authority and protocol binding.** Add tests before implementations for mapping, membership and generation lifecycle, JWT checks, tenant binding, session expiry, issuer-store and platform failures. Files: `src/Platform/SaaS.Tenancy/Data/Identity*`, partial PlatformDbContext model, owned migration/snapshot; identity services. Fresh reads only; SQL Server required.
3. **Strict refresh and revocation.** Test concurrent refresh, lost response/reuse, independent families, logout scopes, account/tenant disable and restart. Serialize before native redemption using a SHA-256 credential lookup in the fixed platform store; commit consumption before issuance and retain lock through native response persistence. Native protocol validation is not bypassed. Errors never revive consumed credentials.
4. **Production integration after PoC.** Add private initialization/upgrade command, explicit native feature profile and DML grants, readiness validation and host registration. Preserve M1 baseline mode; identity is opt-in only after private schema/profile initialization, never a runtime auto-migrate. Exercise the same components from the production host.
5. **Verification and review.** Run full `scripts/check-m2.ps1`, complete M0/M1 SQL regressions, exact migration snapshot checks, package/feature graph and secret hygiene. Every required case must execute; missing/skip/zero is failure. Record tested SHA and counts, update runbook/ADR evidence and PR. Do not merge or self-promote ADR to Accepted.

## Review focus

Refresh lock release on cancellation/failure; no two surviving chains; issuer cookie cannot resurrect global logout; no old-generation revival after re-add/renew/unlock; wrong prefix/body/token never chooses a connection; default native controllers remain inaccessible outside the explicit allowlist; issuance/configuration failures do not return a credential or cached Allow; startup never performs DDL.

## Execution ledger

- Resuming the existing feature branch and draft PR; no main edits. Local container/Python tool backend returned ClientError, so verification uses the repository's pinned GitHub Actions runner. No offline SDK export is needed.
- All completion tasks remain pending until evidence is recorded. Author self-review is not independent owner/security approval.
