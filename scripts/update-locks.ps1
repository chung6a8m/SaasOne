# Explicit maintenance only. Review the complete lock/inventory diff before committing.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & pwsh -NoProfile -File scripts/check.ps1 -Scope Prerequisites
    if ($LASTEXITCODE -ne 0) { throw 'Prerequisites failed.' }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    & dotnet restore SaasOne.slnx --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore/audit failed.' }
    & dotnet build tools/SaaS.Build --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Inventory tool build failed.' }
    & dotnet tools/SaaS.Build/bin/Release/net10.0/SaaS.Build.dll locks-write (Get-Location).Path
    if ($LASTEXITCODE -ne 0) { throw 'Inventory generation failed.' }
    Write-Host 'Review package/license/security changes, then rerun M0 and SQL gates.'
} catch { Write-Error $_; exit 1 }
finally { Pop-Location }
