# SaasOne

Reusable Orchard Core multi-tenant SaaS foundation. The reviewed [roadmap](docs/roadmap/saas-orchard-roadmap-v1.md) is the specification. This branch implements **M0 only**: solution, dependency boundaries, decision records, pinned toolchain, verification scripts and CI. It does not provision tenants, issue tokens, migrate production databases or implement Orders.

The host is deliberately closed: `/health/live` reports process liveness; all other routes return 503. It is not an Orchard tenant readiness endpoint. M1 must implement controlled bootstrap, explicit CMS registrations, tenant identity and readiness before enabling tenant middleware. Identity is [Conditional — pending M2 PoC](docs/adr/0001-identity-and-membership.md).

## Fresh clone without Visual Studio
Supported M0 environment: Ubuntu 22.04 x64 (including a matching WSL2 distribution), Git, .NET SDK **10.0.403**, PowerShell **7.6.9**. Use VS Code optionally. Install the SDK from [Microsoft's SDK downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and PowerShell from the [pinned release](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.9). Exact SDK selection is mandatory (`rollForward: disable`). Other OS/architectures have not been certified by M0 evidence.

Clone this repository with your normal GitHub authentication and enter its root. Never put an access token in a clone command or config file. Run in this order:

```powershell
dotnet --info
pwsh --version
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
dotnet test SaasOne.slnx --configuration Release --no-build --logger trx --results-directory artifacts/test-results/m0
pwsh -NoProfile -File ./scripts/check.ps1 -Scope M0
```

`M0` repeats locked restore/build/tests, clears its own old TRX output, verifies both required test suites (no empty/skipped success), checks the restored dependency graph and scans tracked configuration/secret risks. `Prerequisites` is a fast tool/version check. Native non-zero exit codes fail the script. The emitted `artifacts/verification/m0.json` identifies the commit, environment, commands and real suite counts; it is not hand-maintained.

## Real SQL Server smoke (separate mandatory gate)
The expected non-production target is `saas_m0_smoke`, SQL Server 2022 Developer on Linux, build **16.0.4305.5**. An unavailable/mismatched target or missing secret is BLOCKED/FAIL, never skipped. Do not run the fixture on a production server. SQL Developer is for development/testing only.

After the foundation checks, Docker users can run a disposable local fixture:

```powershell
pwsh -NoProfile -File ./scripts/sql-fixture.ps1
```

The fixture binds to loopback, creates a fresh uniquely named container, creates only the fixed test DB and a CONNECT-only test login, performs a wrong-target negative test, runs the exact SQL check below, and removes that container in finally. Credentials are generated per run and passed via environment/stdin, never written to files. It does not touch an existing SQL instance. Docker daemon access is required.

For an operator-prepared matching instance, inject the connection as the environment variable `SAAS_SQL_SMOKE_CONNECTION` from your secret manager, then run the eighth roadmap command:

```powershell
pwsh -NoProfile -File ./scripts/check.ps1 -Scope SqlSmoke
```

Do not paste a password into shell history. The expected database/version are fixed in `tools/SaaS.SqlSmoke`, not taken from arbitrary expected-value overrides. The SQL tool opens an EF Core SQL Server connection, checks real engine/edition/platform/database/version/privileges and executes a query. `artifacts/verification/sql-smoke.json` contains sanitized evidence only. This proves connectivity, **not** M1 isolation/DDL behavior or M6 backup/restore.

## Development and repository map
`pwsh -NoProfile -File scripts/dev.ps1 -Action Setup` restores the locked baseline. `-Action Run` starts the closed host on loopback port 5080; `-Action Check` invokes M0.

| Path | Responsibility |
|---|---|
| `src/SaaS.Web` | Product composition root and closed M0 host |
| `src/Platform` | Abstractions, Tenancy, Identity, AccessControl, Auditing boundaries |
| `src/Themes/SaaS.Theme.App` | Platform-controlled shared theme boundary |
| `tools/SaaS.Build`, `tools/SaaS.SqlSmoke` | Verification tooling, never runtime tenant services |
| `tests/Unit`, `tests/Architecture` | Host, scripts, evidence, hygiene and dependency tests |
| `docs/adr`, `docs/decisions` | Architectural decisions, owners and future proof gates |

Read [AGENTS.md](AGENTS.md), [conventions](docs/conventions.md), [compatibility matrix](docs/compatibility/m0-matrix.md), [decision register](docs/decisions/m0-register.md) and [verification record](docs/verification/m0.md) before changes. Platform/theme may not depend directly or transitively on business modules; the host alone composes them. No product module is faked merely to make a test green.

## CI and dependency maintenance
`.github/workflows/ci.yml` uses read-only repository permissions and separate foundation/SQL jobs. Both gates are required for acceptance; SQL is not optional when credentials are missing. No production secrets are needed for the ephemeral SQL fixture. Evidence artifacts are scoped to the tested SHA and run.

Normal builds do not rewrite locks. After a reviewed dependency change, run `pwsh -NoProfile -File scripts/update-locks.ps1`, inspect the NuGet lockfile and resolved-graph diff, update the matrix/selection rationale and rerun both gates. Do not bypass NuGet audit. Indirect prerelease sanitizer packages inherited from Orchard are recorded in the matrix; rich text and uploads remain disabled.

Owner review/adoption of the added implementation defaults is still required before merge or dependent implementation. It does not close M1–M6 runtime gates or the M2 identity PoC. No production deployment is implied.
