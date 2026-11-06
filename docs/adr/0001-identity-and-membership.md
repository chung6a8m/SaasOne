# ADR 0001 — Central identity and tenant membership

Status: **Conditional — pending M2 PoC**. Owner: project owner / identity implementer. Scope: R02, R03; roadmap §3, §4.1–4.4. Added TTL values below are implementation proposals for owner review before M2.

## Decision and alternatives
Use Orchard Users and OpenID Server at the Default shell, with a fixed platform connection. Customer web shells are OIDC clients; API validation must reach the issuer's actual token/authorization store or use proven introspection. Same process does not mean same store. Shared authentication cookies across shells and per-tenant passwords are rejected. A separate identity service is deferred unless the PoC requires revisiting this ADR.

GlobalUserId is an immutable platform UUID. External identities map by exact issuer + subject, never by email. TenantId is an immutable platform UUID; ShellName and UrlPrefix are routing attributes, not identity. Membership is platform-owned and unique by GlobalUserId + TenantId. Tenant local profiles are optional subject links, never a second password source. ACL and user-role assignments belong to the customer DB.

AccountSecurityGeneration, TenantSecurityGeneration and MembershipGeneration are monotonic platform counters. Sessions and grant families have independent random IDs, their own revocation state, and capture all three generations at issuance. Remove/re-add creates a new membership generation. Unlock/reopen/renew cannot revive a revoked session or an expired issuance-time validity bound. Natural subscription expiry bounds credential/session validity at issuance; later extension does not extend existing credentials. Exact persistence/API signatures belong to M2's plan, not this skeleton.

Use Authorization Code + PKCE, explicit JWT configuration, validated issuer/signature/audience/expiry/scopes and a server-validated tenant claim. A grant family is bound to one TenantId. The routed shell's TenantId must match; the token never selects a database. Web cookies are Secure/HttpOnly, independently named/scoped per shell, with anti-CSRF for mutations. Callback/logout destinations use a strict prefixed allowlist. No global mutable CurrentTenant or refresh token in localStorage.

Proposed explicit lifetimes: access token 5 minutes, clock tolerance 30 seconds; refresh idle 7 days and absolute 30 days; tenant web session/cookie idle 30 minutes and absolute 8 hours. Every limit is additionally bounded by the subscription expiry captured at issuance. Sliding renewal cannot exceed the absolute bound. Server-side session validation is required on each authorization; a cookie expiration is not the revocation mechanism. Signing/encryption/Data Protection keys are persisted outside Git with rotation and access control.

Refresh rotation is serialized by grant family, consumes a token once, and has no reuse grace window. Reuse revokes that family and its related tenant session, not independent families or other tenants. Concurrent refresh or a lost response can require re-login; clients single-flight refresh. Logout-tenant and logout-global have distinct server-side scopes. OAuth errors follow OAuth (`invalid_grant`), not business API 403 semantics.

## Gate, consequences and evidence
M2 begins with Default + Alpha/Beta on the pinned package graph: two-tenant SSO, prefixed callbacks/logout, independent cookies, issuer+subject mapping, JWT format/encryption/audience/scope, Alpha token rejected by Beta, issuer-store validation, revocation affecting API and web session, rotation/reuse/concurrency/lost response and source failure. Record commands/configuration/versions/results. Failure keeps this ADR Conditional and dependent identity work blocked. M0 restore/build is not this proof.
