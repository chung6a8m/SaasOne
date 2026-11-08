# M6 local verification while CI is disabled

Status: implementation candidate. No runtime acceptance is claimed until the full evidence bundle is reviewed. CI remains unchanged/disabled. Do not merge or deploy this candidate based on source inspection alone.

## Run from a clean checkout

Use Windows x64 with Docker Desktop Linux containers, or Linux x64 with Docker. The repo pins **.NET SDK 10.0.403** and **PowerShell 7.6.9**. Docker must be running with Linux containers; the exact SQL Server image can be pre-pulled once, and subsequent local runs need no MCR connection when its registry digest is verified. NuGet restore requires the configured feeds. Full verification creates disposable SQL Server Developer instances; it does not use deployment databases or the caller's `SAAS_*`/`MSSQL_*` credentials.

```powershell
# From the repository root, on the M6 branch with all changes committed:
# Syntax-only preflight: no Docker, SQL Server, or .NET execution.
pwsh -NoProfile -File scripts/check-powershell-syntax.ps1
$LASTEXITCODE  # must be 0 before running either verification scope

pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
$LASTEXITCODE
```

The runner invokes inherited M0–M5 gates, checks M6's exact policy-case inventory, builds the release package, checks all six EF model snapshots, runs real external bundles and the real SQL/HTTP backup/restore cases, and validates mandatory TRX evidence. A missing, duplicate, unexpected, skipped or zero-test suite cannot pass. A clean checkout and the same commit SHA at start/end are mandatory.

`-Scope Components` runs the solution build/component gate without SQL fixtures. Its success is explicitly **PASS_COMPONENTS_NOT_ACCEPTANCE**, never M6 acceptance. It is useful for finding compilation issues before the full run.

Exit codes: `0` = selected scope passed; `1` = failed or invalid evidence; `2` = blocked prerequisite. A child gate may report BLOCKED inside a nonzero full-gate result; inspect `summary.json`, logs and individual reports rather than relying only on the exit code. Owner review remains PENDING even when all automated gates pass.

## EF bundle build preflight (before the long SQL suite)

All six migration streams carry `Microsoft.EntityFrameworkCore.Design` (private development dependency) and an `IDesignTimeDbContextFactory` in their **owning project**. The published `SaaS.Web` host does not need the Design package. For each `dotnet ef` command, `scripts/build-release.ps1` passes that stream's `project` as both `--project` and `--startup-project`. Using `SaaS.Web` as startup triggers the EF Tools missing-Design-package error.

To check all six model-drift commands, bundles and idempotent scripts **without rerunning inherited M0–M5 SQL tests**, run from a clean checkout:

```powershell
pwsh -NoProfile -File scripts/build-release.ps1
$LASTEXITCODE  # must be 0
```

This creates a separate candidate release package under `artifacts/releases` and is **not** M6 runtime acceptance. Only proceed to `scripts/local-verify.ps1 -Scope Full` once the standalone build succeeds; Full builds a fresh package tied to the same source SHA. Do not add `Microsoft.EntityFrameworkCore.Design` to the web deployment merely to work around an incorrect EF startup-project option.

**RID restore isolation:** EF bundle internally invokes `dotnet publish --runtime`, which can extend tracked `packages.lock.json` with a runtime-specific dependency section. Release generation now creates a detached Git worktree at `artifacts/release-worktrees/<unique-id>` from the exact trusted HEAD and performs all package/build/EF operations there. It validates that any changed lockfiles are strictly in the disposable worktree, under known `src/**/packages.lock.json` paths, and preserve the original dependency graph and pinned package versions/hashes. A new target section may only match the selected release RID (`net10.0/win-x64` or `net10.0/linux-x64`). Unexpected changes cause the build to fail; the worktree is removed, and removal failure also fails the gate. The source working tree remains subject to the same clean-checkout and SHA checks. This is not a waiver for dependency updates: use `scripts/update-locks.ps1` and a reviewed commit if the baseline graph legitimately changes.

Run the inexpensive lock-drift regression test first:

```powershell
pwsh -NoProfile -File scripts/test-m6-bundle-locks.ps1
$LASTEXITCODE
```

**Recovering from the previous failed build:** Check `git status --short` and `git diff -- src` before discarding anything. If, and only if, the six modified lockfiles are the generated changes from the failed M6 release build and contain no intentional edits, restore just these paths from HEAD:

```powershell
git restore -- src/Modules/SaaS.OrderApprovals/packages.lock.json src/Modules/SaaS.Orders/packages.lock.json src/Platform/SaaS.Abstractions/packages.lock.json src/Platform/SaaS.AccessControl/packages.lock.json src/Platform/SaaS.Auditing/packages.lock.json src/Platform/SaaS.Tenancy/packages.lock.json
git status --short
```

This is a one-time operator cleanup of the previous run, **not** an automated cleanup performed by verification. Never use `git reset --hard` to hide unreviewed source changes.

## SQL image preflight and intermittent registry errors

All M0–M6 SQL fixtures use the same tag `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04` and pin registry digest `sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`. Each fixture first queries Docker's local `RepoDigests`. If the exact pinned registry digest exists, it uses the cached image without calling `docker pull`; otherwise, it pulls the tag and verifies the resulting digest **before starting SQL**. A cached tag with a missing/wrong digest fails closed (`BLOCKED:sql_image_digest_mismatch`), not silently accepted. The SQL fixtures also verify SQL Server product version `16.0.4305.5` (M1–M6).

On Windows PowerShell, inspect the local metadata:

```powershell
docker image inspect mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04 --format '{{range .RepoDigests}}{{println .}}{{end}}'
```

Expected output includes `mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`. If the image is missing, use `docker pull mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04` once when MCR is reachable. If its digest differs, stop and investigate rather than retagging/relabeling it to bypass the check. MCR may intermittently return `EOF` even after a previous successful pull; an already-verified cached image avoids this repeated network dependency. The runner does not bypass verification based on a tag alone or on an unverified `docker load`.

## What to return

Each invocation creates a unique directory and ZIP:

```text
artifacts/local-verify/<UTC-run-id>/
  SUMMARY.md
  summary.json
  checksums.json
  m6-full.log                         # or components.log
  verification/*.json                # fresh command/gate reports only
  test-results/**/*.trx              # testcase identity/outcomes preserved
  release/release.json               # manifest and binary hashes, when build succeeded
  release/catalog.json
  release/sql/*.sql                  # reviewable idempotent SQL
artifacts/local-verify/<UTC-run-id>.zip
artifacts/local-verify/<UTC-run-id>.zip.sha256
```

Send the ZIP and its `.sha256` file for analysis, including failed/blocked runs. The ZIP is generated in `finally`; an inability to collect/hash/zip evidence is itself a failure. The console prints the exact paths. Compare the ZIP hash before upload with `Get-FileHash -Algorithm SHA256 <zip>`.

Actual host binaries and EF bundles remain under `artifacts/releases/`; their hashes are in `release.json`. They are deliberately not copied into the evidence ZIP. Never send `.bak` files, signing keys, connection settings, environment dumps, `App_Data`, raw Docker inspect output or MSBuild binary logs. The collector allows only the defined reports/TRX/release metadata and SQL; redacts known caller configuration values, common secret fields and token patterns; preserves test names/outcomes. **Inspect logs before sharing:** redaction is defense in depth, not a guarantee against arbitrary newly introduced logging.

## Isolation, cleanup and repeatability

A repository-local exclusive lock prevents two local-verify sessions sharing evidence directories. Existing `artifacts/verification` and `artifacts/test-results` move to `artifacts/local-archive/<run-id>/` before execution; they are not accepted as new evidence or uploaded. Run standalone gate scripts separately from this runner, not concurrently. The runner does not modify tracked files or silently commit changes.

M6 SQL fixtures use SQL Server `2022-CU27-GDR1-ubuntu-22.04`, record its image digest, and verify product version `16.0.4305.5`. Each container has a random name, generated disposable credentials and a loopback-only dynamic port. A private temporary bind mount gives SQL and the test process access to the same backup files. The M6 fixture runs as root **inside its disposable container only** to handle bind-mount ownership; this is not production guidance. Docker Desktop must permit sharing the host temporary directory. Avoid a temporary directory containing a comma because Docker's mount argument uses commas as separators.

Cleanup removes only the fixture's uniquely named container and temporary backup directory. A failed cleanup makes the gate fail. If Docker itself becomes unavailable, inspect `cleanupExitCode` and remove the recorded `saas-m6-*` fixture by its exact name; do not prune unrelated containers or volumes. After cleanup, only sanitized manifest metadata/hashes remain in evidence. Test backups and their random signing key are intentionally unrecoverable after the run.

## Reading failures

The M0 `dotnet test` invocation runs multiple test assemblies into the same results directory. VSTest can create collision-suffixed files such as `net10.0[1].trx`. M4/M5 policy inventory reads these with `-LiteralPath` and **must include every TRX**; it must never drop a file merely to avoid wildcard parsing. A fast, database-free regression check is:

```powershell
pwsh -NoProfile -File scripts/test-m3-evidence.ps1
$LASTEXITCODE  # must be 0
```

This exercises both ordinary and bracketed TRX filenames before rerunning `-Scope Full`. The full run still regenerates M0–M6 evidence bound to its source SHA.

Start with `SUMMARY.md`, then `summary.json.commands` and `issues`. Build errors precede SQL cases; absent SQL TRX after a build failure means **not run**, not passed. `m6-release-build.json` identifies the last complete migration stream; `m6-poc.json` has exact mandatory test discovery and outcomes; `m6-acceptance.json` binds inherited gate reports to the source SHA. The test names remain readable in TRX even when exception messages have been redacted.

The implementation environment used to author this change has no dotnet, pwsh, Docker or SQL Server. No fabricated TRX, package, schema-drift PASS or acceptance PASS is checked in. See `docs/verification/m6-implementation-status.md` for the remaining acceptance work and review boundary.
