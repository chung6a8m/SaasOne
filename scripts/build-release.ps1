[CmdletBinding()]
param([ValidateSet('linux-x64', 'win-x64')][string]$RuntimeIdentifier = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }), [string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/m6-common.ps1"
. "$PSScriptRoot/m6-bundle-locks.ps1"
$root = Split-Path $PSScriptRoot -Parent
$report = [ordered]@{ scope = 'M6ReleaseBuild'; status = 'FAIL'; sourceSha = $null; commands = @(); package = $null; manifestSha256 = $null; ridLockChanges = @(); worktreeCleanup = 'NOT_RUN' }
$previousExecution = $env:SAAS_BUNDLE_EXECUTION
$previousConnection = $env:SAAS_MIGRATION_CONNECTION
$proof = $null; $code = 1; $worktree = $null; $worktreeAdded = $false
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

    # EF bundle calls dotnet publish --runtime on the owner's project graph. NuGet
    # can extend packages.lock.json for this RID even after a locked-mode solution
    # restore. Run *all* build commands in a detached, disposable copy of HEAD so
    # the trusted checkout is never mutated. Validate its RID lock delta below.
    $worktree = Join-Path $root ('artifacts/release-worktrees/' + [guid]::NewGuid().ToString('N'))
    & git -C $root worktree add --detach $worktree $sha | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'release_worktree_create_failed' }
    $worktreeAdded = $true
    Push-Location $worktree
    try {
    Invoke-M6Checked dotnet @('tool','restore')
    Invoke-M6Checked dotnet @('restore','SaasOne.slnx','--locked-mode')
    Invoke-M6Checked dotnet @('build','SaasOne.slnx','--configuration','Release','--no-restore')
    Invoke-M6Checked dotnet @('publish','src/SaaS.Web/SaaS.Web.csproj','--configuration','Release','--no-build','--no-restore','--output',(Join-Path $outputFull 'host'),'-p:UseAppHost=false')
    $catalogPath = Join-Path $outputFull 'catalog.json'
    Invoke-M6Checked dotnet @((Join-Path $outputFull 'host/SaaS.Web.dll'),'--maintenance','release-catalog',$RuntimeIdentifier,$catalogPath)
    $catalog = @(Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -Depth 20)
    if ($catalog.Count -ne 6) { throw 'release_context_inventory_mismatch' }
    foreach ($entry in $catalog) {
        # The owning migrations project contains the Design package and a no-network design-time factory.
        # SaaS.Web is the deployable host, not an EF tooling startup project: its Design assets are private in the owners.
        $common = @('--project',$entry.project,'--startup-project',$entry.project,'--context',$entry.stream.context,'--configuration','Release','--no-build')
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
    $report.ridLockChanges = @(Assert-M6BundleWorktreeChanges -Worktree $worktree -RuntimeIdentifier $RuntimeIdentifier)
    } finally { Pop-Location }
    $after = @(& git -C $root status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $after.Count -ne 0 -or (Get-M6Sha) -cne $sha) { throw 'source_changed_during_release_build' }
    $report.manifestSha256 = $digest; $report.status = 'PASS'; $code = 0
    Write-Host "Release package: $outputFull"
    Write-Host "Manifest SHA256: $digest"
} catch { $report.error = Protect-M6Text $_.Exception.Message; Write-Host $report.error }
finally {
    Close-M6Package $proof
    if ($worktreeAdded) {
        # Only this newly created detached checkout can be force-removed.
        & git -C $root worktree remove --force $worktree | Out-Host
        if ($LASTEXITCODE -ne 0) {
            $report.worktreeCleanup = 'FAIL'; $report.status = 'FAIL'; $report.error = 'release_worktree_cleanup_failed'; $code = 1
        } else { $report.worktreeCleanup = 'PASS' }
    }
    $env:SAAS_BUNDLE_EXECUTION = $previousExecution; $env:SAAS_MIGRATION_CONNECTION = $previousConnection
    $report.exitCode = $code
    Write-M6Json (Join-Path $root 'artifacts/verification/m6-release-build.json') $report
    Pop-Location
}
exit $code
