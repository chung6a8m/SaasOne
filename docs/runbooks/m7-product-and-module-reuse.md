# M7 — Independent product and module reuse

## Scope and boundaries

`samples/Portal` is a second composition root, not a feature-disabled copy of `SaaS.Web`.
It references the platform libraries and `samples/Portal.Theme`; its published closure must contain
neither the original host nor Orders/OrderApprovals. Its own theme supplies the landing-page markup
and native Orchard theme metadata. This deliberately read-only example does **not** offer customer
business routes, tenant SSO, account management, setup or a second business domain.

No platform/core/business runtime file or migration is changed to make this example work.
The platform schema is installed through the existing controlled operations; the sample never
migrates at startup. The operator/provisioner/admin connections must not be present in its process.

## Verify, publish and start

From a clean checkout with the repository-pinned .NET SDK and PowerShell:

```powershell
pwsh -NoProfile -File scripts/check.ps1 -Scope M0
pwsh -NoProfile -File scripts/check-m7-product.ps1
pwsh -NoProfile -File scripts/m7-sql-fixture.ps1 -Mode Baseline
```

The product gate uses committed NuGet locks, independently restores and publishes the sample,
then inspects project/source closure, resolved package assets, `.deps.json`, every published file,
and native `IExtensionManager` feature dependencies. The final CI workflow is read-only and does
not regenerate lock files. The SQL gate additionally starts the actual published DLL as a separate
process with runtime-only credentials, checks ready/render, missing source, wrong database,
overprivileged runtime and an incomplete platform release. It also runs all other M7 SQL/load cases.

For an already provisioned private installation, supply **only** `SAAS_SQL_SERVER` and
`SAAS_PLATFORM_RUNTIME_CONNECTION` through the deployment secret provider. They must select the
fixed platform database and the exact low-privilege runtime principal; never pass secrets as CLI
arguments or commit their values. Then run:

```powershell
dotnet artifacts/m7/product/publish/SaaS.Portal.dll --urls http://127.0.0.1:5088
```

HTTP loopback is a local smoke-test binding, not a production TLS configuration. Deployment TLS,
secret rotation and the operational prerequisites in the roadmap remain separate release decisions.
`GET /health/live` proves process liveness only. `/health/ready` and `/` return 503 while platform
connection, privilege, schema, bootstrap or release readiness is invalid. A ready root renders the
Portal theme. Unmatched routes, including setup and Orders URLs, remain 404 once ready.

## Generate a module

```powershell
dotnet new install ./templates/module
dotnet new saas-module --name SaaS.Inventory --output src/Modules/SaaS.Inventory
dotnet restore src/Modules/SaaS.Inventory/SaaS.Inventory.csproj
dotnet build src/Modules/SaaS.Inventory/SaaS.Inventory.csproj --no-restore
```

The module is intentionally inert and references only Abstractions. Review and commit its generated
NuGet lock before adding it to the solution and explicitly selecting it from a product host.
Run `pwsh -NoProfile -File scripts/check-m7-template.ps1` for isolated installation, name replacement,
restore/build and resolved-dependency smoke verification without modifying the developer's normal
CLI template home. The generated test module stays under ignored `artifacts/m7/`.

A business implementation must add explicit permissions and resource rules, current entitlement
checks, routed tenant selection, optimistic concurrency, idempotency and transactional audit.
A new table requires a named exclusive schema/history/DbContext/migration owner and real SQL tests.
Do not generate runtime migrations, dynamic plugin loading, shared-theme business dependencies or
public setup. Update the independent-product graph tests whenever composition changes.
