# Diagnostic regression for the storage drill's secret-free writer child.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('m6-marker-probe-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$process = $null
try {
    $exe = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @($start.Environment.Keys | Where-Object { $_ -match '^(SAAS_|MSSQL_)' })) { $start.Environment.Remove($name) | Out-Null }
    foreach ($argument in @('-NoProfile','-File',(Join-Path $PSScriptRoot 'm6-storage-drill.ps1'),'-Mode','WriteMarker',
        '-PrivateRoot',$directory,'-Resource',('tenant-' + [guid]::NewGuid().ToString('N')),
        '-OperationId',[guid]::NewGuid().ToString('D'),'-OwnerId',[guid]::NewGuid().ToString('D'))) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
    while (-not $process.HasExited -and -not (Test-Path -LiteralPath (Join-Path $directory 'writer.ready')) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    $ready = Test-Path -LiteralPath (Join-Path $directory 'writer.ready')
    if (-not $process.HasExited) { $process.Kill($true) }
    $process.WaitForExit()
    # This child receives no deployment credentials and never creates a key or SQL session.
    # Bound its diagnostic output; never execute this probe against deployment paths.
    $text = $errors.GetAwaiter().GetResult()
    if ($text.Length -gt 4096) { $text = $text.Substring(0,4096) }
    if (-not $ready) { Write-Host $text; throw 'marker_writer_probe_failed' }
    Write-Host 'PASS: production marker writer returned before owned child termination.'
} finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $directory -Recurse -Force
}
