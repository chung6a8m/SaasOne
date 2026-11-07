[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & pwsh -NoProfile -File scripts/check-m2.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Inherited M0/M1/M2 gates failed.' }
    foreach ($stream in @(@('SaaS.AccessControl', 'AccessDbContext'), @('SaaS.Auditing', 'AuditDbContext'))) {
        & dotnet ef migrations has-pending-model-changes --project "src/Platform/$($stream[0])" --context $stream[1] --configuration Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Snapshot drift: $($stream[1])" }
    }
    & pwsh -NoProfile -File scripts/m3-sql-fixture.ps1
    if ($LASTEXITCODE -ne 0) { throw 'M3 SQL gate failed.' }
    throw 'M3 lifecycle/recovery/HTTP acceptance is not yet installed; component success is not completion.'
} catch { Write-Host ('FAIL: ' + $_.Exception.Message); exit 1 }
finally { Pop-Location }
