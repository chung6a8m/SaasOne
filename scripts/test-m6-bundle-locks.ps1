[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'm6-bundle-locks.ps1')

$baseline = '{"version":2,"dependencies":{"net10.0":{"Example":{"type":"Direct","resolved":"1.0.0","contentHash":"sample"}}}}'
$valid = '{"version":2,"dependencies":{"net10.0":{"Example":{"type":"Direct","resolved":"1.0.0","contentHash":"sample"}},"net10.0/win-x64":{"Example":{"type":"Direct","resolved":"1.0.0","contentHash":"sample"}}}}'
$badBase = '{"version":2,"dependencies":{"net10.0":{"Example":{"type":"Direct","resolved":"2.0.0","contentHash":"sample"}},"net10.0/win-x64":{}}}'
$badRid = '{"version":2,"dependencies":{"net10.0":{"Example":{"type":"Direct","resolved":"1.0.0","contentHash":"sample"}},"net10.0/linux-x64":{}}}'
$badRidVersion = '{"version":2,"dependencies":{"net10.0":{"Example":{"type":"Direct","resolved":"1.0.0","contentHash":"sample"}},"net10.0/win-x64":{"Example":{"type":"Direct","resolved":"2.0.0","contentHash":"sample"}}}}'
$badExtra = '{"version":2,"dependencies":{"net10.0":{"Example":{"type":"Direct","resolved":"1.0.0","contentHash":"sample"}},"net10.0/win-x64":{},"net10.0/unknown":{}}}'

if (-not (Test-M6RidLockDelta $baseline $valid 'win-x64')) {
    throw 'valid_rid_only_lock_change_rejected'
}
if (-not (Test-M6RidLockDelta $baseline $valid.Replace('/win-x64', '/linux-x64') 'linux-x64')) {
    throw 'valid_linux_rid_only_lock_change_rejected'
}
foreach ($case in @(
    @{ name = 'base_package_changed'; value = $badBase },
    @{ name = 'wrong_target_rid'; value = $badRid },
    @{ name = 'rid_dependency_upgraded'; value = $badRidVersion },
    @{ name = 'unexpected_extra_target'; value = $badExtra }
)) {
    $blocked = $false
    try { Test-M6RidLockDelta $baseline $case.value 'win-x64' | Out-Null }
    catch { $blocked = $true }
    if (-not $blocked) { throw ('lock_regression_not_rejected:' + $case.name) }
}
Write-Host 'PASS: six RID lock-drift validation scenarios.'
