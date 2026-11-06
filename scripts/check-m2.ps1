[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & pwsh -NoProfile -File scripts/check.ps1 -Scope M0
    if ($LASTEXITCODE -ne 0) { throw 'Foundation regression gate failed.' }
    & pwsh -NoProfile -File scripts/m2-sql-fixture.ps1
    if ($LASTEXITCODE -ne 0) { throw 'M2 cross-shell identity PoC gate failed or is incomplete.' }
} catch { Write-Error $_; exit 1 }
finally { Pop-Location }
exit 0
