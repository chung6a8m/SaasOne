# Private terminal UI. Run outside the HTTP host with operator-only deployment secrets.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssemblyPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) { throw 'Pinned dotnet is required.' }
function Invoke-Maintenance([string[]]$Command) {
    & dotnet $assembly --maintenance @Command
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Operation rejected or interrupted. Inspect current state; never clear fences manually.' }
}
Write-Host 'PRIVATE M3 OPERATIONS. No HTTP exposure. Secrets must be supplied through deployment environment references.'
Write-Host 'Recovery reconciles security only; it does not restore SQL backups. Purge is irreversible.'
while ($true) {
    Invoke-Maintenance -Command @('ops-list')
    Write-Host 'list | install | lifecycle | plan | resume | recovery-begin | recovery-approve | recovery-resume | purge | exit'
    $action = Read-Host 'Action'
    if ($action -eq 'exit') { break }
    if ($action -eq 'list') { continue }
    if ($action -notin @('install','lifecycle','plan','resume','recovery-begin','recovery-approve','recovery-resume','purge')) { Write-Warning 'Unknown action'; continue }
    $tenant = Read-Host 'Tenant UUID from current list'
    $version = Read-Host 'Expected RegistryVersion from current list'
    if ($action -eq 'lifecycle') {
        $state = Read-Host 'active | suspended | delete (PendingDeletion)'
        $expiry = Read-Host 'Expiry in O format, including UTC offset'
        Invoke-Maintenance -Command @('ops-lifecycle', $tenant, $version, $state, $expiry)
        continue
    }
    $operation = Read-Host 'Operation UUID (new UUID for new work; recorded UUID for resume/approval/retry)'
    switch ($action) {
        'install' { $command = @('access-install', $tenant, $operation, $version) }
        'plan' { $command = @('ops-plan', $tenant, $operation, $version, (Read-Host 'Standard | Approval')) }
        'resume' { $command = @('ops-resume', $tenant, $operation, $version) }
        'recovery-begin' { $command = @('ops-recovery-begin', $tenant, $operation, $version) }
        'recovery-resume' { $command = @('ops-recovery-resume', $tenant, $operation, $version) }
        'recovery-approve' {
            Write-Host 'Requires recovery authority registered before the operation and SAAS_RECOVERY_REASON in this private process.'
            $path = Read-Host 'Reviewed complete ACL JSON file path'
            $upgrade = Read-Host 'keep-plan | allow-approval-upgrade'
            $command = @('ops-recovery-approve', $tenant, $operation, $version, $path, $upgrade)
        }
        'purge' {
            Write-Warning 'IRREVERSIBLE: deletes only the registered customer database and its SQL logins; retains the platform tombstone.'
            $confirmation = Read-Host 'Type the identical tenant UUID again to confirm'
            $command = @('ops-purge', $tenant, $operation, $version, $confirmation)
        }
    }
    Invoke-Maintenance -Command $command
}
