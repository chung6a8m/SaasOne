# Repository conventions

`src/SaaS.Web` composes platform and product modules. `src/Platform` contains reusable capabilities; `src/Themes` contains platform-controlled presentation. Business modules are added in M4/M5, not represented by fake M0 implementations. Empty capability projects intentionally establish the dependency boundaries, not service contracts.

Use nullable-enabled C#, UTC instants (`DateTimeOffset`), stable opaque IDs, cancellation tokens on I/O and explicit unavailable/denied outcomes. Business UI and API call the same application service and resource policy. Do not infer tenant identity from a SQL database name or an email address.

Package versions belong in `Directory.Packages.props`; use exact SDK/tool pins and commit NuGet-generated lockfiles. Normal builds use locked restore. A package update is a reviewed change that regenerates locks and resolved dependency evidence, reruns both gates and updates the compatibility matrix. Do not edit hashes by hand, suppress audit warnings to make CI green, or auto-update dependencies during checks.

Tests live under `tests/Unit` and `tests/Architecture` for M0. Add SQL integration tests with the first data change in M1; EndToEnd and Load enter their owning milestones. Real restored MSBuild/NuGet dependencies are checked, not just a hard-coded expected graph. Feature dependency parsing must be extended/tested before the first M1 feature dependency is introduced; unsupported feature declarations fail closed.

Migrations and provider SQL remain SQL Server-specific in V1. PostgreSQL needs a separate tested migration stream later. Each migration PR names its owner and rollback/recovery implications; different agents never edit the same stream concurrently. Successful tenant business changes and tenant audit share one transaction. No distributed transaction across platform/customer databases is assumed.

All scripts run from any current directory by resolving the repository from `$PSScriptRoot`. Fail on native non-zero exit codes. Outputs go under ignored `artifacts/`; no secrets in transcripts. The supported M0 execution environment is Linux x64 with the pinned toolchain; Windows/macOS support is not inferred from cross-platform APIs.
