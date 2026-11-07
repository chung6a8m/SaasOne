# SaasOne

Reusable Orchard Core multi-tenant SaaS foundation. The reviewed [roadmap](docs/roadmap/saas-orchard-roadmap-v1.md), revision 1.3, is the specification. This branch implements M0 foundation, M1 tenancy/provisioning, M2 central identity and **M3 authorization, lifecycle and audit**.

Default uses a fixed platform database and native Orchard Users/OpenIddict. Customers have isolated databases, independent cookies, tenant-bound API credentials and current platform membership/session checks. M3 adds tenant roles, Allow/Deny/Unspecified, user overrides, resource policy, guarded operations and atomic customer audit. The protected workspace is a policy probe, not Orders/OrderApprovals. There are no public provisioning/operations endpoints or customer CMS administration.

Implementation verification, owner architecture/security acceptance and merge are separate. Read [M3 verification](docs/verification/m3.md) and PR #5; ADR 0001–0006 are not automatically promoted to Accepted. No production deployment is implied. Orders/Approvals, SQL restore/release rehearsal and load/reuse acceptance remain M4–M7 work.

## Fresh clone without Visual Studio

The evidence-backed environment is Ubuntu 22.04 x64, Git, .NET SDK **10.0.403**, PowerShell **7.6.9**, and Docker. VS Code is optional. SDK selection is exact (`rollForward: disable`); other operating systems and architectures have not been certified by this evidence.

Clone using your normal GitHub authentication, enter the repository root, then run:

```powershell
dotnet --info
pwsh --version
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
dotnet test SaasOne.slnx --configuration Release --no-build --filter 'Category!=SqlM1&Category!=SqlM2&Category!=SqlM3' --logger trx --results-directory artifacts/test-results/m0
pwsh -NoProfile -File scripts/check-m3.ps1
if ($LASTEXITCODE -ne 0) { throw 'M3 verification failed.' }
```

The complete gate runs inherited M0/M1/M2 checks, all four owned EF snapshot checks, restricted SQL smoke and the mandatory M3 SQL/HTTP/private-operations suite. It includes locked build, project/package/native-feature architecture checks and tracked-configuration secret scanning. Nonzero native exit codes fail the gate.

SQL fixtures create disposable, loopback-bound containers using the pinned SQL Server 2022 Developer image/build **16.0.4305.5**, generate disposable credentials, and require successful cleanup. They never target an existing deployment. Developer edition is for development/testing only. SQL tests are excluded only from the non-SQL command above; all three categories are mandatory in the full gate and never silently skipped.

| Gate | Additional evidence beyond its prerequisites |
|---|---|
| M0 | Locked dependencies, non-SQL behavior, architecture, secrets and evidence-validator checks |
| M1 | Four customer databases, real Orchard routing, immutable DB identity, bootstrap/provision retry, ownership, readiness and restricted runtime privileges |
| M2 | Native login, discovery, PKCE/code redemption, SSO, signed JWT binding, current authority, refresh concurrency/reuse/lost response and production host composition |
| M3 | Current ACL/override/resource policy on UI and API; administration/CSRF; audit rollback; write-gate drain/fences/crash/retry; lifecycle races; external ACL revalidation and child upgrade; private commands and target-only purge |

TestServer replaces HTTP transport, not authentication or SQL stores. These tests do not certify deployment DNS, TLS listeners, proxy logging or secret-manager controls. M3 recovery fixtures do not claim an M6 SQL restore rehearsal.

Missing prerequisites/cases, duplicate or zero execution, skipped/failing mandatory tests, stale source evidence and cleanup errors cannot produce PASS. JSON/TRX evidence is under `artifacts/verification` and `artifacts/test-results`. The historical `m2-poc` and `m3-poc` paths contain complete mandatory suites. `m3-acceptance.json` verifies that inherited reports share the same tested SHA; the M3 evidence validator tests incomplete, duplicate, skipped, failed and substituted results.

For focused regression after a foundation build, use `scripts/sql-fixture.ps1`, `scripts/m1-sql-fixture.ps1`, `scripts/m2-sql-fixture.ps1` or `scripts/m3-sql-fixture.ps1`. They do not replace the complete milestone gate. `scripts/check.ps1 -Scope M0` retains the foundation-only gate. A separately prepared matching SQL smoke instance uses a secret-managed `SAAS_SQL_SMOKE_CONNECTION` and `-Scope SqlSmoke`; never paste credentials into commands or Git.

## Provisioning and runtime modes

Read the [M1 runbook](docs/runbooks/m1-tenancy-provisioning.md) before injecting secrets. Bootstrap/provisioning run privately, never through HTTP. Runtime, operator, per-database migrator and server provisioner identities have different SQL privileges.

```powershell
# IDs, operation IDs and expiry come from a persisted deployment inventory.
./scripts/tenant-provision.ps1 -Bootstrap -InstallationId $installationId
./scripts/tenant-provision.ps1 -TenantId $tenantId -OperationId $operationId -Slug alpha -Plan Standard -ExpiresAt $expiresAt
```

Retry uses the same complete request tuple. Resource coordination uses one trusted operations machine, a durable local process lock, a non-expiring SQL session lock and prior-worker SQL-session reconciliation. Cross-host takeover and unidentified existing targets are blocked. These operational constraints require owner review before deployment.

M1 baseline mode remains available without identity. `/health/live` proves liveness only; readiness checks the platform/customer baseline. Setup, admin, template, recipe, feature and upload surfaces stay closed. An unknown/unready customer never falls through to Default customer business data.

For M2, follow the [identity runbook](docs/runbooks/m2-identity.md): provision, privately run `identity-init`, explicitly enroll accounts/memberships, and enable `SAAS_IDENTITY_ENABLED=true`. Supply persisted signing/encryption certificates, a protected persistent Data Protection key ring, canonical HTTPS issuer and independent tenant client secrets. The HTTP-only port-5080 development shortcut is not an identity deployment. Missing state is a stop condition, never permission for runtime migration or seeding.

The HTTP process receives only restricted runtime connections and identity configuration, not provisioner/operator/migrator credentials. Central `/account/login` and `/account/tenants` provide login/tenant choice. `/{slug}/account/login` performs native OIDC; `/{slug}/account` shows the tenant session; `/api/v1/identity` under the tenant prefix requires bearer authentication. Callback/logout destinations must match the independent authoritative registry before native redirects.

Private identity commands include `identity-init`, `identity-verify`, `identity-client`, `identity-enroll`, `identity-membership`, `identity-account` and `identity-tenant`. Enrollment passwords use a one-command environment variable, never argv. Completed initialization retry verifies read-only; new client registration is a separate command, not a startup side effect. Deleted/retired tenants cannot regain memberships or enabled clients through registration/retry.

Secret delivery is environment-only. New tenant connections/client secrets must already be available to the running process; changes made by a parent or maintenance process do not update an existing process environment. Change deployment configuration and perform a controlled restart/recreation. There is no claim of dynamic secret delivery or zero-downtime provisioning.

## M3 access control and operations

Follow the [M3 runbook](docs/runbooks/m3-operations.md). Privately run `access-install` per tenant, review initial application administrators through external recovery authority, then enable `SAAS_ACCESS_CONTROL_ENABLED=true` alongside identity. Installation starts with an empty ACL, never an implicit administrator. Runtime validates schema/profile and current rights; it does not migrate or repair them.

`/{slug}/access` is the minimal server-rendered role editor; `/{slug}/api/v1/access` uses the same service with bearer authentication. Web mutations require antiforgery. Changes carry an operation UUID, ACL revision and write-gate version; they cannot elevate the actor or delegate `Access.Manage`. Roles, history and audit commit on one customer connection/transaction. Audit failure rolls back the complete mutation.

`workspace`, `workspace/approval` and `profile/{userId}` have matching web/API probes for permission, entitlement and owner policy. The actual routed shell selects the customer connection; body/header/token hints never choose a database or resource owner. Deny dominates every Allow; required-source failure never reuses a prior allow. Native CMS administration remains closed.

The minimal operator UI is a separate private terminal, not a privileged HTTP dashboard:

```powershell
pwsh -NoProfile -File scripts/operations-console.ps1 -AssemblyPath <published/SaaS.Web.dll>
```

It lists current registry versions and invokes authenticated lifecycle, plan, recovery and purge commands. New suspension/deletion/expiry decisions are not overwritten by stale operation completion. Purge requires PendingDeletion, repeated target confirmation, prior-worker quiescence and immutable identity verification; global accounts and unrelated tenant resources survive. The registry tombstone is permanent.

Recovery approval comes from current platform authority registered before the operation, not tenant roles or restored audit. It applies a reviewed complete ACL. M3's foundation-only obligation reader rejects unfamiliar business tables; M4/M5 must provide explicit persisted business readers. No direct feature toggle bypasses the protocol. This console does not perform SQL backup/restore.

## Repository and maintenance

| Path | Responsibility |
|---|---|
| `src/SaaS.Web` | Product composition, private CLI, Orchard routing, identity and application boundaries |
| `src/Platform/SaaS.Tenancy` | Registry, shared resource coordination, immutable identity, write gate and transaction coordinator |
| `src/Platform/SaaS.Identity` | Native integration, current authority, sessions, JWT binding and refresh rotation |
| `src/Platform/SaaS.AccessControl` | Permission catalog, tenant ACL, safe administration and recovery apply |
| `src/Platform/SaaS.Auditing` | Append-only customer audit |
| `src/Platform/SaaS.Abstractions`, `src/Themes/SaaS.Theme.App` | Shared contracts and platform-controlled theme |
| `tools`, `tests`, `docs/verification`, `docs/runbooks` | Verification tooling, behavioral/SQL/architecture tests, source-bound results and operations |

Tenancy owns `platform`, `identity` and `tenancy`; AccessControl owns `access`; Auditing owns `audit`; Orchard owns its native tables/history. One migration owner per table/history. Platform/shared theme must not reference business modules directly or transitively. Read [AGENTS.md](AGENTS.md), [conventions](docs/conventions.md), the [compatibility matrix](docs/compatibility/m0-matrix.md), [M0 decisions](docs/decisions/m0-register.md) and ADRs before changing boundaries.

`dev.ps1 -Action Setup` restores the baseline; `-Action Run` serves baseline mode on loopback; `-Action Check` runs M0. Identity/M3 need separately configured HTTPS and trusted deployment state.

The inherited workflows remain. `.github/workflows/m3.yml` runs full acceptance on PRs into `main`, pushes to `main` and explicit dispatch. `m3-components.yml` is additional focused evidence, not full acceptance. Actions are pinned and repository permissions read-only; artifacts contain JSON/TRX, not raw credential-bearing stdout. No deployment secrets are needed.

Normal builds never rewrite locks. After a reviewed dependency change, use `scripts/update-locks.ps1`, inspect resolved graph/lock changes, update compatibility rationale and rerun every gate. Do not bypass NuGet audit. Rich text, executable uploads and arbitrary customer scripts remain disabled.
