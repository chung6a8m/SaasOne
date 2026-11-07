# M6 local verification while CI is disabled

Status: implementation candidate. No runtime acceptance is claimed until the full evidence bundle is reviewed. CI remains unchanged/disabled. Do not merge or deploy this candidate based on source inspection alone.

## Run from a clean checkout

Use Windows x64 with Docker Desktop Linux containers, or Linux x64 with Docker. The repo pins **.NET SDK 10.0.403** and **PowerShell 7.6.9**. Docker must be running and able to pull the pinned SQL Server image. NuGet restore requires the configured feeds. Full verification creates disposable SQL Server Developer instances; it does not use deployment databases or the caller's `SAAS_*`/`MSSQL_*` credentials.

```powershell
# From the repository root, on the M6 branch with all changes committed:
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
$LASTEXITCODE
```

The runner invokes inherited M0–M5 gates, checks M6's exact policy-case inventory, builds the release package, checks all six EF model snapshots, runs real external bundles and the real SQL/HTTP backup/restore cases, and validates mandatory TRX evidence. A missing, duplicate, unexpected, skipped or zero-test suite cannot pass. A clean checkout and the same commit SHA at start/end are mandatory.

`-Scope Components` runs the solution build/component gate without SQL fixtures. Its success is explicitly **PASS_COMPONENTS_NOT_ACCEPTANCE**, never M6 acceptance. It is useful for finding compilation issues before the full run.

Exit codes: `0` = selected scope passed; `1` = failed or invalid evidence; `2` = blocked prerequisite. A child gate may report BLOCKED inside a nonzero full-gate result; inspect `summary.json`, logs and individual reports rather than relying only on the exit code. Owner review remains PENDING even when all automated gates pass.

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

Start with `SUMMARY.md`, then `summary.json.commands` and `issues`. Build errors precede SQL cases; absent SQL TRX after a build failure means **not run**, not passed. `m6-release-build.json` identifies the last complete migration stream; `m6-poc.json` has exact mandatory test discovery and outcomes; `m6-acceptance.json` binds inherited gate reports to the source SHA. The test names remain readable in TRX even when exception messages have been redacted.

The implementation environment used to author this change has no dotnet, pwsh, Docker or SQL Server. No fabricated TRX, package, schema-drift PASS or acceptance PASS is checked in. See `docs/verification/m6-implementation-status.md` for the remaining acceptance work and review boundary.
