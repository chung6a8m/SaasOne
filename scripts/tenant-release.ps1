[CmdletBinding()]
param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$ManifestSha256,
      [Parameter(Mandatory)][string]$Requests, [switch]$Resume, [string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/m6-common.ps1"
$proof = $null; $previousTrust = $env:SAAS_RELEASE_MANIFEST_SHA256; $code = 1
$report = [ordered]@{scope='Tenant release batch';status='FAIL';manifestSha256=$ManifestSha256;sourceSha=$null;tenants=@()}
try {
    $proof = Open-M6Package $Package $ManifestSha256
    $report.sourceSha = $proof.manifest.sourceSha
    $items = @(Get-Content -LiteralPath $Requests -Raw | ConvertFrom-Json -Depth 10)
    if ($items.Count -lt 1) { throw 'release_requests_empty' }
    $tenants = [Collections.Generic.HashSet[guid]]::new(); $operations = [Collections.Generic.HashSet[guid]]::new()
    foreach ($item in $items) {
        $tenant = [guid]::ParseExact([string]$item.tenantId,'D'); $operation = [guid]::ParseExact([string]$item.operationId,'D')
        if ($tenant -eq [guid]::Empty -or $operation -eq [guid]::Empty -or -not $tenants.Add($tenant) -or -not $operations.Add($operation) -or
            [string]$item.expectedRegistryVersion -notmatch '^[1-9][0-9]*$') { throw 'release_request_invalid' }
    }
    if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/release-execution/' + [guid]::NewGuid().ToString('N')) }
    $Output = [IO.Path]::GetFullPath($Output)
    if (Test-Path $Output) { throw 'release_report_output_must_not_exist' }
    New-Item $Output -ItemType Directory -Force | Out-Null
    $env:SAAS_RELEASE_MANIFEST_SHA256 = $ManifestSha256
    $hostDll = Join-Path $proof.root 'host/SaaS.Web.dll'
    Invoke-M6Checked dotnet @($hostDll,'--maintenance','release-check',$proof.root,(Join-Path $Output 'package-check.json'))
    $action = if ($Resume) { 'release-resume' } else { 'release-apply' }
    $code = 0
    foreach ($item in $items) {
        $resultPath = Join-Path $Output ($item.tenantId + '.json')
        $log = Join-Path $Output ($item.tenantId + '.log')
        $start = [DateTimeOffset]::UtcNow
        & dotnet $hostDll --maintenance $action $proof.root $item.tenantId $item.operationId ([string]$item.expectedRegistryVersion) $resultPath 2>&1 |
            ForEach-Object { Protect-M6Text $_.ToString() | Tee-Object -FilePath $log -Append | Out-Host }
        $exitCode = $LASTEXITCODE
        $report.tenants += [ordered]@{ tenantId=$item.tenantId; operationId=$item.operationId; expectedRegistryVersion=$item.expectedRegistryVersion;
            exitCode=$exitCode; durationSeconds=([DateTimeOffset]::UtcNow-$start).TotalSeconds; resultFile=[IO.Path]::GetFileName($resultPath) }
        if ($exitCode -ne 0 -or -not (Test-Path $resultPath)) { $code = 1 }
        # A failed tenant remains gated; independent tenant entries still run. Never reload/retry a CAS conflict here.
    }
    if ($code -eq 0) { $report.status = 'PASS' }
} catch { $report.error = Protect-M6Text $_.Exception.Message; Write-Host $report.error }
finally {
    Close-M6Package $proof; $env:SAAS_RELEASE_MANIFEST_SHA256 = $previousTrust
    if (-not [string]::IsNullOrWhiteSpace($Output) -and (Test-Path $Output)) { $report.exitCode=$code; Write-M6Json (Join-Path $Output 'batch.json') $report }
}
exit $code
