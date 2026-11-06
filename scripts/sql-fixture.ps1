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
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
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
    if ($negativeCode -eq 0) { throw 'Wrong SQL target was incorrectly accepted.' }
    $env:SAAS_SQL_SMOKE_CONNECTION = $connection
    & pwsh -NoProfile -File scripts/check.ps1 -Scope SqlSmoke
    $positiveCode = $LASTEXITCODE
    if ($positiveCode -ne 0) { throw 'Real SQL smoke failed.' }
    $digest = (& docker image inspect $image --format '{{index .RepoDigests 0}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record SQL image identity.' }
    $sha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record source identity.' }
    [ordered]@{ sourceSha = $sha; image = $image; digest = $digest; wrongTargetExitCode = $negativeCode; sqlSmokeExitCode = $positiveCode; discovered = 2; executed = 2; passed = 2; failed = 0; skipped = 0 } |
        ConvertTo-Json | Set-Content artifacts/verification/sql-fixture.json -Encoding utf8
    $code = 0
} catch { Write-Host ('FAIL: ' + $_.Exception.Message); $code = 1 }
finally {
    if ($started) {
        & docker rm --force $name | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL: disposable SQL container cleanup failed.'; $code = 1 }
    }
    $env:MSSQL_SA_PASSWORD = $oldSa
    $env:SAAS_SQL_SMOKE_CONNECTION = $oldConnection
    Pop-Location
}
exit $code
