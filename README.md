# SaasOne

Reusable Orchard Core multi-tenant SaaS foundation. The reviewed [roadmap](docs/roadmap/saas-orchard-roadmap-v1.md) is the specification. This branch implements M0 foundation, M1 tenancy/provisioning and **M2 central identity**: a fixed platform database, isolated customer databases, private maintenance commands, native Orchard prefix routing, fail-closed readiness, central login, tenant SSO and tenant-bound API tokens.

Identity uses native Orchard Users/OpenIddict at Default, with independent customer cookies and server-side membership/session/grant validation. The [identity ADR](docs/adr/0001-identity-and-membership.md) remains Conditional pending owner architecture/security acceptance; implementation evidence is recorded in [M2 verification](docs/verification/m2.md). There are no Orders/OrderApprovals or customer business APIs, public setup/provisioning endpoints, or tenant CMS administration. No production deployment is implied.

## Fresh clone without Visual Studio

The evidence-backed environment is Ubuntu 22.04 x64, Git, .NET SDK **10.0.403**, PowerShell **7.6.9**, and Docker for the SQL gates. VS Code is optional. Exact SDK selection is mandatory (`rollForward: disable`); other operating systems and architectures have not been certified by this evidence.

Clone with your normal GitHub authentication, enter the repository root, and run:

```powershell
dotnet --info
pwsh --version
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
dotnet test SaasOne.slnx --configuration Release --no-build --filter 'Category!=SqlM1&Category!=SqlM2' --logger trx --results-directory artifacts/test-results/m0
pwsh -NoProfile -File ./scripts/check.ps1 -Scope M0
```

The retained `M0` command is the mandatory foundation gate. It repeats locked restore/build/tests, rejects empty/skipped evidence, checks restored project/package **and native Orchard feature** dependencies, and scans tracked configuration for secrets. Native nonzero exit codes fail the gate. `artifacts/verification/m0.json` records the tested SHA, environment, commands and actual suite counts.

`SqlM1` and `SqlM2` are excluded only from the non-SQL command. Both are mandatory in the complete SQL gate below. Running all tests without that filter requires the private fixture environment and fails when SQL is absent; SQL tests are never silently skipped.

## Mandatory complete gate

```powershell
pwsh -NoProfile -File ./scripts/check-m2.ps1
if ($LASTEXITCODE -ne 0) { throw 'M2 verification failed.' }
```

This runs the M0 foundation, both EF snapshot checks, restricted SQL smoke, all M1 SQL/Orchard tests and all M2 native-store/HTTP/production-composition tests. The fixtures require Docker, bind to loopback, create fresh containers from the pinned SQL Server 2022 Developer image/build **16.0.4305.5**, generate disposable credentials and remove their containers before publishing success. They never target an existing deployment. SQL Developer is for development/testing only.

For focused regression after a foundation build, `scripts/sql-fixture.ps1`, `scripts/m1-sql-fixture.ps1` and `scripts/m2-sql-fixture.ps1` run the individual disposable fixtures. They do not replace the complete gate for milestone acceptance.

The SQL smoke proves provider/engine/version/database/privilege connectivity, including an expected privileged-connection rejection before the restricted-user positive probe. Its database is `saas_m0_smoke`. For a separately prepared matching test instance, inject `SAAS_SQL_SMOKE_CONNECTION` from a secret manager and run `scripts/check.ps1 -Scope SqlSmoke`; never paste credentials into commands or Git.

M1 covers private platform bootstrap, four customer databases, actual Orchard routing, idempotent retry, concurrent/stale operation rejection, lifecycle preservation, immutable identity, corrupt-baseline rejection, forbidden cross-database/DDL access and runtime-only process restarts. M2 adds real native login/discovery/code redemption/PKCE, SSO, cryptographic JWT validation, tenant binding, persistent authority and revocation, strict refresh concurrency/reuse/lost-response behavior, key reload, private CLI and the production host's registry/readiness boundary. TestServer replaces HTTP transport, not authentication or SQL stores. Neither fixture certifies an actual deployment's DNS, TLS listener, proxy logging or secret-manager access controls.

Missing prerequisites, missing mandatory cases, duplicate/zero execution, skipped/failing tests or cleanup failures cannot produce PASS. JSON and TRX evidence remain under `artifacts/verification` and `artifacts/test-results`; the historical `m2-poc` path now contains the complete M2 acceptance suite.

## Provisioning and runtime

Read the [M1 runbook](docs/runbooks/m1-tenancy-provisioning.md) before injecting secrets or running maintenance. Bootstrap and provisioning use separate private processes, not HTTP. Runtime, operator, per-database migrator and server provisioner credentials have different privileges.

```powershell
# IDs and expiry come from a persisted private deployment inventory.
./scripts/tenant-provision.ps1 -Bootstrap -InstallationId $installationId
./scripts/tenant-provision.ps1 -TenantId $tenantId -OperationId $operationId -Slug alpha -Plan Standard -ExpiresAt $expiresAt
```

Retry uses the same complete request tuple. The M1 coordinator uses one trusted operations machine, a durable local process lock, a non-expiring SQL session lock and prior-owner SQL-session reconciliation. Cross-host takeover and unidentified existing databases are blocked. Owner review of these constraints is required before deployment.

M1 baseline mode remains available when identity is not enabled: `GET /health/live` proves liveness only, readiness validates the platform/customer baseline, and `GET /{slug}/` serves the foundation page. Setup, admin, feature, template, recipe and upload surfaces remain closed. Unknown/unready tenants never fall through to Default customer business data.

For M2, follow the [identity runbook](docs/runbooks/m2-identity.md): provision first, privately run `identity-init`, explicitly enroll native accounts and memberships, and then start HTTP with `SAAS_IDENTITY_ENABLED=true`. Supply persisted signing/encryption certificates, a persistent protected Data Protection key ring, canonical HTTPS issuer and independent tenant client secrets. Do not enable identity through the HTTP-only port-5080 development shortcut. Missing state is a stop condition, not permission for the HTTP process to migrate or seed.

The HTTP process receives only restricted platform/customer runtime connections and identity runtime configuration, never provisioner/operator/migrator credentials. The same production composition is exercised by the M2 acceptance fixture with all privileged connection variables removed. Central `/account/login` and `/account/tenants` provide login and tenant choice. `/{slug}/account/login` runs the native OIDC flow, `/{slug}/account` is the tenant session view, and `GET /{slug}/api/v1/identity` is the bearer-only protected probe. Callback and logout destinations must match the authoritative registry before native redirect handling.

Private identity commands include `identity-init`, `identity-verify`, `identity-client`, `identity-enroll`, `identity-membership`, `identity-account` and `identity-tenant`; see the runbook for exact arguments and credential requirements. Enrollment passwords are injected through a one-command environment variable, never argv. A completed initialization retry is read-only verification. Registering a newly provisioned tenant's client is a separate command, not a side effect of startup or retry.

Secret delivery is environment-only. A new tenant can become available without HTTP restart only when its runtime connection and identity client secret were injected before that process started. Later parent/maintenance environment changes are not visible inside a running process; update the deployment environment and perform a controlled restart/container recreation. There is no claim of dynamic secret delivery or zero-downtime provisioning.

`Standard` and `Approval` retain the reviewed foundation profile; later milestones implement business modules. TenantContext, selected customer connection and prefixed links derive from the actual routed shell, not a header/body/token-selected database. Roles, per-user allow/deny and the full operations UI belong to M3.

## Development and repository map

`pwsh -NoProfile -File scripts/dev.ps1 -Action Setup` restores the locked baseline. `-Action Run` starts baseline mode on loopback port 5080; without runtime secrets business/readiness stay closed. M2 requires a separately configured HTTPS listener and trusted identity configuration. `-Action Check` runs the foundation gate.

| Path | Responsibility |
|---|---|
| `src/SaaS.Web` | Composition root, private CLI, Orchard adapter and HTTP readiness/identity boundary |
| `src/Platform/SaaS.Tenancy` | Registry, operation coordination, immutable identity, platform-owned EF contexts/migrations |
| `src/Platform/SaaS.Identity` | Native identity integration, dynamic authority, sessions, JWT checks and refresh rotation |
| `src/Platform` | Abstractions, AccessControl and Auditing boundaries |
| `src/Themes/SaaS.Theme.App` | Platform-controlled shared theme boundary |
| `tools/SaaS.Build`, `tools/SaaS.SqlSmoke` | Verification tooling, never runtime tenant services |
| `tests/Unit`, `tests/Architecture` | Behavioral, real SQL/HTTP, native-feature, dependency and evidence tests |
| `docs/verification`, `docs/runbooks` | Source-bound results and private operational procedures |

Read [AGENTS.md](AGENTS.md), [conventions](docs/conventions.md), [compatibility matrix](docs/compatibility/m0-matrix.md), [M0 decisions](docs/decisions/m0-register.md), [M0 evidence](docs/verification/m0.md), [M1 evidence](docs/verification/m1.md) and [M2 evidence](docs/verification/m2.md). Platform/theme must not depend directly or transitively on business modules. Tenancy owns the `platform`, `identity` and `tenancy` EF model streams; Orchard owns native tables/history. Runtime never applies EF migrations.

## CI and dependency maintenance

The foundation and M1 workflows remain; `.github/workflows/m2.yml` runs the full M2 gate on PRs into `main`, pushes to `main` and explicit dispatch. All retained verification has read-only repository permissions and pinned actions. The temporary generated-input write workflow is removed. Artifacts contain JSON/TRX evidence, not raw stdout or masking directives. No deployment secrets are needed.

Normal builds do not rewrite locks. After a reviewed dependency change, run `scripts/update-locks.ps1`, inspect lock/resolved-graph changes, update the compatibility rationale and rerun every gate. Do not bypass NuGet audit. M2 enables the pinned native identity dependencies only in its explicit Default identity profile; it does not enable a customer CMS. Rich text, arbitrary scripts and uploads remain disabled.

Implementation verification, owner architecture/security acceptance and merge are separate. PR #4 carries the latest source-bound evidence; ADR 0001 is not automatically promoted to Accepted. M3–M7 remain outside this implementation.
