# M2 completion design and execution ledger

Starting handoff: `d71bca7ee077003dd47a641f9a37369ed2fba1f5`, Draft PR #4. Base M1: `2e1dd5568c204e3422e8c9222771e7796cf0eb25`. Specification: roadmap revision 1.3 sections 3, 4.1–4.4 and M2. This supplements `m2-identity.md`; it does not weaken any mandatory-case gate. Current source-bound counts and final verification are in `../verification/m2.md`.

## Architecture and boundaries

Native Orchard Users/OpenID Server and OpenIddict/YesSql remain at Default. Customer shells use ASP.NET Core's OIDC handler with Authorization Code + PKCE and independent secure cookies; they do not enable Orchard Users or link by email. A deployment-owned `IOpenIdServerService` supplies trusted settings and persisted signing/encryption certificates to the native server. Protocol validation, code redemption, signing and token storage remain native. Application endpoints supply principals only after tenant membership and current-state validation.

`PlatformDbContext` remains the sole EF migration owner of platform identity rows. These use the `identity` schema so customer-registry writes need not be granted to runtime. Native Users/OpenID tables remain Orchard-owned. Identity references Tenancy/Abstractions, never business modules. The routed shell selects the customer connection; credentials cannot select a database. The native HTTP PoC was demonstrated before dependent production registration.

Lifetime candidates, pending owner security acceptance: access five minutes, cryptographic clock tolerance 30 seconds; refresh idle seven days / absolute 30 days; issuer and tenant web sessions idle 30 minutes / absolute eight hours. Tenant credentials additionally capture subscription expiry. Renewal cannot extend that captured boundary. Native store validation may reject an actually expired access entry even inside cryptographic clock tolerance. No dynamic Allow cache crosses request boundaries.

## Components and ownership

- `IdentityDeployment` validates canonical HTTPS origin, independent persisted RSA certificates, persistent Data Protection directory and client metadata. Client secrets are supplied by deployment references keyed by immutable tenant UUID, never argv or tracked JSON.
- `IdentityAuthority` reads fresh fixed-platform account, subject, membership, session and family state. It validates generations, current lifecycle, idle/absolute limits and issuance-time subscription bounds. `DefaultIssuerStore` opens a fresh Default scope for native account/token/authorization checks with caching disabled.
- `RefreshRotation` serializes each family with a SQL application lock, records a hash-only token consumption before native redemption, retains the lock through response persistence and invalidates the family on reuse. Failure or lost response cannot make an old token reusable.
- `IdentityEndpoints` provides central native login, tenant choice, authorized native code/token passthrough, tenant account UI and the bearer-only `api/v1/identity` probe. UI mutations require antiforgery. Account second-factor requirements fail closed rather than being bypassed.
- `IdentityRuntimeHost` is shared by Program and product-host acceptance tests. It composes registry-backed settings, exact HTTP/callback allowlists, readiness, HTTPS/host validation and sanitized errors. Missing initialization or corrupt history closes access; it never migrates or repairs state.
- `IdentityInstaller` and the pre-host maintenance CLI own private bootstrap, explicit client registration and native enrollment. `IdentityOperations` owns atomic membership/account/subscription generation changes and audit. Runtime SQL credentials cannot manage the authoritative registry/memberships/clients or schema.

## Execution ledger

| Stage | Executed change and observed evidence |
|---|---|
| Native store proof | Existing test-first store bridge retained; the initial nine-pass/one-404-failure handoff is archived, not relabeled as complete identity proof. |
| Native HTTP wiring | Added real Users/server/client paths and the full acceptance cases. Fixed missing native document management and entity ID generation; these were fixture/composition blockers, not behavioral passes. |
| Authority and refresh | Implemented platform-owned model/migration, exact mapping, immutable binding, persistent generations/sessions/families, strict rotation, failure handling and lifecycle tests. |
| Remaining native failures | Fixed Orchard's pre-increment failure count at lockout, web-first visibility of native access revocation and root TimeProvider replacement. Added regressions for independent web lifetime and restart. |
| PoC prerequisite | Branch `4c07a4e711c2460ded750938c47881a95405b9ae`, run `42289975570`: full inherited gate and 37/37 native-store/HTTP cases passed before production integration. |
| Product-host contracts | Added three tests first; run `42290933413` retained 37 passing cases and failed the three missing integration contracts. Then registry-backed production composition and completed-retry verification were implemented. |
| Private CLI contract | Run `42292635442` passed 40/41 cases, including all production-host contracts; the only failure was the deliberately not-yet-implemented private command dispatcher. CLI implementation followed. |
| Callback registry contract | Run `42293839126` passed 41/42, including the private CLI case. A drifted native callback was incorrectly redirected with 302; the new production guard compares the independent registry before native handlers. |
| Final gate and handoff | See `../verification/m2.md` and PR #4 for the exact submitted source/run/artifact identities. The mandatory inventory now includes production, CLI, native lockout and web-first revocation, not only the initial 18 PoC cases. |

The work stays on the existing feature branch; no `main` edits or merge were performed. The local execution backend was unavailable, so all retained build and SQL evidence comes from the pinned Actions runner. The generated-input write workflow is removed; retained CI is read-only and archives only JSON/TRX. HTML/cookie/token-bearing assertion diagnostics are avoided.

## Review focus and acceptance boundary

Review lock release on cancellation/failure; absence of two surviving refresh chains; exact client/tenant binding before consumption; logout scopes; non-resurrection after re-add, reopen, renewal and unlock; fresh native validation on web-first requests; immutable routing/database identity; native callback drift; and verify-only completed bootstrap behavior. Current checks must not be replaced with token expiry, cached Allow or public setup.

ADR 0001 remains Conditional for the project owner's architecture/security decision. The lifetime defaults, strict lost-response/re-login behavior and deployment/key-management constraints need explicit acceptance. Author self-review is not independent review. Real deployment TLS/proxy/logging/access controls and subsequent M3 authorization/lifecycle UI are not claimed as implemented by this milestone's test evidence.
