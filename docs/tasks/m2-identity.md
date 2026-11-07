# M2 — Central identity implementation ledger

Baseline: `2e1dd5568c204e3422e8c9222771e7796cf0eb25` (merged M1 PR #3).
Specification: `docs/roadmap/saas-orchard-roadmap-v1.md` revision 1.3, sections 3, 4.1–4.4 and M2.
Branch: `feat/m2-identity-poc`; PR #4.

The original nine-store-case-only handoff is historical, not the current implementation. Its exact source-bound limitations and log-hygiene history are retained in `docs/verification/m2-initial-store-poc.md`. Current gate results are in `docs/verification/m2.md`; the detailed completion design is `m2-completion.md`, and operator commands are in `../runbooks/m2-identity.md`. A list of implemented components is not a substitute for a passing gate on the submitted SHA.

## Delivered implementation

| Workstream | Components and verification boundary |
|---|---|
| Initial cross-shell proof | Real SQL/Orchard/YesSql store bridge and native HTTP fixture. The complete 37-case native HTTP PoC passed in run `42289975570` before production composition was added. |
| Native central identity | Orchard Users/OpenIddict at Default; deployment-owned server settings and persisted certificates; real code/PKCE redemption; independent customer OIDC cookies; central login and tenant choice. |
| Durable authority | Exact issuer+subject to immutable GlobalUserId; unique memberships; account/tenant/membership generations; persistent sessions/families and captured subscription bounds in the platform-owned `identity` schema. |
| API and web validation | Cryptographic JWT validation followed by fresh Default native token/authorization and platform authority reads. Routed tenant binding, no credential-selected connection, no cross-request Allow cache. |
| Refresh/revocation | Hashed single-use consumption ledger, family-scoped SQL serialization, strict reuse handling, independent-family survival, tenant/global logout, lockout and non-resurrection after reactivation or restart. |
| Production composition | Shared `IdentityRuntimeHost` used by Program and acceptance tests; registry-backed shell settings, HTTPS/host checks, readiness, exact callback boundary and explicit endpoint allowlist. |
| Private operations | Initialization, verify-only completed retry, explicit client registration, native enrollment, membership/account/subscription commands, operational audit and restricted runtime SQL credentials. |
| Acceptance and hygiene | Complete inherited M0/M1 gates, retained platform migration/snapshot, 30 named mandatory M2 contracts plus every discovered case, JSON/TRX artifacts, read-only workflow and no generated-input write workflow. |

## Authority and ownership rules

Native Users/OpenID documents remain Orchard-owned. `PlatformDbContext` is the sole migration owner of platform identity rows; there is no competing identity EF context. Customer databases retain their M1 foundation and do not become central credential stores. Customer native OpenID stores in the original fixture are negative controls only.

The retained test-only `DefaultIssuerStoreProbe` proves exact native-store linkage but is not an authenticator. Production `DefaultIssuerStore` runs only after cryptographic authentication or protected-cookie validation, in a fresh Default shell scope with native entity caching disabled. Missing/expired/revoked/mismatched entries deny access; required-source failures return unavailable, not cached Allow. The native canonical token-type URN is checked, not the OAuth token-type hint.

No shared tenant cookies, account linking by email, global mutable CurrentTenant, public provisioning/admin shortcut, runtime migration, arbitrary native-controller exposure or plaintext refresh persistence is permitted. Callback destinations must agree with the authoritative platform registry before native protocol handlers may redirect. A completed private initialization is a read-only verification boundary, not permission to reset clients or repair native history.

## Verification and review

Run `pwsh -NoProfile -File scripts/check-m2.ps1`. It executes locked restore/Release build, all M0 behavioral/architecture/hygiene/lock checks, both EF snapshot checks, inherited SQL smoke, complete M1 SQL tests and all `Category=SqlM2` tests. Missing mandatory cases, duplicate/zero execution, failures, skips or cleanup failure cannot pass. The `m2-poc.json` path is retained for evidence compatibility and now includes native HTTP, production-composition and private-command contracts.

All recorded build/SQL evidence comes from actual source-identified GitHub Actions runs; no local execution is claimed. TestServer substitutes transport only. Real deployment DNS/TLS/proxy/secret permissions, independent review, owner acceptance of ADR 0001 and merge remain separate activities. Roles, per-user grants/denies, full lifecycle/operations UI and Orders belong to subsequent milestones.
