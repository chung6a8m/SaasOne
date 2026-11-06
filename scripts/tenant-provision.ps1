[CmdletBinding(DefaultParameterSetName = 'Provision')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Bootstrap')][switch]$Bootstrap,
    [Parameter(Mandatory, ParameterSetName = 'Bootstrap')][Guid]$InstallationId,
    [Parameter(Mandatory, ParameterSetName = 'Provision')][Guid]$TenantId,
    [Parameter(Mandatory, ParameterSetName = 'Provision')][Guid]$OperationId,
    [Parameter(Mandatory, ParameterSetName = 'Provision')][string]$Slug,
    [Parameter(Mandatory, ParameterSetName = 'Provision')][ValidateSet('Standard', 'Approval')][string]$Plan,
    [Parameter(Mandatory, ParameterSetName = 'Provision')][DateTimeOffset]$ExpiresAt,
    [string]$HostAssembly = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/SaaS.Web/bin/Release/net10.0/SaaS.Web.dll')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$code = 1
try {
    if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED: dotnet is required.' }
    if (-not (Test-Path -LiteralPath $HostAssembly -PathType Leaf)) { throw 'BLOCKED: a verified Release host build is required.' }
    $arguments = @($HostAssembly, '--maintenance')
    if ($PSCmdlet.ParameterSetName -eq 'Bootstrap') {
        if ($InstallationId -eq [Guid]::Empty) { throw 'InstallationId must be persisted and nonempty.' }
        $arguments += @('bootstrap', $InstallationId.ToString('D'))
    } else {
        if ($TenantId -eq [Guid]::Empty -or $OperationId -eq [Guid]::Empty) { throw 'TenantId and OperationId must be persisted and nonempty.' }
        $arguments += @('provision', $TenantId.ToString('D'), $OperationId.ToString('D'), $Slug, $Plan, $ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", [Globalization.CultureInfo]::InvariantCulture))
    }
    # Connections/credentials are resolved only from the private process environment, never arguments.
    & dotnet @arguments
    $code = $LASTEXITCODE
} catch { Write-Error $_.Exception.Message -ErrorAction Continue }
exit $code
