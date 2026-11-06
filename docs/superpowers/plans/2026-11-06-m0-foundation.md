# M0 foundation implementation plan

**Goal:** Deliver the M0 foundation and reproducible evidence, without implementing M1–M7.
**Architecture:** One closed ASP.NET Core host; platform assemblies independent of product modules; separate verification tools and test suites. Preserve the reviewed roadmap.
**Tech stack:** .NET SDK 10.0.403; Orchard Core 3.0.1; EF Core SQL Server 10.0.14; PowerShell 7.6.9.
**Spec:** `docs/roadmap/saas-orchard-roadmap-v1.md`, revision 1.3, baseline `639a03253bc653758f2a028909cec9b163e0de73`.

## Constraints and rulings
- Work on `feat/m0-foundation`; owner reviews/merges. Do not modify the reviewed roadmap or historical review reports.
- Added toolchain/CI/TTL defaults are implementer selections for review, not previously approved product requirements.
- Identity ADR remains `Conditional — pending M2 PoC`.
- No customer business endpoint, public Orchard setup, migration or real provisioning in M0.
- No fabricated NuGet hashes, SQL results, test counts or PASS for unavailable prerequisites.
- Local container lacks dotnet/pwsh and outbound downloads. Use GitHub Actions for compilation and execution; record actual run SHA and artifacts.

## Review focus
Missing/empty/skipped test suites; transitive dependency violations; wrong SQL database/engine; secrets in logs or Git; public setup before M1.

## Tasks
1. [ ] Skeleton and red architecture fixtures: create solution, central pins, test projects and closed host. Generate lockfiles with NuGet on an isolated branch, never by hand. Run the negative fixtures before implementing the checker.
2. [ ] Verification tooling: `DependencyRules.Validate(IEnumerable<DependencyNode>)`; load the actual evaluated dependency graph; TRX completeness checks; secret/config hygiene; valid and missing-prerequisite script tests. Record RED/GREEN commands.
3. [ ] SQL smoke and scripts: `check.ps1 -Scope M0|SqlSmoke|Prerequisites`; fail closed without SQL connection; verify exact target, engine and version with a real SQL query. Disposable SQL fixture has runtime CONNECT only; no persisted password.
4. [ ] ADRs, conventions, compatibility matrix and decision register cover every R01–R08, R2-01–R2-03 and R3-01–R3-03, including future owners/gates. No migration signatures are prematurely frozen.
5. [ ] CI locked restore/build/test/check and independent SQL job; inspect test counts, package graph and SQL evidence. Self-review (no subagent execution tool available), then open a PR. M0 remains BLOCKED if any mandatory evidence is missing.

## Verification commands
Use the eight commands in roadmap §7.1, plus `git diff --check`; CI artifacts must identify SHA, runner, SDK/pwsh, command exit codes, each required suite's discovered/executed/passed/failed/skipped counts, and SQL identity/version.

## Migration ownership
No product schema migrations in M0. Tenancy owns future platform registry/operations/audit and customer DB identity/write gate; AccessControl owns customer authorization; Auditing owns customer audit; Orders/Approvals own their business tables; Orchard alone owns its YesSql/OpenID schema.
