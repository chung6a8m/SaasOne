[CmdletBinding()]
param([ValidateSet('Setup', 'Run', 'Check')][string]$Action = 'Setup')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & pwsh -NoProfile -File scripts/check.ps1 -Scope Prerequisites
    if ($LASTEXITCODE -ne 0) { throw 'Prerequisites failed.' }
    switch ($Action) {
        'Setup' {
            & dotnet tool restore
            if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
            & dotnet restore SaasOne.slnx --locked-mode
            if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
        }
        'Run' {
            & dotnet run --project src/SaaS.Web --no-restore --urls http://127.0.0.1:5080
            if ($LASTEXITCODE -ne 0) { throw 'Host failed.' }
        }
        'Check' {
            & pwsh -NoProfile -File scripts/check.ps1 -Scope M0
            if ($LASTEXITCODE -ne 0) { throw 'M0 check failed.' }
        }
    }
} catch { Write-Error $_; exit 1 }
finally { Pop-Location }
