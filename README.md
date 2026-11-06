# SaasOne

Reusable Orchard Core multi-tenant SaaS foundation. The reviewed [roadmap](docs/roadmap/saas-orchard-roadmap-v1.md) is the specification. This branch implements the M0 foundation and **M1 tenancy/provisioning**: a fixed platform database, isolated customer databases, private maintenance commands, native Orchard prefix routing and fail-closed readiness.

Identity/SSO remains [Conditional — pending M2 PoC](docs/adr/0001-identity-and-membership.md). There are no customer business APIs, Orders/OrderApprovals implementation, public setup/provisioning endpoint or tenant CMS administration. No production deployment is implied.

## Fresh clone without Visual Studio

The evidence-backed environment is Ubuntu 22.04 x64, Git, .NET SDK **10.0.403**, PowerShell **7.6.9**, and Docker for the SQL gates. VS Code is optional. Exact SDK selection is mandatory (`rollForward: disable`); other operating systems and architectures have not been certified by this evidence.

Clone with your normal GitHub authentication, enter the repository root, and run:

```powershell
dotnet --info
pwsh --version
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
dotnet test SaasOne.slnx --configuration Release --no-build --filter 'Category!=SqlM1' --logger trx --results-directory artifacts/test-results/m0
pwsh -NoProfile -File ./scripts/check.ps1 -Scope M0
```

The retained `M0` command is the mandatory foundation gate, including the new M1 non-SQL tests. It repeats locked restore/build/tests, rejects empty/skipped evidence, checks restored project/package **and native Orchard feature** dependencies, and scans tracked configuration for secrets. Native nonzero exit codes fail the gate. `artifacts/verification/m0.json` records the tested SHA, environment, commands and actual suite counts.

The `SqlM1` category is excluded only from this non-SQL command. It is mandatory in its own real SQL gate below. Running all tests without that filter requires the private fixture environment and fails when SQL is absent; SQL tests are never silently skipped.

## Mandatory real SQL gates

After the foundation build, run both isolated fixtures:

```powershell
pwsh -NoProfile -File ./scripts/sql-fixture.ps1
pwsh -NoProfile -File ./scripts/m1-sql-fixture.ps1
```

Both require Docker, bind to loopback, create fresh containers from the pinned SQL Server 2022 Developer image/build **16.0.4305.5**, generate disposable credentials, and remove their containers before publishing success. They never target an existing deployment. SQL Developer is for development/testing only.

The M0 fixture proves exact provider/engine/version/database/privilege connectivity, including a wrong-target negative probe. Its expected database is `saas_m0_smoke`. For an operator-prepared matching test instance, inject `SAAS_SQL_SMOKE_CONNECTION` from a secret manager and run `scripts/check.ps1 -Scope SqlSmoke`; never paste credentials into commands or Git.

The M1 fixture proves private platform bootstrap, four real customer databases, actual Orchard routing, idempotent retry, concurrent/stale operation rejection, lifecycle preservation, immutable identity, missing/corrupt baseline rejection, forbidden cross-database/DDL access and fresh runtime-only process restarts. It requires at least 38 executed passing cases, validates named mandatory cases, and writes `artifacts/verification/m1-sql.json` plus TRX evidence. A missing SQL prerequisite, skipped test, failed assertion or cleanup failure cannot produce PASS.

## Provisioning and runtime

Read the [M1 runbook](docs/runbooks/m1-tenancy-provisioning.md) before injecting secrets or running maintenance. Bootstrap and provisioning use **separate private processes**, not HTTP. Runtime, operator, per-database migrator and server provisioner credentials have different privileges. The runtime process must receive only runtime secrets.

```powershell
# IDs and expiry below come from a persisted private deployment inventory.
./scripts/tenant-provision.ps1 -Bootstrap -InstallationId $installationId
./scripts/tenant-provision.ps1 -TenantId $tenantId -OperationId $operationId -Slug alpha -Plan Standard -ExpiresAt $expiresAt
```

Retry uses the same complete request tuple. The supported M1 coordinator uses one trusted operations machine, a durable local process lock, a non-expiring SQL session lock and prior-owner SQL-session reconciliation. Cross-host takeover and unidentified existing databases are blocked, not automatically adopted. Owner review of these constraints is required before deployment.

`GET /health/live` proves process liveness only. After private provisioning, `GET /health/ready` checks the platform baseline, `GET /{slug}/health/ready` checks the selected customer's readiness, and `GET /{slug}/` serves a minimal tenant-prefixed foundation page. Setup, admin, feature, template, recipe and upload HTTP surfaces remain closed. Unknown/unready tenants never fall through to Default customer business data. Newly completed tenants can be loaded without restarting the host.

`Standard` and `Approval` currently share the reviewed foundation profile. M1 records the selected plan but does not fake business modules scheduled for later milestones. TenantContext, customer connection, prefixed links and cache partition derive from the actual routed shell, not request-supplied tenant identifiers.

## Development and repository map

`pwsh -NoProfile -File scripts/dev.ps1 -Action Setup` restores the locked baseline. `-Action Run` starts the host on loopback port 5080; without runtime secrets its business/readiness surfaces stay closed. `-Action Check` runs the foundation gate.

| Path | Responsibility |
|---|---|
| `src/SaaS.Web` | Composition root, private CLI, native Orchard adapter and HTTP readiness boundary |
| `src/Platform/SaaS.Tenancy` | Registry, operation coordination, identity, owned EF contexts and migrations |
| `src/Platform` | Abstractions, Identity, AccessControl and Auditing module boundaries |
| `src/Themes/SaaS.Theme.App` | Platform-controlled shared theme boundary |
| `tools/SaaS.Build`, `tools/SaaS.SqlSmoke` | Verification tooling, never runtime tenant services |
| `tests/Unit`, `tests/Architecture` | Behavioral, real SQL, native-feature, dependency and evidence tests |
| `docs/verification`, `docs/runbooks` | Source-bound verification records and private operational procedures |

Read [AGENTS.md](AGENTS.md), [conventions](docs/conventions.md), [compatibility matrix](docs/compatibility/m0-matrix.md), [M0 decision register](docs/decisions/m0-register.md), [M0 evidence](docs/verification/m0.md) and [M1 evidence](docs/verification/m1.md). Platform/theme must not depend directly or transitively on business modules. Tenancy owns the `platform` and `tenancy` EF streams; Orchard owns its native tables/migration record. Runtime never applies EF migrations.

## CI and dependency maintenance

The existing `.github/workflows/ci.yml` foundation/SQL jobs remain. `.github/workflows/m1.yml` adds the full M1 gate on the implementation branch, `main`, and pull requests. It uses read-only repository permissions, checks retained migration snapshots and PowerShell syntax, runs both SQL fixtures, and uploads SHA-scoped evidence and sanitized logs. No deployment secrets are needed.

Normal builds do not rewrite locks. After a reviewed dependency change, run `scripts/update-locks.ps1`, inspect lock/resolved-graph changes, update the matrix/rationale and rerun every gate. Do not bypass NuGet audit. M1 retains the M0 package pins; dormant OpenID dependencies are explicitly unavailable and cannot enter the enabled M1 profile. Rich text, arbitrary scripts and uploads remain disabled.

M1 owner review and merge are separate from implementation verification. M2–M6 acceptance gates remain open.
