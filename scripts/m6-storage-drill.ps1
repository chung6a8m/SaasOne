# Disposable OS/storage rehearsal. Never point this script at a real deployment.
[CmdletBinding()]
param(
    [switch]$DisposableHostAcknowledged,
    [ValidateSet('Parent','WriteMarker','ReadMarker')][string]$Mode = 'Parent',
    [string]$AssemblyDirectory,
    [string]$PrivateRoot,
    [string]$Resource,
    [guid]$OperationId,
    [guid]$OwnerId
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
if (-not $AssemblyDirectory) { $AssemblyDirectory = Join-Path $root 'src/SaaS.Web/bin/Release/net10.0' }
$AssemblyDirectory = [IO.Path]::GetFullPath($AssemblyDirectory)
function Get-DrillError($ErrorObject) {
    $exception = $ErrorObject.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    if ($null -ne $exception.PSObject.Properties['Code']) { return [string]$exception.Code }
    if ($exception.Message -cmatch '^[a-z][a-z0-9_]+$') { return $exception.Message }
    return $exception.GetType().Name
}
function Import-ProductionTypes {
    foreach ($name in @('SaaS.Abstractions.dll','SaaS.Tenancy.dll')) {
        $path = Join-Path $AssemblyDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'fresh_production_build_required' }
        [Reflection.Assembly]::LoadFrom($path) | Out-Null
    }
}
if ($Mode -ne 'Parent') {
    Import-ProductionTypes
    $env:SAAS_OPERATIONS_HOME = $PrivateRoot
    $type = [SaaS.Tenancy.Operations.OperationChildProcess]
    $path = Join-Path $PrivateRoot ($Resource + '.child.json')
    if ($Mode -eq 'WriteMarker') {
        $markerType = $type.GetNestedType('Marker', [Reflection.BindingFlags]::NonPublic)
        $marker = [Activator]::CreateInstance($markerType,
            [Reflection.BindingFlags]'Public,NonPublic,Instance', $null,
            [object[]]@(1, $Resource, $OperationId, $OwnerId, $null, $null), $null)
        $writer = $type.GetMethod('WriteAsync', [Reflection.BindingFlags]'NonPublic,Static')
        $task = $writer.Invoke($null, [object[]]@($path, $marker, [Threading.CancellationToken]::None))
        $task.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $PrivateRoot 'writer.ready'), 'production_write_completed')
        while ($true) { Start-Sleep -Seconds 5 }
    } else {
        $bytes = [IO.File]::ReadAllBytes($path)
        $marker = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        if ($marker.Version -ne 1 -or $marker.Resource -cne $Resource -or $marker.OperationId -ne $OperationId -or
            $marker.OwnerId -ne $OwnerId -or $null -ne $marker.ProcessId -or $null -ne $marker.StartUtcTicks) {
            throw 'durable_intent_corrupted'
        }
        $demand = $type.GetMethod('DemandQuiescentAsync', [Reflection.BindingFlags]'NonPublic,Static')
        $rejected = $false
        try {
            $task = $demand.Invoke($null, [object[]]@($Resource, [Threading.CancellationToken]::None))
            $task.GetAwaiter().GetResult()
        } catch {
            if ((Get-DrillError $_) -cne 'operation_child_reconciliation_required') { throw }
            $rejected = $true
        }
        if (-not $rejected -or -not (Test-Path -LiteralPath $path)) { throw 'ambiguous_marker_was_bypassed' }
        [IO.File]::WriteAllText((Join-Path $PrivateRoot 'reader.json'), (@{
            intentParsed = $true; automaticTakeoverDenied = $true; markerRetained = $true
            markerSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        } | ConvertTo-Json))
        exit 0
    }
}
if (-not $DisposableHostAcknowledged) { throw 'disposable_host_acknowledgement_required' }
if (-not ($IsWindows -or $IsLinux) -or $PSVersionTable.PSVersion.ToString() -ne '7.6.9') { throw 'pinned_windows_or_linux_powershell_required' }
$run = [guid]::NewGuid().ToString('N')
$base = if ($IsWindows) { Join-Path ($env:SystemDrive + '\') ('saas-m6-' + $run) } else { Join-Path '/tmp' ('saas-m6-' + $run) }
$backup = Join-Path $base 'backup'
$keys = Join-Path $base 'keys'
$operations = Join-Path $base 'operations'
$public = Join-Path $base 'public-control'
$sqlUser = 's6sql' + $run.Substring(0,8)
$otherUser = 's6usr' + $run.Substring(0,8)
$accounts = [Collections.Generic.List[string]]::new()
$previous = @{}
$writerProcess = $null
$drains = @()
$key = $null; $replacement = $null; $password = $null
$code = 1
$report = [ordered]@{ formatVersion = 1; status = 'FAIL'; sourceSha = $null; os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    powershell = $PSVersionTable.PSVersion.ToString(); startedUtc = [DateTimeOffset]::UtcNow
    scope = 'Disposable protected-store principals, signing-key retention and production marker writer process crash'
    exclusions = @('No production deployment or secret-manager certification','No power-loss/filesystem-controller durability claim','Key fixture is authenticated bytes; SqlM6 separately proves physical SQL backups')
    steps = [ordered]@{}; cleanup = 'PENDING'; exitCode = 1 }
$evidence = Join-Path $root 'artifacts/verification/m6-storage-drill.json'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidence)) | Out-Null
function Invoke-DrillCommand([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments *> $null
    if ($LASTEXITCODE -ne 0) { throw 'disposable_os_command_failed' }
}
function Set-PrivateDirectory([string]$Path, $SqlSid = $null) {
    if ($IsWindows) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
            [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        }
        if ($null -ne $SqlSid) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($SqlSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')) }
        Set-Acl -LiteralPath $Path -AclObject $acl
    } else {
        [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]448)
        if ($null -ne $SqlSid) {
            Invoke-DrillCommand 'sudo' @('-n','chgrp',$sqlUser,$Path)
            [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]1528) # 02770: operator + SQL group, no others.
        }
    }
}
function Assert-PrincipalAccess([string]$User, [string]$ReadPath, [string]$WritePath, [bool]$Readable, [bool]$Writable) {
    if ($IsWindows) {
        $canRead = [M6StoragePrincipal]::Read($User, $password, $ReadPath)
        $canWrite = [M6StoragePrincipal]::Write($User, $password, $WritePath)
    } else {
        & sudo -n runuser -u $User -- cat -- $ReadPath *> $null
        $canRead = $LASTEXITCODE -eq 0
        & sudo -n runuser -u $User -- touch -- $WritePath *> $null
        $canWrite = $LASTEXITCODE -eq 0
    }
    if ($canRead -ne $Readable -or $canWrite -ne $Writable) { throw 'protected_principal_access_mismatch' }
}
function Start-MarkerChild([string]$ChildMode, [string]$ResourceName, [guid]$Operation, [guid]$Owner) {
    $executable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($item in @('-NoProfile','-File',$PSCommandPath,'-Mode',$ChildMode,'-AssemblyDirectory',$AssemblyDirectory,
        '-PrivateRoot',$operations,'-Resource',$ResourceName,'-OperationId',$Operation.ToString('D'),'-OwnerId',$Owner.ToString('D'))) { $start.ArgumentList.Add($item) }
    foreach ($name in @($start.Environment.Keys | Where-Object { $_ -match '^(SAAS_|MSSQL_)' })) { $start.Environment.Remove($name) | Out-Null }
    return [Diagnostics.Process]::Start($start)
}
try {
    $report.sourceSha = (& git -C $root rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $report.sourceSha -notmatch '^[a-f0-9]{40}$') { throw 'source_sha_required' }
    Import-ProductionTypes
    foreach ($path in @($base,$backup,$keys,$operations,$public)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
    if ($IsWindows) {
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant() + 'Aa1!'
        $secure = ConvertTo-SecureString $password -AsPlainText -Force
        $sqlAccount = New-LocalUser -Name $sqlUser -Password $secure -Description 'Disposable M6 SQL principal rehearsal'
        $accounts.Add($sqlUser)
        $null = New-LocalUser -Name $otherUser -Password $secure -Description 'Disposable M6 unauthorized principal rehearsal'
        $accounts.Add($otherUser)
        foreach ($account in $accounts) { Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $account }
        # Public controls prove the impersonated tokens work; a denied logon must not masquerade as ACL denial.
        foreach ($path in @($base,$public)) {
            $acl = Get-Acl -LiteralPath $path
            $rights = if ($path -eq $public) { 'Modify' } else { 'ReadAndExecute' }
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'), $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
            Set-Acl -LiteralPath $path -AclObject $acl
        }
        Set-PrivateDirectory $backup $sqlAccount.SID
        Set-PrivateDirectory $keys
        Set-PrivateDirectory $operations
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public static class M6StoragePrincipal {
    [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);
    private static bool Run(string user, string password, Action action) {
        if (!LogonUser(user, ".", password, 2, 0, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token) return WindowsIdentity.RunImpersonated(token, () => {
            try { action(); return true; } catch (UnauthorizedAccessException) { return false; }
        });
    }
    public static bool Read(string user, string password, string path) => Run(user, password, () => File.ReadAllBytes(path));
    public static bool Write(string user, string password, string path) => Run(user, password, () => File.WriteAllText(path, "fixture"));
}
'@
        $report.steps.acl = @{ backupSddl = (Get-Acl -LiteralPath $backup).Sddl; keySddl = (Get-Acl -LiteralPath $keys).Sddl }
    } else {
        Invoke-DrillCommand 'sudo' @('-n','useradd','--system','--user-group','--no-create-home','--shell','/usr/sbin/nologin',$sqlUser)
        $accounts.Add($sqlUser); $otherUser = 'nobody'
        [IO.File]::SetUnixFileMode($base, [IO.UnixFileMode]493)
        [IO.File]::SetUnixFileMode($public, [IO.UnixFileMode]511)
        Set-PrivateDirectory $backup $sqlUser
        Set-PrivateDirectory $keys
        Set-PrivateDirectory $operations
        $report.steps.acl = @{ backupMode = [int][IO.File]::GetUnixFileMode($backup); keyMode = [int][IO.File]::GetUnixFileMode($keys) }
    }
    $control = Join-Path $public 'read.txt'; [IO.File]::WriteAllText($control, 'public fixture')
    $sample = Join-Path $backup 'read.txt'; [IO.File]::WriteAllText($sample, 'private fixture')
    $keyPath = Join-Path $keys 'retained-v1.key'
    $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    [IO.File]::WriteAllBytes($keyPath, $key)
    foreach ($user in @($sqlUser,$otherUser)) {
        Assert-PrincipalAccess $user $control (Join-Path $public ($user + '.write')) $true $true
    }
    Assert-PrincipalAccess $sqlUser $sample (Join-Path $backup 'sql.write') $true $true
    Assert-PrincipalAccess $otherUser $sample (Join-Path $backup 'unauthorized.write') $false $false
    foreach ($user in @($sqlUser,$otherUser)) {
        Assert-PrincipalAccess $user $keyPath (Join-Path $keys ($user + '.write')) $false $false
    }
    $report.steps.principalControls = 'PASS: operator and SQL access; unauthorized backup access denied; signing key denied to SQL and unauthorized users'
    foreach ($name in @('SAAS_BACKUP_ROOT','SAAS_SQL_BACKUP_ROOT','SAAS_BACKUP_STORE_ACK','SAAS_BACKUP_MANIFEST_KEY')) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $env:SAAS_BACKUP_ROOT = $backup; $env:SAAS_SQL_BACKUP_ROOT = $backup; $env:SAAS_BACKUP_STORE_ACK = 'private-shared-store-v1'
    $tenant = [guid]::NewGuid(); $backupId = [guid]::NewGuid()
    $directory = Join-Path $backup $backupId.ToString('N'); [IO.Directory]::CreateDirectory($directory) | Out-Null
    $data = [Text.Encoding]::UTF8.GetBytes('Authenticated storage/key-retention fixture; NOT a SQL backup.')
    [IO.File]::WriteAllBytes((Join-Path $directory 'database.bak'), $data)
    $part = [SaaS.Tenancy.Release.BackupPart]::new('database.bak', $data.LongLength,
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)), [guid]::NewGuid(), 1)
    $manifest = [SaaS.Tenancy.Release.BackupManifest]::new(1, $tenant, $backupId,
        [SaaS.Tenancy.Data.TenancyConnections]::TenantDatabase($tenant), [DateTimeOffset]::UtcNow, [SaaS.Tenancy.Release.BackupPart[]]@($part))
    [IO.File]::WriteAllBytes((Join-Path $directory 'manifest.json'), [SaaS.Tenancy.Release.BackupManifestCodec]::Sign($manifest, $key))
    $replacement = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $env:SAAS_BACKUP_MANIFEST_KEY = [Convert]::ToBase64String($replacement)
    $store = [SaaS.Tenancy.Release.SqlBackupStore]::new()
    try {
        $rejected = $false
        try {
            $opened = $store.OpenAsync($tenant, $backupId, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $opened.DisposeAsync().AsTask().GetAwaiter().GetResult()
        } catch {
            if ((Get-DrillError $_) -cne 'backup_manifest_untrusted') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'replacement_key_accepted_historical_manifest' }
    } finally { $store.Dispose() }
    [Environment]::SetEnvironmentVariable('SAAS_BACKUP_MANIFEST_KEY', $null)
    $missingRejected = $false
    try { $unexpected = [SaaS.Tenancy.Release.SqlBackupStore]::new(); $unexpected.Dispose() }
    catch { if ((Get-DrillError $_) -cne 'missing_deployment_secret') { throw }; $missingRejected = $true }
    if (-not $missingRejected) { throw 'missing_key_accepted' }
    $env:SAAS_BACKUP_MANIFEST_KEY = [Convert]::ToBase64String([IO.File]::ReadAllBytes($keyPath))
    $store = [SaaS.Tenancy.Release.SqlBackupStore]::new()
    try {
        $opened = $store.OpenAsync($tenant, $backupId, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        try { $report.steps.retainedKey = @{ status = 'PASS'; backupId = $backupId; manifestSha256 = $opened.ManifestSha256; wrongAndMissingKeysRejected = $true } }
        finally { $opened.DisposeAsync().AsTask().GetAwaiter().GetResult() }
    } finally { $store.Dispose() }
    $resourceName = 'tenant-' + [guid]::NewGuid().ToString('N')
    $operation = [guid]::NewGuid(); $owner = [guid]::NewGuid()
    $writerProcess = Start-MarkerChild 'WriteMarker' $resourceName $operation $owner
    $drains = @($writerProcess.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null), $writerProcess.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null))
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
    while (-not (Test-Path -LiteralPath (Join-Path $operations 'writer.ready'))) {
        if ($writerProcess.HasExited -or [DateTimeOffset]::UtcNow -gt $deadline) { throw 'production_marker_writer_not_ready' }
        Start-Sleep -Milliseconds 50
    }
    $report.steps.marker = @{ processId = $writerProcess.Id; startUtc = $writerProcess.StartTime.ToUniversalTime(); killUtc = [DateTimeOffset]::UtcNow }
    $writerProcess.Kill($true); $writerProcess.WaitForExit()
    foreach ($drain in $drains) { $drain.GetAwaiter().GetResult() }
    $reader = Start-MarkerChild 'ReadMarker' $resourceName $operation $owner
    try {
        $readerOut = $reader.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
        $readerError = $reader.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        if (-not $reader.WaitForExit(40000)) { $reader.Kill($true); $reader.WaitForExit(); throw 'marker_reader_timeout' }
        $readerOut.GetAwaiter().GetResult(); $readerError.GetAwaiter().GetResult()
        if ($reader.ExitCode -ne 0) { throw 'fresh_process_marker_reopen_failed' }
    } finally { $reader.Dispose() }
    $report.steps.marker.reopened = Get-Content -LiteralPath (Join-Path $operations 'reader.json') -Raw | ConvertFrom-Json
    $report.steps.marker.resolution = 'Owned writer exited; drill writer launches no mutating child or SQL; ambiguous marker retained until explicit disposable-fixture cleanup.'
    $report.steps.marker.powerLoss = 'NOT_TESTED'
    $code = 0
} catch {
    $report.errorCode = Get-DrillError $_
    $report.errorLine = $_.InvocationInfo.ScriptLineNumber
} finally {
    try {
        if ($null -ne $writerProcess) {
            if (-not $writerProcess.HasExited) { $writerProcess.Kill($true); $writerProcess.WaitForExit() }
            foreach ($drain in $drains) { $drain.GetAwaiter().GetResult() }
            $writerProcess.Dispose()
        }
    } catch { $code = 1; $report.processCleanup = 'FAIL' }
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    if ($null -ne $key) { [Array]::Clear($key, 0, $key.Length) }
    if ($null -ne $replacement) { [Array]::Clear($replacement, 0, $replacement.Length) }
    $password = $null
    try {
        foreach ($account in $accounts) {
            if ($IsWindows) { Remove-LocalUser -Name $account }
            else { Invoke-DrillCommand 'sudo' @('-n','userdel',$account) }
        }
        if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
        $report.cleanup = 'PASS'
    } catch { $code = 1; $report.cleanup = 'FAIL' }
    if ($code -eq 0) { $report.status = 'PASS' }
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText($evidence, ($report | ConvertTo-Json -Depth 12))
    [IO.File]::WriteAllText(($evidence + '.sha256'), (Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash + "`n")
    Write-Host ('M6 storage drill: ' + $report.status)
    if ($report.Contains('errorCode')) { Write-Host ($report.errorCode + ' at script line ' + $report.errorLine) }
}
exit $code
