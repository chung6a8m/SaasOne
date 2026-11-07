[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/m6-common.ps1"
. "$PSScriptRoot/m3-evidence-inventory.ps1"
$root = Split-Path $PSScriptRoot -Parent
$report = [ordered]@{ scope = 'M6 evidence negative self-tests'; status = 'FAIL'; sourceSha = $null; cases = @() }
$code = 1
function Assert-Case([string]$Name, [bool]$Passed) {
    $report.cases += [ordered]@{ name = $Name; passed = $Passed }
    if (-not $Passed) { throw "evidence_self_test_failed:$Name" }
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('saas-evidence-' + [guid]::NewGuid().ToString('N'))
Push-Location $root
try {
    $report.sourceSha = Get-M6Sha
    $a = [pscustomobject]@{ testName = 'A'; outcome = 'Passed' }; $b = [pscustomobject]@{ testName = 'B'; outcome = 'Passed' }
    Assert-Case 'complete_pass' (Get-M3EvidenceInventory @($a,$b) @('A','B')).valid
    Assert-Case 'missing_fails' (-not (Get-M3EvidenceInventory @($a) @('A','B')).valid)
    Assert-Case 'duplicate_fails' (-not (Get-M3EvidenceInventory @($a,$a,$b) @('A','B')).valid)
    Assert-Case 'zero_fails' (-not (Get-M3EvidenceInventory @() @('A','B')).valid)
    Assert-Case 'skipped_fails' (-not (Get-M3EvidenceInventory @($a,[pscustomobject]@{testName='B';outcome='NotExecuted'}) @('A','B')).valid)
    Assert-Case 'unexpected_fails' (-not (Get-M3EvidenceInventory @($a,$b,[pscustomobject]@{testName='C';outcome='Passed'}) @('A','B')).valid)
    New-Item (Join-Path $temp 'host') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'host/SaaS.Web.dll'),'test fixture, not executable')
    $file = Get-Item (Join-Path $temp 'host/SaaS.Web.dll')
    $manifest = [ordered]@{version=1;sourceSha=('a'*40);runtimeIdentifier='linux-x64';files=@([ordered]@{path='host/SaaS.Web.dll';length=$file.Length;sha256=(Get-FileHash $file.FullName).Hash});streams=@()}
    Write-M6Json (Join-Path $temp 'release.json') $manifest
    $digest = (Get-FileHash (Join-Path $temp 'release.json')).Hash
    $proof = Open-M6Package $temp $digest
    try { Assert-Case 'pre_execution_package_verified' ($proof.digest -eq $digest) } finally { Close-M6Package $proof }
    [IO.File]::WriteAllText($file.FullName,'tampered')
    $rejected = $false
    try { $proof = Open-M6Package $temp $digest; Close-M6Package $proof } catch { $rejected = $true }
    Assert-Case 'tampered_host_rejected_before_execution' $rejected
    $rejected = $false
    try { Resolve-M6Artifact $temp '../outside' | Out-Null } catch { $rejected = $true }
    Assert-Case 'path_traversal_rejected' $rejected
    $secret = [guid]::NewGuid().ToString('N')
    Assert-Case 'connection_secret_redacted' (-not (Protect-M6Text (('Pass'+'word=')+$secret+';')).Contains($secret))
    $report.status = 'PASS'; $code = 0
} catch { $report.error = Protect-M6Text $_.Exception.Message; Write-Host $report.error }
finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
    $report.exitCode = $code
    Write-M6Json (Join-Path $root 'artifacts/verification/m6-evidence-self-tests.json') $report
    Pop-Location
}
exit $code
