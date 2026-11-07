[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$commands = [System.Collections.Generic.List[object]]::new()
$report = [ordered]@{ scope = 'M3 full acceptance'; status = 'FAIL'; sourceSha = $null; commands = $commands; evidence = @() }
$code = 1
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item artifacts/verification -ItemType Directory -Force | Out-Null
    Remove-Item artifacts/verification/m3-poc.json, artifacts/verification/m3-acceptance.json -Force -ErrorAction SilentlyContinue
    Remove-Item artifacts/test-results/m3-poc -Recurse -Force -ErrorAction SilentlyContinue
    $report.sourceSha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $report.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record source identity.' }
    & pwsh -NoProfile -File scripts/check-m2.ps1
    $commands.Add([ordered]@{ command = 'pwsh -NoProfile -File scripts/check-m2.ps1'; exitCode = $LASTEXITCODE })
    if ($LASTEXITCODE -ne 0) { throw 'Inherited M0/M1/M2 gates failed.' }
    foreach ($stream in @(@('SaaS.AccessControl', 'AccessDbContext'), @('SaaS.Auditing', 'AuditDbContext'))) {
        & dotnet ef migrations has-pending-model-changes --project "src/Platform/$($stream[0])" --context $stream[1] --configuration Release --no-build
        $commands.Add([ordered]@{ command = "dotnet ef migrations has-pending-model-changes --project src/Platform/$($stream[0]) --context $($stream[1]) --configuration Release --no-build"; exitCode = $LASTEXITCODE })
        if ($LASTEXITCODE -ne 0) { throw "Snapshot drift: $($stream[1])" }
    }
    & pwsh -NoProfile -File scripts/m3-sql-fixture.ps1
    $commands.Add([ordered]@{ command = 'pwsh -NoProfile -File scripts/m3-sql-fixture.ps1'; exitCode = $LASTEXITCODE })
    if ($LASTEXITCODE -ne 0) { throw 'M3 SQL/HTTP/operations acceptance failed.' }
    $report.evidence = @('m0.json', 'sql-command.json', 'sql-fixture.json', 'm1-sql.json', 'm2-poc.json', 'm3-poc.json')
    foreach ($name in $report.evidence) {
        $evidence = Get-Content (Join-Path 'artifacts/verification' $name) -Raw | ConvertFrom-Json
        if ($evidence.status -cne 'PASS' -or $evidence.sourceSha -cne $report.sourceSha) { throw "Missing or stale prerequisite evidence: $name" }
    }
    $report.status = 'PASS'; $code = 0
    Write-Host 'PASS: M3 full acceptance; architecture/security adoption and merge remain owner decisions.'
} catch { Write-Host ('FAIL: ' + $_.Exception.Message) }
finally {
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    try { $report | ConvertTo-Json -Depth 10 | Set-Content artifacts/verification/m3-acceptance.json -Encoding utf8 }
    catch { $code = 1 }
    Pop-Location
}
exit $code
