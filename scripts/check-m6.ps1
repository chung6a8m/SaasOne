[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/m6-common.ps1"
. "$PSScriptRoot/m3-evidence-inventory.ps1"
$root = Split-Path $PSScriptRoot -Parent
$report = [ordered]@{ scope = 'M6 automated acceptance gate'; status = 'FAIL'; sourceSha = $null; ownerReview = 'PENDING'; mandatoryPolicyCases = @(); inherited = @() }
$code = 1
Push-Location $root
try {
    $report.sourceSha = Get-M6Sha
    $dirty = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'full_verification_requires_clean_checkout' }
    Invoke-M6Checked pwsh @('-NoProfile','-File',"$PSScriptRoot/test-m6-evidence.ps1")
    # Do not weaken or replace any of the preceding milestone gates.
    Invoke-M6Checked pwsh @('-NoProfile','-File',"$PSScriptRoot/check-m5.ps1")
    $policyRequired = @(Get-Content "$PSScriptRoot/m6-policy-cases.json" -Raw | ConvertFrom-Json)
    $policyResults = @(Get-ChildItem artifacts/test-results/m0 -Filter *.trx -Recurse | ForEach-Object {
        $evidence = Read-M6Trx $_.FullName
        $evidence.results | Where-Object testName -CLike 'SaaS.UnitTests.M6*'
    })
    $policy = Get-M3EvidenceInventory -Results $policyResults -Required $policyRequired
    $report.mandatoryPolicyCases = $policy.mandatoryCases
    if (-not $policy.valid) { throw 'm6_policy_inventory_failed' }
    Invoke-M6Checked pwsh @('-NoProfile','-File',"$PSScriptRoot/build-release.ps1")
    $release = Get-Content artifacts/verification/m6-release-build.json -Raw | ConvertFrom-Json -Depth 20
    if ($release.status -cne 'PASS' -or $release.sourceSha -cne $report.sourceSha) { throw 'release_build_evidence_invalid' }
    Invoke-M6Checked pwsh @('-NoProfile','-File',"$PSScriptRoot/m6-sql-fixture.ps1",'-Package',$release.package,'-ManifestSha256',$release.manifestSha256)
    foreach ($name in @('m5-acceptance.json','m6-evidence-self-tests.json','m6-release-build.json','m6-poc.json')) {
        $evidence = Get-Content (Join-Path artifacts/verification $name) -Raw | ConvertFrom-Json -Depth 32
        if ($evidence.status -cne 'PASS' -or $evidence.sourceSha -cne $report.sourceSha) { throw "mandatory_report_invalid:$name" }
        $report.inherited += [ordered]@{file=$name;status=$evidence.status;sourceSha=$evidence.sourceSha}
    }
    $dirty = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0 -or (Get-M6Sha) -cne $report.sourceSha) { throw 'source_changed_during_verification' }
    $report.status = 'PASS'; $code = 0
} catch { $report.error = Protect-M6Text $_.Exception.Message; Write-Host $report.error }
finally {
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    Write-M6Json (Join-Path $root 'artifacts/verification/m6-acceptance.json') $report
    Pop-Location
}
exit $code
