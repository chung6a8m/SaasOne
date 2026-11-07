# M2 identity operations

M2 adds a centrally authenticated account with tenant-specific grants and cookies. The issuer is the native Orchard Users/OpenIddict profile in the Default shell. Customer identity is selected from the immutable tenant registry, never from a request header, body, or a caller-selected connection string. This document describes the implementation; acceptance evidence and outstanding owner decisions are recorded in `docs/verification/m2.md` and ADR 0001.

## Prerequisites and deployment boundary

Complete the M1 bootstrap and tenant provisioning runbook first. Use the pinned SDK, runtime, SQL Server baseline and locked package graph. The Default foundation stays in `saas_platform` with prefix `oc`; the identity profile has its separate `ocid` native prefix in that database. The `identity` EF schema is owned by `PlatformDbContext`. Customer databases retain the M1 foundation and contain no central credentials or central OpenIddict store.

The HTTP process must receive only `SAAS_PLATFORM_RUNTIME_CONNECTION`, the selected `SAAS_TENANT_<UUID-N-UPPERCASE>_RUNTIME_CONNECTION` values, and the trusted identity configuration below. Do not inject provisioner, operator, or migrator connections into the HTTP process. Privileged credentials belong to a separate private maintenance process. Normal HTTP startup never migrates, seeds, re-grants SQL access, or repairs a missing native history record.

Identity is opt-in through `SAAS_IDENTITY_ENABLED=true`. When it is not enabled, the existing M1 closed surface remains in effect. When enabled, incomplete identity configuration prevents normal startup; incomplete/corrupt persistent state returns readiness 503. An anonymous liveness check at `/health/live` is not an authorization or readiness check.

Use HTTPS to the application itself. The canonical issuer must be an HTTPS origin ending in `/`, without a path prefix, query, fragment or user information. This version does not trust arbitrary forwarded host/protocol headers. A TLS-terminating proxy must preserve end-to-end HTTPS and the canonical host, or receive a separately reviewed trusted-proxy configuration. Do not disable certificate validation to make a deployment work. Configure the ASP.NET Core HTTPS listener and its transport certificate independently of the protocol signing/encryption keys.

## Trusted identity configuration

Supply these values from the deployment secret manager, not tracked JSON, command-line arguments, shell history, a recipe or an HTTP form:

| Environment variable | Meaning |
|---|---|
| `SAAS_IDENTITY_ISSUER` | Canonical HTTPS origin |
| `SAAS_IDENTITY_SIGNING_CERTIFICATE` | Absolute path to the persisted signing PFX |
| `SAAS_IDENTITY_SIGNING_PASSWORD` | Password protecting that PFX |
| `SAAS_IDENTITY_ENCRYPTION_CERTIFICATE` | Absolute path to a different persisted encryption PFX |
| `SAAS_IDENTITY_ENCRYPTION_PASSWORD` | Password protecting that PFX |
| `SAAS_IDENTITY_DATA_PROTECTION_DIRECTORY` | Existing absolute, persistent, access-controlled key-ring directory |
| `SAAS_IDENTITY_CLIENT_<UUID-N-UPPERCASE>` | Independent random client secret, at least 32 characters, for that immutable tenant UUID |

Use independent current RSA private keys of at least 2048 bits for signing and encryption. Protect the PFX files and key-ring directory with OS access controls; persist them across process replacement and include them in the protected backup plan. Data Protection keys are encrypted with the configured encryption certificate, and cookie/state purposes are separated by shell. Never use development or ephemeral protocol signing keys in deployment. The test suite creates disposable test certificates but reloads their persisted bytes for restart acceptance.

The service's framework HTTP, authentication, protocol and document-store log categories are suppressed to prevent credential-bearing diagnostics. The application returns sanitized error codes and a correlation ID. Configure the reverse proxy, APM and hosting platform not to record authorization/cookie headers, query strings, request/response bodies, passwords, authorization codes, refresh tokens or client secrets. Do not enable sensitive EF logging, IdentityModel PII diagnostics or protocol debug dumps.

## Private initialization and enrollment

In a private maintenance shell, inject the M1 provisioner, platform operator and platform migrator connections, the restricted platform runtime connection used for verification, and the identity configuration. Register secrets for all existing tenants before first initialization. The operator identity is taken from SQL `ORIGINAL_LOGIN()`, not a user-supplied actor string. The following PowerShell variables contain identifiers or account metadata, not passwords.

```powershell
# $installationId is the existing M1 installation UUID in D format.
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-init $installationId
if ($LASTEXITCODE -ne 0) { throw 'Identity initialization failed.' }

dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-verify
if ($LASTEXITCODE -ne 0) { throw 'Identity verification failed.' }

# Inject SAAS_IDENTITY_ENROLLMENT_PASSWORD from the secret manager for this command only.
# $tenantIds is a comma-separated list of immutable UUIDs in D format.
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-enroll $installationId $username $email $tenantIds
if ($LASTEXITCODE -ne 0) { throw 'Identity enrollment failed.' }
Remove-Item Env:SAAS_IDENTITY_ENROLLMENT_PASSWORD -ErrorAction SilentlyContinue
```

Initialization uses a private SQL operation lease. It creates the native identity profile, native confidential clients, exact callback bindings, authoritative metadata, schema and the completion marker. Retrying a completed initialization is verification-only: it does not run migrations, change native clients, resolve their secrets or repeat privilege grants. Corrupt/missing history requires investigation, not deletion of the completion marker.

Enrollment uses native `UserManager` password hashing and validation, then explicitly binds `(issuer, subject)` to a `GlobalUserId` and the requested memberships. It never finds or joins a global account by email. The command outputs `global_user_id:<UUID>` for subsequent administration. Retrying an existing username requires its valid native password; it is not a password-reset or email-linking mechanism. Only native accounts without a configured second factor can use this minimal login form; an account with a second factor fails closed rather than bypassing it.

After provisioning another tenant with M1, inject its independent identity client secret and run the explicit private client registration command:

```powershell
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-client $installationId $tenantId
if ($LASTEXITCODE -ne 0) { throw 'Identity client registration failed.' }
```

Client registration does not silently rotate secrets or change an existing callback. Configuration drift is rejected for explicit reconciliation. In particular, repeating `identity-init` is not the client-registration mechanism for a newly provisioned tenant.

## Membership, account and subscription changes

These commands need the private platform operator connection and do not load protocol private keys. UUID inputs must be nonempty D-format values. State words are exact and case-sensitive. Every successful state change and its invalidation generation are committed with the operational audit; a reactivation never restores an old credential.

```powershell
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-membership $globalUserId $tenantId inactive
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-membership $globalUserId $tenantId active
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-account $globalUserId disabled
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-account $globalUserId enabled

# Expiry is a DateTimeOffset round-trip value, including offset and seven fractional digits.
$expiry = ([DateTimeOffset]::UtcNow.AddDays(30)).ToString('O')
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-tenant $tenantId suspended $expiry
dotnet run --project src/SaaS.Web -c Release --no-build -- --maintenance identity-tenant $tenantId active $expiry
```

Check `$LASTEXITCODE` after each command; a nonzero value is not success. Do not bypass these commands with direct state updates: membership/account/tenant generations and credential revocations must change atomically. Pending-deletion lifecycle fences are not reversible through the subscription command. Product-specific roles, per-user grants/denies and tenant administration UI belong to later milestones, not this M2 surface.

## Runtime checks and expected behavior

Start a new HTTP process with identity enabled and only restricted runtime connections. Check `/health/ready` and `/<slug>/health/ready`. Each readiness request reads current schema/baseline/native history; no cached readiness result can repair or bypass a broken deployment. `/account/login` authenticates centrally; `/account/tenants` shows allowed active tenants; `/<slug>/account/login` starts Authorization Code + S256 PKCE; `/<slug>/account` is the tenant session view. The protected probe is `GET /<slug>/api/v1/identity` with a bearer access token for that same immutable tenant UUID.

Native issuer discovery, authorization, token and end-session endpoints are allowlisted individually. Admin, setup, recipes, template editing, arbitrary native controllers and membership/role mutations are not public. Host/readiness/database faults fail with 503; invalid credentials or cross-tenant tokens fail with 401; an authenticated but disallowed operation uses 403 or the corresponding OAuth error. Redirect/return/callback validation is exact, not a wildcard or suffix test.

A tenant logout is a CSRF-protected POST and affects that user's sessions/grants for the selected tenant only. Central logout is a CSRF-protected POST and invalidates the global account's sessions/grants across tenants. Native lockout changes the security stamp so unlocking cannot revive prior grants. Revoking the native authorization or an access-token entry invalidates its related family and web session at their next validation; independent families survive.

Access tokens last at most five minutes. Web sessions have a 30-minute idle limit and eight-hour absolute limit. Refresh grants have seven-day idle and 30-day absolute limits. Tenant credentials are additionally bounded by the captured subscription expiry. These defaults remain subject to owner acceptance in ADR 0001; increasing a subscription expiry never extends an already-issued family's captured boundary.

Refresh is strictly single-use and bound to client, tenant and family. Concurrent refresh is serialized with a SQL application lock. Reuse invalidates only that family and the related tenant session. A lost refresh response followed by replay requires a fresh authorization flow; there is no retry grace period that creates two valid chains. Clients must serialize refresh and not retry an old token after an uncertain result. Only hashes, not plaintext refresh values, are persisted in the application ledger.

## Incident/restart procedure

Remove traffic when readiness fails. Keep the same persisted certificates, key-ring directory, issuer, canonical registry and database state across a normal restart. Do not create replacement keys automatically. Revocations are durable; restarting or reactivating a tenant cannot clear them. Investigate a sanitized correlation ID without enabling sensitive payload logging.

A completed marker with damaged native history is a stop condition. Restore a consistent backup or use a separately reviewed migration/reconciliation procedure. Never erase the marker, widen SQL roles, enable public setup, or make the HTTP host a migrator to obtain a green health response. Existing-client secret rotation and overlapping key rollover are not automated by this runbook; plan them as explicit maintenance with credential invalidation and owner approval, not an undocumented startup side effect.

## Verification

```powershell
./scripts/check-m2.ps1
if ($LASTEXITCODE -ne 0) { throw 'M2 gate failed.' }
```

The gate retains JSON and TRX evidence under `artifacts/verification` and `artifacts/test-results`. It includes locked restore, warning-free Release build, M0 tests/architecture/hygiene/package inventory, both EF snapshots, restricted SQL smoke, all M1 SQL tests and all discovered M2 cases. Missing mandatory cases, skipped cases, zero execution or cleanup failure are failures. TestServer replaces HTTP transport only; real SQL, native Users/OpenIddict, cryptography, cookies, PKCE and persisted grant state are exercised. This does not claim that a real deployment's DNS, TLS listener, proxy logging or backup permissions have already been inspected.
