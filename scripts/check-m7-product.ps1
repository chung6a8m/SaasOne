[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/m6-common.ps1"
. "$PSScriptRoot/m7-product-graph.ps1"
$root = Split-Path $PSScriptRoot -Parent
$report = [ordered]@{ scope = 'M7 independent product and module template'; status = 'FAIL'; sourceSha = $null }
$code = 1
Push-Location $root
try {
    $report.sourceSha = Get-M6Sha
    if ((& git status --porcelain | Out-String).Trim()) { throw 'clean_checkout_required' }
    Invoke-M6Checked pwsh @('-NoProfile', '-File', "$PSScriptRoot/test-m7-product-graph.ps1")
    foreach ($name in @('SaaS.Workspace.Web', 'SaaS.Workspace.Theme')) {
        if (-not (Test-Path "samples/Workspace/$name/packages.lock.json")) { throw 'BLOCKED:sample_committed_lock_required' }
    }
    Invoke-M6Checked dotnet @('restore', 'samples/Workspace/Workspace.slnx', '--locked-mode')
    $publish = Join-Path $root 'artifacts/workspace'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    Invoke-M6Checked dotnet @('publish', 'samples/Workspace/SaaS.Workspace.Web/SaaS.Workspace.Web.csproj', '-c', 'Release', '--no-restore', '-o', $publish)
    $assets = Get-Content samples/Workspace/SaaS.Workspace.Web/obj/project.assets.json -Raw | ConvertFrom-Json -Depth 100
    $deps = Get-Content "$publish/SaaS.Workspace.Web.deps.json" -Raw | ConvertFrom-Json -Depth 100
    $native = & dotnet "$publish/SaaS.Workspace.Web.dll" --feature-graph
    if ($LASTEXITCODE -ne 0) { throw 'product_native_inventory_failed' }
    $native = @($native | ConvertFrom-Json)
    $libraries = @($assets.libraries.PSObject.Properties.Name | Sort-Object -Unique)
    $features = @($native | ForEach-Object { $_.Id; $_.ExtensionId; $_.Dependencies } | Sort-Object -Unique)
    $assemblies = @()
    foreach ($file in Get-ChildItem $publish -Filter *.dll -Recurse -File) {
        try { $assemblies += [Reflection.AssemblyName]::GetAssemblyName($file.FullName).Name }
        catch [BadImageFormatException] { } # Native DLLs have no managed assembly identity.
    }
    Assert-M7ProductInventory $libraries $features $assemblies
    $publishedLibraries = @($deps.libraries.PSObject.Properties.Name)
    Assert-M7ProductInventory $publishedLibraries $features $assemblies
    foreach ($file in Get-ChildItem $publish -Recurse -File) {
        if ($file.Name -match '(?i)^SaaS\.(Orders|OrderApprovals|Web|Theme\.App)([.]|$)') { throw 'product_forbidden_publish_file' }
    }
    $report.projectAndPackageLibraries = $libraries
    $report.publishedLibraries = $publishedLibraries
    $report.nativeFeatures = $native
    $report.assemblyIdentities = @($assemblies | Sort-Object -Unique)
    $report.files = @(Get-ChildItem $publish -Recurse -File | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } })
    $generated = Join-Path $root ('artifacts/m7-template-' + [guid]::NewGuid().ToString('N'))
    foreach ($bad in @('../Escaped', 'SaaS.Bad-Name')) {
        & pwsh -NoProfile -File "$PSScriptRoot/new-module.ps1" -Name $bad -DestinationRoot $generated
        if ($LASTEXITCODE -eq 0 -or (Test-Path $generated)) { throw 'invalid_template_name_not_rejected_before_writes' }
    }
    Invoke-M6Checked pwsh @('-NoProfile', '-File', "$PSScriptRoot/new-module.ps1", '-Name', 'SaaS.Inventory', '-DestinationRoot', $generated)
    $module = Join-Path $generated 'SaaS.Inventory/SaaS.Inventory.csproj'
    $platform = "-p:SaasPlatformRoot=$root/src/Platform"
    Invoke-M6Checked dotnet @('restore', $module, $platform)
    Invoke-M6Checked dotnet @('build', $module, '-c', 'Release', '--no-restore', $platform)
    $report.template = @{ generated = 'SaaS.Inventory'; buildExitCode = 0; invalidNamesRejected = 2 }
    Invoke-M6Checked dotnet @('restore', 'SaasOne.slnx', '--locked-mode')
    Invoke-M6Checked dotnet @('build', 'SaasOne.slnx', '-c', 'Release', '--no-restore')
    Invoke-M6Checked pwsh @('-NoProfile', '-File', "$PSScriptRoot/m7-sql-fixture.ps1", '-Suite', 'Product', '-ProductDll', "$publish/SaaS.Workspace.Web.dll")
    if ((& git status --porcelain | Out-String).Trim()) { throw 'verification_changed_source' }
    $report.status = 'PASS'; $code = 0
} catch { $report.error = Protect-M6Text $_.Exception.Message; Write-Host $report.error }
finally {
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    Write-M6Json (Join-Path $root 'artifacts/m7-evidence/product-build.json') $report
    Pop-Location
}
exit $code
