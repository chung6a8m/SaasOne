# M0 compatibility matrix

Selected 2026-11-06. Concrete baseline, not a claim that every later feature works. Actual execution status/commit is in [verification](../verification/m0.md). Dependency closure and hashes are generated in [resolved-packages.json](resolved-packages.json) and all eleven project `packages.lock.json` files. The lockfiles, not a hand-written package list, are authoritative.

| Dimension | Pinned choice / acquisition / policy |
|---|---|
| SDK / target | .NET SDK 10.0.403, net10.0; `global.json` rollForward disable, prerelease SDK false |
| Runtime in verified runner | Microsoft.NETCore.App / Microsoft.AspNetCore.App 10.0.14, linux-x64 |
| Orchard | 3.0.1: Application.Targets, Infrastructure, OpenId, Theme.Targets, DisplayManagement. Explicit packages, not all-CMS/media bundle |
| EF / provider | EF Core + SQL Server provider + Design 10.0.14; SQL Server only for application data |
| Local tool | dotnet-ef 10.0.14 from root `.config/dotnet-tools.json`; no global-tool dependency |
| PowerShell | 7.6.9 linux-x64 release tar; SHA256 `1da6d6d6035d651f20e70ddee7c4c15ac6ab8b9fb40ee92240d91263f28a884b` |
| Test runner | Microsoft.NET.Test.Sdk 18.0.1; xunit.v3 3.2.2; Visual Studio adapter 3.1.5; VSTest via dotnet test, no Visual Studio installation |
| Host test | Microsoft.AspNetCore.Mvc.Testing 10.0.14 |
| Dev baseline | Ubuntu 22.04 x64, VS Code optional, Git CLI; matching WSL2 distro allowed but not separately certified |
| CI | GitHub Actions, hosted ubuntu-22.04 x64, observed image 20261101.347.1; moving runner image recorded on each run, not assumed immutable |
| SQL baseline | SQL Server 2022 Developer, Linux x64 Ubuntu 22.04 container, CU27-GDR1 product version 16.0.4305.5; dedicated saas_m0_smoke DB |
| SQL acquisition | Microsoft registry image `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04`; fixture captures resolved digest and validates actual ProductVersion (no latest tag) |
| NuGet sources | nuget.org v3 only, sources cleared and mapped in NuGet.config; central versions + transitive security pins; generated per-project locks; locked normal restore |
| SQL rights | Separate runtime/migrator/provisioner allocations in ADR 0003. M0 smoke principal CONNECT-only; M1 still must prove tenant runtime least privilege and Orchard DDL behavior |
| Secrets | Environment reference SAAS_SQL_SMOKE_CONNECTION; local user-secrets ID SaasOne.Web.Development; no secret values in tracked files; fixture credentials exist only for the disposable run |
| Transitive updates | AngleSharp 1.8.5, MySqlConnector 2.6.2, Cryptography.Pkcs/Xml 10.0.14; NuGet audit remains all + warnings-as-errors |
| Indirect prerelease exception | Orchard resolves HtmlSanitizer 9.1.893-beta and AngleSharp.Css 1.0.0-beta.159; exact locked graph, owner review required; no customer rich-text/upload support in M0 |

## Resolved persistence and identity graph
Orchard Infrastructure/Data.YesSql resolve YesSql, YesSql.Core and YesSql.Abstractions **5.4.7**. Orchard OpenId/OpenId.Core resolve OpenIddict Core/Server/Validation and corresponding ASP.NET Core, DataProtection and SystemNetHttp components **7.5.0**. Full dependency edges and integrity hashes are in the generated graph and host lockfile. These package resolutions do not prove cross-shell identity or SQL migrations.

The original all-CMS restore failed audit and was replaced rather than suppressing warnings; see [package selection](package-selection.md). The selected non-SQL database libraries in Orchard's dependency closure do not enable PostgreSQL/MySQL/SQLite as application providers. M0 tests never substitute them for SQL Server.

## Version changes
Exact pins intentionally require a reviewed update; missing/outdated/vulnerable packages fail rather than silently roll forward. Regenerate locks with `scripts/update-locks.ps1`, preserve package hashes from NuGet, review indirect prereleases and licenses, rerun foundation and real SQL, then record the new tested commit/environment. An SDK installation or successful restore alone is not compatibility acceptance.

Sources: https://dotnet.microsoft.com/en-us/download/dotnet/10.0 ; https://github.com/PowerShell/PowerShell/releases/tag/v7.6.9 ; https://learn.microsoft.com/en-us/sql/database-engine/install-windows/latest-updates-for-microsoft-sql-server?view=sql-server-ver16 ; official NuGet links in package-selection.md; actual CI/lock evidence in the verification record.
