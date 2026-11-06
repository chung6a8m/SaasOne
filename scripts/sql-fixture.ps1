# Disposable development-only SQL instance. No existing server/database is touched.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m0-' + [Guid]::NewGuid().ToString('N')
$oldSa = $env:MSSQL_SA_PASSWORD
$oldConnection = $env:SAAS_SQL_SMOKE_CONNECTION
$sa = 'Aa1!' + [Guid]::NewGuid().ToString('N')
$runtimePassword = 'Bb2!' + [Guid]::NewGuid().ToString('N')
$started = $false
$code = 1
$blocked = $false
$verification = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/verification'
$report = [ordered]@{
    runId = [Guid]::NewGuid().ToString('N')
    timestampUtc = [DateTimeOffset]::UtcNow
    completedUtc = $null
    sourceSha = $null
    status = 'FAIL'
    exitCode = 1
    image = $image
    digest = $null
    wrongTargetExitCode = $null
    sqlSmokeExitCode = $null
    cleanupExitCode = $null
    discovered = 2
    executed = 0
    passed = 0
    failed = 0
    skipped = 2
}
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # On every attempt, stale reports must be gone before Docker/git prerequisites are tested.
    New-Item $verification -ItemType Directory -Force | Out-Null
    foreach ($artifactName in @('sql-fixture.json', 'sql-smoke.json', 'sql-command.json')) {
        $artifact = Join-Path $verification $artifactName
        if (Test-Path $artifact) { Remove-Item $artifact -Force }
    }
    $sha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record source identity.' }
    $report.sourceSha = $sha
    if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED: Docker is required for the disposable SQL fixture.' }
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$sa"; Write-Host "::add-mask::$runtimePassword" }
    $env:MSSQL_SA_PASSWORD = $sa
    & docker pull $image
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED: pinned SQL image could not be acquired.' }
    & docker run --detach --name $name --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD --publish '127.0.0.1::1433' $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SQL fixture start failed.' }
    $started = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        'SELECT 1;' | & docker exec -i $name /bin/sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'BLOCKED: SQL did not become query-ready.' }
    $sql = @"
CREATE DATABASE [saas_m0_smoke];
GO
CREATE LOGIN [saas_smoke_runtime] WITH PASSWORD = N'$runtimePassword';
GO
USE [saas_m0_smoke];
CREATE USER [saas_smoke_runtime] FOR LOGIN [saas_smoke_runtime];
GRANT CONNECT TO [saas_smoke_runtime];
GO
"@
    $sql | & docker exec -i $name /bin/sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SQL fixture initialization failed.' }
    $binding = (& docker port $name 1433/tcp | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'SQL loopback binding is invalid.' }
    $port = $Matches[1]
    $connection = "Server=127.0.0.1,$port;Initial Catalog=saas_m0_smoke;User ID=saas_smoke_runtime;Password=$runtimePassword;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15"
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$connection" }
    $env:SAAS_SQL_SMOKE_CONNECTION = $connection.Replace('Initial Catalog=saas_m0_smoke', 'Initial Catalog=master')
    & pwsh -NoProfile -File scripts/check.ps1 -Scope SqlSmoke
    $negativeCode = $LASTEXITCODE
    $report.wrongTargetExitCode = $negativeCode
    $report.executed++
    if ($negativeCode -eq 0) { throw 'Wrong SQL target was incorrectly accepted.' }
    $env:SAAS_SQL_SMOKE_CONNECTION = $connection
    & pwsh -NoProfile -File scripts/check.ps1 -Scope SqlSmoke
    $positiveCode = $LASTEXITCODE
    $report.sqlSmokeExitCode = $positiveCode
    $report.executed++
    if ($positiveCode -ne 0) { throw 'Real SQL smoke failed.' }
    $digest = (& docker image inspect $image --format '{{index .RepoDigests 0}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record SQL image identity.' }
    $report.digest = $digest
    $code = 0
} catch {
    $blocked = $_.Exception.Message -like 'BLOCKED:*'
    Write-Host ('FAIL: ' + $_.Exception.Message)
    $code = 1
}
finally {
    if ($started) {
        try {
            & docker rm --force $name | Out-Null
            $report.cleanupExitCode = $LASTEXITCODE
            if ($report.cleanupExitCode -ne 0) {
                Write-Host 'FAIL: disposable SQL container cleanup failed.'
                $code = 1
                $blocked = $false
            }
        } catch {
            Write-Host 'FAIL: disposable SQL container cleanup raised an error.'
            $report.cleanupExitCode = 1
            $code = 1
            $blocked = $false
        }
    }

    if ($code -ne 0) {
        # Even a late failure (including docker rm) cannot publish positive probe evidence.
        try {
            foreach ($artifactName in @('sql-smoke.json', 'sql-command.json')) {
                $artifact = Join-Path $verification $artifactName
                if (Test-Path $artifact) { Remove-Item $artifact -Force }
            }
        } catch {
            Write-Host 'FAIL: could not invalidate incomplete SQL probe evidence.'
            $blocked = $false
        }
    }
    $report.skipped = $report.discovered - $report.executed
    if ($code -eq 0) { $report.passed = $report.executed }
    else { $report.failed = $report.executed }
    $report.status = if ($code -eq 0) { 'PASS' } elseif ($blocked) { 'BLOCKED' } else { 'FAIL' }
    $report.exitCode = $code
    $report.completedUtc = [DateTimeOffset]::UtcNow

    # Publish only after cleanup. A temporary file avoids a partial successful JSON report.
    $temporary = Join-Path $verification ('sql-fixture-' + $report.runId + '.tmp')
    try {
        $report | ConvertTo-Json -Depth 6 | Set-Content $temporary -Encoding utf8
        Move-Item $temporary (Join-Path $verification 'sql-fixture.json') -Force
    } catch {
        Write-Host 'FAIL: cannot persist final SQL fixture evidence.'
        $code = 1
        if (Test-Path $temporary) { Remove-Item $temporary -Force -ErrorAction SilentlyContinue }
        foreach ($artifactName in @('sql-smoke.json', 'sql-command.json')) {
            Remove-Item (Join-Path $verification $artifactName) -Force -ErrorAction SilentlyContinue
        }
    }
    $env:MSSQL_SA_PASSWORD = $oldSa
    $env:SAAS_SQL_SMOKE_CONNECTION = $oldConnection
    Pop-Location
}
exit $code
