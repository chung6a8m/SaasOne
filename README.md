# SaasOne

Reusable Orchard Core multi-tenant SaaS foundation. The reviewed [roadmap](docs/roadmap/saas-orchard-roadmap-v1.md), revision 1.3, is normative. This branch implements M0 foundation, M1 tenancy/provisioning, M2 central identity, M3 authorization/lifecycle/audit and **M4 end-to-end Orders**.

Default uses a fixed platform database and native Orchard Users/OpenIddict. Customers have isolated databases, independent cookies, tenant-bound API credentials and current membership/session checks. M3 adds roles, Allow/Deny/Unspecified, user overrides, safe administration and guarded operations. M4 adds Orders screens/API using one service, atomic audit/history/receipts, optimistic concurrency and currently authorized replay. There are no public provisioning/operations or customer CMS administration endpoints.

Implementation verification, owner acceptance and merge are separate. Read [M4 verification](docs/verification/m4.md) and [PR #6](https://github.com/chung6a8m/SaasOne/pull/6). ADR 0001–0007 are not automatically promoted to Accepted. No production deployment is implied. Production Approvals, SQL restore/release rehearsal and load/reuse acceptance remain M5–M7.

## Fresh clone without Visual Studio

The evidence-backed environment is Ubuntu 22.04 x64, Git, .NET SDK **10.0.403**, PowerShell **7.6.9** and Docker. VS Code is optional. SDK selection is exact (`rollForward: disable`); other operating systems/architectures are not certified by this evidence.

Clone with your normal GitHub authentication, enter the root and run:

```powershell
dotnet --info
pwsh --version
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
dotnet test SaasOne.slnx --configuration Release --no-build --filter 'Category!=SqlM1&Category!=SqlM2&Category!=SqlM3&Category!=SqlM4' --logger trx --results-directory artifacts/test-results/m0
pwsh -NoProfile -File scripts/check-m4.ps1
if ($LASTEXITCODE -ne 0) { throw 'M4 verification failed.' }
```

The full gate runs inherited M0–M3 checks, five owned EF snapshot checks, restricted SQL smoke and the frozen M4 inventories. Locked build, dependency/native-feature architecture, configuration hygiene and evidence validation remain mandatory. SQL fixtures use disposable loopback-bound SQL Server 2022 Developer containers at build **16.0.4305.5**, generated credentials and mandatory cleanup. Developer edition is for development/testing only. All four SQL categories excluded from the non-SQL command are mandatory in full acceptance, never silently skipped.

| Gate | Additional coverage |
|---|---|
| M0 | Locked dependencies, non-SQL behavior, architecture, secrets and evidence validators. |
| M1 | Four customer databases, Orchard routing, immutable identity, provision/retry, ownership, readiness and restricted privileges. |
| M2 | Native login, discovery, PKCE/code redemption, SSO, signed JWT binding, current authority, refresh races/reuse/lost response and production composition. |
| M3 | ACL/overrides/resource policy on UI/API, safe administration/CSRF, atomic audit, write-gate drain/fences/crash/retry, lifecycle races, external ACL recovery/child upgrade and target-only purge. |
| M4 | Orders create/read/edit/Submit, Own/Others Deny, revision/rowversion conflicts, atomic history/audit/receipt/handler writes, idempotency races/lost responses/current-policy replay, sticky obligations, private upgrade and SQL boundaries. |

TestServer replaces HTTP transport, not authentication or SQL. These tests do not certify production DNS/TLS/proxy logging/secret delivery, M6 SQL restore or M7 distributed load. Missing/duplicate/zero/skipped/failing mandatory cases, stale source reports and cleanup errors cannot pass. JSON/TRX are under `artifacts/verification` and `artifacts/test-results`; `m4-acceptance.json` requires the entire prerequisite chain to identify the same tested SHA.

Focused scripts `sql-fixture.ps1`, `m1-sql-fixture.ps1`, `m2-sql-fixture.ps1`, `m3-sql-fixture.ps1` and `m4-sql-fixture.ps1` under `scripts` do not replace full acceptance. `check.ps1 -Scope M0` remains the foundation gate. A separately prepared matching SQL smoke instance uses secret-managed `SAAS_SQL_SMOKE_CONNECTION` and `-Scope SqlSmoke`; never paste credentials into commands or Git.

## Provisioning and identity

Read the [M1 runbook](docs/runbooks/m1-tenancy-provisioning.md) before injecting secrets. Bootstrap/provisioning are private, not HTTP. Runtime, operator, per-database migrator and server provisioner have distinct SQL privileges.

```powershell
./scripts/tenant-provision.ps1 -Bootstrap -InstallationId $installationId
./scripts/tenant-provision.ps1 -TenantId $tenantId -OperationId $operationId -Slug alpha -Plan Standard -ExpiresAt $expiresAt
```

IDs/expiry come from persisted deployment inventory; retries reuse the complete tuple. Resource coordination retains a trusted operations host, durable local process lock, non-expiring SQL session lock and prior-worker reconciliation. Cross-host takeover and unidentified existing targets are blocked.

M1 baseline mode remains available without identity. `/health/live` proves liveness only. Setup/admin/template/recipe/feature/upload surfaces stay closed; unknown/unready customers never fall through to Default business data.

Follow the [M2 identity runbook](docs/runbooks/m2-identity.md): provision, privately run `identity-init`, explicitly enroll accounts/memberships and enable `SAAS_IDENTITY_ENABLED=true`. Supply persisted signing/encryption certificates, protected persistent Data Protection, canonical HTTPS issuer and independent tenant client secrets. The HTTP-only port-5080 shortcut is not an identity deployment. Missing state is a stop condition, never permission for runtime migration/seeding.

HTTP receives only restricted runtime connections and identity configuration. Central `/account/login` and `/account/tenants` provide login/tenant choice; `/{slug}/account/login` performs native OIDC, `/{slug}/account` shows the tenant session, and tenant-prefixed `/api/v1/identity` requires bearer authentication. Callback/logout destinations must match the independent authoritative registry.

Private commands include `identity-init`, `identity-verify`, `identity-client`, `identity-enroll`, `identity-membership`, `identity-account` and `identity-tenant`. Enrollment passwords use one-command environment variables, never argv. Completed initialization retry is read-only; registration is not a startup side effect. Retired tenants cannot regain access through retry/registration.

Secret delivery is environment-only. Parent/maintenance environment changes do not update a running HTTP process. Change trusted deployment configuration and restart/recreate deliberately; dynamic secret delivery and zero-downtime provisioning are not claimed.

## M3 access and operations

Follow the [M3 runbook](docs/runbooks/m3-operations.md). Privately run `access-install` per customer, establish initial administrators through externally approved recovery, and enable `SAAS_ACCESS_CONTROL_ENABLED=true` alongside identity. Initial ACL is empty, not implicit administrator. Runtime validates schema/profile/current rights and never repairs them.

`/{slug}/access` and bearer `/api/v1/access` share a safe administration service. Web writes require antiforgery; commands carry operation UUID, ACL revision and gate version. They cannot elevate the actor or delegate `Access.Manage`. State/history/audit commit on one customer transaction; audit failure rolls back the complete mutation.

Workspace/approval/profile policy probes retain matching UI/API behavior. Shell routing, not body/header/token hints, selects the connection. Deny dominates Allow; required-source failure never reuses a previous allow. Customer CMS administration remains closed.

The operator UI is a separate private terminal, not a privileged HTTP dashboard:

```powershell
pwsh -NoProfile -File scripts/operations-console.ps1 -AssemblyPath <published/SaaS.Web.dll>
```

It invokes authenticated lifecycle, plan, recovery and purge operations. New suspension/deletion/expiry wins over stale completion. Purge requires PendingDeletion, repeated target confirmation, quiescence and immutable identity; global accounts and unrelated resources survive, with a permanent registry tombstone. Recovery authority comes from current platform state registered before the operation, not restored tenant roles/audit, and applies a reviewed complete ACL. The console does not perform SQL backup/restore or mutate an external secret manager.

## M4 Orders

Follow the [Orders runbook](docs/runbooks/m4-orders.md). Private `access-install` advances application profile to version 2, applies the owned Orders and audit-detail migrations, preserves ACL and grants narrow runtime DML under the write gate. Existing M3 deployments require private reconciliation before this binary serves Orders. Unknown future profiles, corrupt installed schema and unknown business tables fail closed; there is no runtime repair. Orders permissions start default-denied.

`/{slug}/orders` and bearer `/{slug}/api/v1/orders` support create, list, read, edit draft and Submit through one service. Web writes use antiforgery; API writes require `Idempotency-Key`; existing-order writes require expected revision/rowversion. Retry a lost response with the same key and original command, not substituted fresh concurrency values. Current action and read permission remain required for replay.

Standard confirms only without a persisted approval obligation. Required-approval submission needs current entitlement and a ready handler. M4 registers none and returns 503 rather than falling back to Confirmed. Rejected/rework retains its marker. The host composes the independent persisted Orders obligation reader, including when an approval handler is absent. M5 must deliberately extend the closed table inventory for its own tables; production decision routes are not hidden M4 endpoints.

[ADR 0007](docs/adr/0007-orders-command-contract.md) proposes the minimum field model, compiled-module composition and seven-day replay window. Expired receipts remain tombstones; no automatic deletion or silent key reuse exists. Product fields and data-retention policy require owner review.

## Repository and maintenance

| Path | Responsibility |
|---|---|
| `src/SaaS.Web` | Product composition, native host, application routes and private commands. |
| `src/Platform/SaaS.Tenancy` | Registry, resource ownership, immutable identity, write gate and shared transaction coordinator. |
| `src/Platform/SaaS.Identity` | Native integration, current authority, sessions, JWT binding and refresh rotation. |
| `src/Platform/SaaS.AccessControl` | Permission catalog, tenant ACL, safe administration, transaction-bound authorization and recovery. |
| `src/Platform/SaaS.Auditing` | Append-only customer audit. |
| `src/Modules/SaaS.Orders` | State/policy, owned EF stream, atomic service, replay and obligation reader. |
| `src/Platform/SaaS.Abstractions`, `src/Themes/SaaS.Theme.App` | Shared contracts and platform-controlled theme. |
| `tools`, `tests`, `docs` | Verification tooling, tests, source-bound evidence, decisions and runbooks. |

Tenancy owns `platform`, `identity`, `tenancy`; AccessControl owns `access`; Auditing owns `audit`; Orders owns `orders`; Orchard owns its native tables/history. Platform/shared theme never reference business modules, including transitively. Read [AGENTS.md](AGENTS.md), [conventions](docs/conventions.md), [compatibility](docs/compatibility/m0-matrix.md), [M0 decisions](docs/decisions/m0-register.md) and ADRs before boundary changes.

`dev.ps1 -Action Setup/Run/Check` retains baseline behavior; identity/M3/M4 require separately configured HTTPS and trusted state. Inherited workflows remain. `.github/workflows/m4.yml` runs full acceptance on PRs to main, main pushes and dispatch; component workflows are not full acceptance. Actions are pinned, final workflow permissions read-only, and artifacts contain source/JSON/TRX rather than raw credential-bearing stdout. The temporary branch-only development writer is removed.

Normal builds never rewrite locks. For reviewed dependency changes, run `scripts/update-locks.ps1`, inspect graph/locks, update rationale and rerun all gates. Never bypass NuGet audit. Rich text, executable uploads and arbitrary customer scripts remain disabled.
