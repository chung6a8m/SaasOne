[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$homePath = Join-Path ([IO.Path]::GetTempPath()) ('saas-m7-boundary-' + [guid]::NewGuid().ToString('N'))
$previous = @{}
foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' })) { $previous[$item.Name] = $item.Value }
$code = 1
try {
    $scripts = Join-Path $homePath 'scripts'
    New-Item $scripts -ItemType Directory -Force | Out-Null
    foreach ($name in @('m7-sql-fixture.ps1', 'm6-common.ps1', 'm3-evidence-inventory.ps1', 'sql-image.ps1', 'm7-workload-mandatory-cases.json')) {
        Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $scripts $name)
    }
    $stale = Join-Path $homePath 'artifacts/test-results/m7-workload'
    New-Item $stale -ItemType Directory -Force | Out-Null
    '{"sourceSha":"stale"}' | Set-Content "$stale/load.json"
    'stale-log-marker' | Set-Content "$stale/runner.log"
    $env:SAAS_M7_BOUNDARY_SENTINEL = 'preserve-on-prerequisite-failure'
    $env:MSSQL_M7_BOUNDARY_SENTINEL = 'preserve-on-prerequisite-failure'
    & "$scripts/m7-sql-fixture.ps1" -Suite Workload -Mode Baseline
    if ($LASTEXITCODE -eq 0) { throw 'A directory without Git source must fail before Docker.' }
    foreach ($name in @('SAAS_M7_BOUNDARY_SENTINEL', 'MSSQL_M7_BOUNDARY_SENTINEL')) {
        if ([Environment]::GetEnvironmentVariable($name) -cne 'preserve-on-prerequisite-failure') { throw 'Prerequisite failure must preserve caller environment.' }
    }
    if ((Test-Path "$homePath/artifacts/m7-evidence/load.json") -or (Test-Path "$homePath/artifacts/m7-evidence/workload.log")) {
        throw 'Prerequisite failure must not export previous-run evidence.'
    }
    $report = Get-Content "$homePath/artifacts/m7-evidence/workload.json" -Raw | ConvertFrom-Json
    if ($report.status -eq 'PASS' -or $report.exitCode -eq 0 -or $report.testExitCode -ne $null) { throw 'Unexecuted SQL suite must not report success.' }
    Write-Host 'PASS: prerequisite failure preserves environment, rejects stale evidence and cannot report unexecuted SQL as success.'
    $code = 0
} catch { Write-Host $_.Exception.Message }
finally {
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' })) { [Environment]::SetEnvironmentVariable($item.Name, $null) }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    if (Test-Path $homePath) { Remove-Item $homePath -Recurse -Force }
}
exit $code
