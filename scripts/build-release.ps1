[CmdletBinding()]
param([ValidateSet('linux-x64', 'win-x64')][string]$RuntimeIdentifier = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }), [string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/m6-common.ps1"
$root = Split-Path $PSScriptRoot -Parent
$report = [ordered]@{ scope = 'M6ReleaseBuild'; status = 'FAIL'; sourceSha = $null; commands = @(); package = $null; manifestSha256 = $null }
$previousExecution = $env:SAAS_BUNDLE_EXECUTION
$previousConnection = $env:SAAS_MIGRATION_CONNECTION
$proof = $null; $code = 1
Push-Location $root
try {
    $sha = Get-M6Sha; $report.sourceSha = $sha
    $dirty = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'release_requires_clean_checkout' }
    $env:SAAS_BUNDLE_EXECUTION = $null; $env:SAAS_MIGRATION_CONNECTION = $null
    if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $root ('artifacts/releases/' + $sha.Substring(0,12) + '-' + [guid]::NewGuid().ToString('N')) }
    $outputFull = [IO.Path]::GetFullPath($Output)
    if (Test-Path -LiteralPath $outputFull) { throw 'release_output_must_not_exist' }
    New-Item $outputFull -ItemType Directory | Out-Null
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($outputFull, [IO.UnixFileMode]'UserRead,UserWrite,UserExecute') }
    foreach ($directory in @('host','bundles','sql')) { New-Item (Join-Path $outputFull $directory) -ItemType Directory | Out-Null }
    $report.package = $outputFull
    Invoke-M6Checked dotnet @('tool','restore')
    Invoke-M6Checked dotnet @('restore','SaasOne.slnx','--locked-mode')
    Invoke-M6Checked dotnet @('build','SaasOne.slnx','--configuration','Release','--no-restore')
    Invoke-M6Checked dotnet @('publish','src/SaaS.Web/SaaS.Web.csproj','--configuration','Release','--no-build','--no-restore','--output',(Join-Path $outputFull 'host'),'-p:UseAppHost=false')
    $catalogPath = Join-Path $outputFull 'catalog.json'
    Invoke-M6Checked dotnet @((Join-Path $outputFull 'host/SaaS.Web.dll'),'--maintenance','release-catalog',$RuntimeIdentifier,$catalogPath)
    $catalog = @(Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -Depth 20)
    if ($catalog.Count -ne 6) { throw 'release_context_inventory_mismatch' }
    foreach ($entry in $catalog) {
        $common = @('--project',$entry.project,'--startup-project','src/SaaS.Web','--context',$entry.stream.context,'--configuration','Release','--no-build')
        Invoke-M6Checked dotnet (@('ef','migrations','has-pending-model-changes') + $common)
        Invoke-M6Checked dotnet (@('ef','migrations','bundle') + $common + @('--target-runtime',$RuntimeIdentifier,'--output',(Join-Path $outputFull $entry.stream.bundle)))
        Invoke-M6Checked dotnet (@('ef','migrations','script','--idempotent') + $common + @('--output',(Join-Path $outputFull $entry.stream.sql)))
        $report.commands += [ordered]@{ context = $entry.stream.context; snapshotDrift = 'PASS'; bundle = 'PASS'; idempotentSql = 'PASS' }
    }
    $files = @(Get-ChildItem -LiteralPath $outputFull -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($outputFull,$_.FullName).Replace('\','/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $manifest = [ordered]@{ version = 1; sourceSha = $sha; runtimeIdentifier = $RuntimeIdentifier; files = $files; streams = @($catalog | ForEach-Object { $_.stream }) }
    Write-M6Json (Join-Path $outputFull 'release.json') $manifest
    $digest = (Get-FileHash -LiteralPath (Join-Path $outputFull 'release.json') -Algorithm SHA256).Hash
    # The sidecar is OUTSIDE the closed package; deployment must obtain its digest via a trusted channel.
    [IO.File]::WriteAllText($outputFull + '.sha256', $digest + "`n", [Text.UTF8Encoding]::new($false))
    $proof = Open-M6Package $outputFull $digest
    $previousTrust = $env:SAAS_RELEASE_MANIFEST_SHA256
    try {
        $env:SAAS_RELEASE_MANIFEST_SHA256 = $digest
        Invoke-M6Checked dotnet @((Join-Path $outputFull 'host/SaaS.Web.dll'),'--maintenance','release-check',$outputFull,($outputFull + '.validated.json'))
    } finally { $env:SAAS_RELEASE_MANIFEST_SHA256 = $previousTrust }
    $after = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $after.Count -ne 0 -or (Get-M6Sha) -cne $sha) { throw 'source_changed_during_release_build' }
    $report.manifestSha256 = $digest; $report.status = 'PASS'; $code = 0
    Write-Host "Release package: $outputFull"
    Write-Host "Manifest SHA256: $digest"
} catch { $report.error = Protect-M6Text $_.Exception.Message; Write-Host $report.error }
finally {
    Close-M6Package $proof
    $env:SAAS_BUNDLE_EXECUTION = $previousExecution; $env:SAAS_MIGRATION_CONNECTION = $previousConnection
    $report.exitCode = $code
    Write-M6Json (Join-Path $root 'artifacts/verification/m6-release-build.json') $report
    Pop-Location
}
exit $code
