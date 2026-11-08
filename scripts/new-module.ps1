[CmdletBinding()]
param([Parameter(Mandatory)][string]$Name, [string]$DestinationRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$hive = Join-Path ([IO.Path]::GetTempPath()) ('saas-template-' + [guid]::NewGuid().ToString('N'))
$created = $false
$destination = $null
$code = 1
try {
    if ($Name -cnotmatch '^SaaS\.[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)*$' -or $Name.Length -gt 100) { throw 'module_name_invalid' }
    if ([string]::IsNullOrWhiteSpace($DestinationRoot)) { $DestinationRoot = Join-Path $root 'src/Modules' }
    $parent = [IO.Path]::GetFullPath($DestinationRoot)
    $destination = Join-Path $parent $Name
    if (Test-Path -LiteralPath $destination) { throw 'module_destination_exists' }
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'module_destination_link_forbidden' }
    & dotnet new install (Join-Path $root 'templates/saas-module') --debug:custom-hive $hive
    if ($LASTEXITCODE -ne 0) { throw 'module_template_install_failed' }
    # The name contains no path separators; output is new and is never overwritten.
    New-Item -ItemType Directory -Path $destination | Out-Null
    $created = $true
    & dotnet new saas-module --name $Name --output $destination --debug:custom-hive $hive
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $destination "$Name.csproj"))) { throw 'module_generation_failed' }
    Write-Host "Generated $Name. Add it to the chosen product host explicitly; no host or platform was modified."
    $code = 0
} catch { Write-Host $_.Exception.Message }
finally {
    if (Test-Path -LiteralPath $hive) { Remove-Item -LiteralPath $hive -Recurse -Force }
    if ($code -ne 0 -and $created -and (Test-Path -LiteralPath $destination)) { Remove-Item -LiteralPath $destination -Recurse -Force }
}
exit $code
