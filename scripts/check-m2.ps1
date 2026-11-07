[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Clear prior milestone outputs before any prerequisite can fail.
    Remove-Item artifacts/verification/m2-poc.json -Force -ErrorAction SilentlyContinue
    Remove-Item artifacts/test-results/m2-poc -Recurse -Force -ErrorAction SilentlyContinue
    & pwsh -NoProfile -File scripts/check.ps1 -Scope M0
    if ($LASTEXITCODE -ne 0) { throw 'Foundation regression gate failed.' }
    foreach ($context in @('PlatformDbContext', 'TenantDbContext')) {
        & dotnet ef migrations has-pending-model-changes --project src/Platform/SaaS.Tenancy --context $context --configuration Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Snapshot/model mismatch: $context" }
    }
    & pwsh -NoProfile -File scripts/sql-fixture.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Inherited SQL smoke gate failed.' }
    & pwsh -NoProfile -File scripts/m1-sql-fixture.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Inherited M1 SQL/Orchard gate failed.' }
    & pwsh -NoProfile -File scripts/m2-sql-fixture.ps1
    if ($LASTEXITCODE -ne 0) { throw 'M2 cross-shell identity PoC gate failed or is incomplete.' }
} catch { Write-Host ('FAIL: ' + $_.Exception.Message); exit 1 }
finally { Pop-Location }
exit 0
