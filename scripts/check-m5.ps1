[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$commands = [System.Collections.Generic.List[object]]::new()
$report = [ordered]@{ scope = 'M5 full acceptance'; status = 'FAIL'; sourceSha = $null; commands = $commands; evidence = @(); policyCases = @() }
$code = 1
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item artifacts/verification -ItemType Directory -Force | Out-Null
    Remove-Item artifacts/verification/m5-poc.json, artifacts/verification/m5-acceptance.json -Force -ErrorAction SilentlyContinue
    Remove-Item artifacts/test-results/m5-poc -Recurse -Force -ErrorAction SilentlyContinue
    $report.sourceSha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $report.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record source identity.' }
    & pwsh -NoProfile -File scripts/check-m4.ps1
    $commands.Add([ordered]@{ command = 'pwsh -NoProfile -File scripts/check-m4.ps1'; exitCode = $LASTEXITCODE })
    if ($LASTEXITCODE -ne 0) { throw 'Inherited M0/M1/M2/M3/M4 gates failed.' }
    . (Join-Path $PSScriptRoot 'm3-evidence-inventory.ps1')
    $required = @(Get-Content scripts/m5-policy-mandatory-cases.json -Raw | ConvertFrom-Json)
    $policy = @(Get-M3PolicyTrxResults -ResultsDirectory 'artifacts/test-results/m0' -TestNamePattern '^SaaS\.UnitTests\.M5')
    $inventory = Get-M3EvidenceInventory -Results $policy -Required $required
    $report.policyCases = $inventory.mandatoryCases
    if (-not $inventory.valid) { throw 'Every mandatory M5 policy/model case must execute exactly once and pass.' }
    & dotnet ef migrations has-pending-model-changes --project src/Modules/SaaS.OrderApprovals --context ApprovalsDbContext --configuration Release --no-build
    $commands.Add([ordered]@{ command = 'dotnet ef migrations has-pending-model-changes --project src/Modules/SaaS.OrderApprovals --context ApprovalsDbContext --configuration Release --no-build'; exitCode = $LASTEXITCODE })
    if ($LASTEXITCODE -ne 0) { throw 'Snapshot drift: ApprovalsDbContext' }
    & pwsh -NoProfile -File scripts/m5-sql-fixture.ps1
    $commands.Add([ordered]@{ command = 'pwsh -NoProfile -File scripts/m5-sql-fixture.ps1'; exitCode = $LASTEXITCODE })
    if ($LASTEXITCODE -ne 0) { throw 'M5 SQL/native HTTP acceptance failed.' }
    $report.evidence = @('m0.json', 'sql-command.json', 'sql-fixture.json', 'm1-sql.json', 'm2-poc.json', 'm3-poc.json', 'm3-acceptance.json', 'm4-poc.json', 'm4-acceptance.json', 'm5-poc.json')
    foreach ($name in $report.evidence) {
        $evidence = Get-Content (Join-Path 'artifacts/verification' $name) -Raw | ConvertFrom-Json
        if ($evidence.status -cne 'PASS' -or $evidence.sourceSha -cne $report.sourceSha) { throw "Missing or stale prerequisite evidence: $name" }
    }
    $report.status = 'PASS'; $code = 0
    Write-Host 'PASS: M5 full acceptance; product choices, architecture adoption and merge remain owner decisions.'
} catch { Write-Host ('FAIL: ' + $_.Exception.Message) }
finally {
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    try { $report | ConvertTo-Json -Depth 10 | Set-Content artifacts/verification/m5-acceptance.json -Encoding utf8 }
    catch { $code = 1 }
    Pop-Location
}
exit $code
