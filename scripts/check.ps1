[CmdletBinding()]
param([ValidateSet('M0', 'SqlSmoke', 'Prerequisites')][string]$Scope = 'M0')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$commands = [System.Collections.Generic.List[object]]::new()
$report = [ordered]@{ scope = $Scope; status = 'FAIL'; timestampUtc = [DateTimeOffset]::UtcNow; sourceSha = $null; commands = $commands; suites = @() }
$code = 1
function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    Write-Host ('> ' + $Executable + ' ' + ($Arguments -join ' '))
    & $Executable @Arguments | Out-Host
    $nativeCode = $LASTEXITCODE
    $commands.Add([ordered]@{ command = $Executable + ' ' + ($Arguments -join ' '); exitCode = $nativeCode })
    if ($nativeCode -ne 0) { throw "Command failed with exit code ${nativeCode}: $Executable" }
}
Push-Location $root
try {
    if ($Scope -eq 'SqlSmoke') {
        # A blocked or failed attempt must not inherit a previous successful probe.
        $sqlEvidence = Join-Path $root 'artifacts/verification/sql-smoke.json'
        if (Test-Path $sqlEvidence) { Remove-Item $sqlEvidence -Force }
    }
    if ($Scope -eq 'SqlSmoke' -and [string]::IsNullOrWhiteSpace($env:SAAS_SQL_SMOKE_CONNECTION)) {
        Write-Host 'BLOCKED: SAAS_SQL_SMOKE_CONNECTION is required; SQL smoke cannot be skipped.'
        $report.status = 'BLOCKED'
        $code = 2
    } else {
        foreach ($tool in @('dotnet', 'git')) {
            if (-not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) { throw "MissingPrerequisite: $tool" }
        }
        $pin = Get-Content global.json -Raw | ConvertFrom-Json
        $sdk = (& dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdk -ne $pin.sdk.version -or $pin.sdk.rollForward -ne 'disable' -or $pin.sdk.allowPrerelease) { throw 'SDK pin mismatch.' }
        if ($PSVersionTable.PSVersion.ToString() -ne '7.6.9') { throw 'PowerShell 7.6.9 is required.' }
        $sha = (& git rev-parse HEAD | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
        $report.sourceSha = $sha
        $report.environment = [ordered]@{ sdk = $sdk; powershell = $PSVersionTable.PSVersion.ToString(); os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); runnerImage = $env:ImageVersion }
        if ($Scope -eq 'M0') {
            Invoke-Checked dotnet @('--info')
            Invoke-Checked pwsh @('--version')
            Invoke-Checked dotnet @('tool', 'restore')
            Invoke-Checked dotnet @('restore', 'SaasOne.slnx', '--locked-mode')
            Invoke-Checked dotnet @('build', 'SaasOne.slnx', '--configuration', 'Release', '--no-restore')
            $results = Join-Path $root 'artifacts/test-results/m0'
            if (Test-Path $results) { Remove-Item $results -Recurse -Force }
            New-Item $results -ItemType Directory -Force | Out-Null
            Invoke-Checked dotnet @('test', 'SaasOne.slnx', '--configuration', 'Release', '--no-build', '--logger', 'trx', '--results-directory', 'artifacts/test-results/m0')
            $tool = 'tools/SaaS.Build/bin/Release/net10.0/SaaS.Build.dll'
            foreach ($check in @('architecture', 'hygiene', 'locks')) { Invoke-Checked dotnet @($tool, $check, $root) }
            $json = & dotnet $tool evidence $results
            $evidenceCode = $LASTEXITCODE
            $commands.Add([ordered]@{ command = 'SaaS.Build evidence artifacts/test-results/m0'; exitCode = $evidenceCode })
            if ($evidenceCode -ne 0) { throw 'Mandatory test evidence is incomplete.' }
            $report.suites = @($json | ConvertFrom-Json)
        } elseif ($Scope -eq 'SqlSmoke') {
            $tool = 'tools/SaaS.SqlSmoke/bin/Release/net10.0/SaaS.SqlSmoke.dll'
            if (-not (Test-Path $tool)) { throw 'MissingPrerequisite: build the solution before SQL smoke.' }
            $previousSha = $env:SAAS_SOURCE_SHA
            try { $env:SAAS_SOURCE_SHA = $sha; Invoke-Checked dotnet @($tool) }
            finally { $env:SAAS_SOURCE_SHA = $previousSha }
        }
        $report.status = 'PASS'
        $code = 0
        Write-Host "PASS: $Scope"
    }
} catch {
    Write-Host ('FAIL: ' + $_.Exception.Message)
    $code = 1
} finally {
    if ($Scope -in @('M0', 'SqlSmoke')) {
        New-Item 'artifacts/verification' -ItemType Directory -Force | Out-Null
        $report.exitCode = $code
        $reportName = if ($Scope -eq 'M0') { 'm0.json' } else { 'sql-command.json' }
        $report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path 'artifacts/verification' $reportName) -Encoding utf8
    }
    Pop-Location
}
exit $code
