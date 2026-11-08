[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$home=Join-Path $root 'artifacts/m7/template-cli'
$out=Join-Path $root 'artifacts/m7/template-smoke'
$oldHome=$env:DOTNET_CLI_HOME
Push-Location $root
try {
    foreach ($path in @($home,$out)) { if (Test-Path $path) { Remove-Item $path -Recurse -Force } }
    $env:DOTNET_CLI_HOME=$home
    & dotnet new install (Join-Path $root 'templates/module')
    if ($LASTEXITCODE -ne 0) { throw 'Module template install failed.' }
    & dotnet new saas-module --name SaaS.SmokeModule --output $out
    if ($LASTEXITCODE -ne 0) { throw 'Module template generation failed.' }
    $project=Join-Path $out 'SaaS.SmokeModule.csproj'
    $source=Get-Content (Join-Path $out 'ModuleContract.cs') -Raw
    if ($source -notmatch 'namespace SaaS\.SmokeModule;' -or $source -match 'SaaS.Module.Template') { throw 'Module namespace substitution failed.' }
    $reference=Join-Path $root 'src/Platform/SaaS.Abstractions/SaaS.Abstractions.csproj'
    & dotnet restore $project "-p:SaasAbstractionsProject=$reference"
    if ($LASTEXITCODE -ne 0) { throw 'Generated module restore failed.' }
    & dotnet build $project -c Release --no-restore "-p:SaasAbstractionsProject=$reference"
    if ($LASTEXITCODE -ne 0) { throw 'Generated module build failed.' }
    $assets=Get-Content (Join-Path $out 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($assets.libraries.Count -ne 1 -or 'SaaS.Abstractions/1.0.0' -notin @($assets.libraries.Keys)) { throw 'Generated module pulled an unexpected dependency.' }
    Write-Host 'PASS: module template generated, substituted, restored and built with Abstractions only.'
} finally {
    $env:DOTNET_CLI_HOME=$oldHome
    if (Test-Path $home) { Remove-Item $home -Recurse -Force }
    Pop-Location
}
