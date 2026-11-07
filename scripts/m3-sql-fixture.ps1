# Disposable SQL Server acceptance gate; never connects to an existing deployment.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m3-' + [Guid]::NewGuid().ToString('N')
$previousSa = $env:MSSQL_SA_PASSWORD
$previousConnection = $env:SAAS_M1_ADMIN_CONNECTION
$started = $false
$code = 1
$required = @(
    'Gate_close_drains_writers_and_timeout_does_not_claim_durable_closure',
    'Preauthorized_writer_cannot_cross_closed_or_newer_gate_after_restart',
    'Stale_owner_fence_and_wrong_tenant_cannot_open_or_write_the_gate',
    'Cross_context_acl_and_audit_commit_on_one_connection_and_transaction',
    'Audit_failure_rolls_back_the_entire_acl_transaction'
)
$report = [ordered]@{
    scope = 'M3 SQL infrastructure'; status = 'FAIL'; sourceSha = $null; image = $image; digest = $null
    command = 'dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter Category=SqlM3 --logger trx --results-directory artifacts/test-results/m3-poc'
    testExitCode = $null; cleanupExitCode = $null; total = 0; executed = 0; passed = 0; failed = 0; skipped = 0
    mandatoryCases = @($required | ForEach-Object { [ordered]@{ name = $_; discovered = 0; outcome = 'NotExecuted' } })
    discoveredCases = @(); missingMandatoryCases = @($required)
}
Push-Location $root
try {
    New-Item artifacts/verification -ItemType Directory -Force | Out-Null
    Remove-Item artifacts/verification/m3-poc.json -Force -ErrorAction SilentlyContinue
    Remove-Item artifacts/test-results/m3-poc -Recurse -Force -ErrorAction SilentlyContinue
    New-Item artifacts/test-results/m3-poc -ItemType Directory -Force | Out-Null
    $report.sourceSha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $report.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record source identity.' }
    if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED: Docker is required.' }
    $env:MSSQL_SA_PASSWORD = 'Aa1!' + [Guid]::NewGuid().ToString('N')
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:MSSQL_SA_PASSWORD" }
    & docker pull $image
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED: pinned SQL image unavailable.' }
    $report.digest = (& docker image inspect $image --format '{{index .RepoDigests 0}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($report.digest)) { throw 'Cannot record image identity.' }
    & docker run --detach --name $name --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD --publish '127.0.0.1::1433' $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SQL instance failed to start.' }
    $started = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        "IF CONVERT(varchar(32), SERVERPROPERTY('ProductVersion')) <> '16.0.4305.5' THROW 51000, 'Wrong SQL patch', 1; SELECT 1;" | & docker exec -i $name /bin/sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'BLOCKED: required SQL patch did not become query-ready.' }
    $binding = (& docker port $name 1433/tcp | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Invalid loopback binding.' }
    $env:SAAS_M1_ADMIN_CONNECTION = "Server=127.0.0.1,$($Matches[1]);Initial Catalog=master;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;Pooling=False"
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:SAAS_M1_ADMIN_CONNECTION" }
    & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlM3' --logger trx --results-directory artifacts/test-results/m3-poc
    $report.testExitCode = $LASTEXITCODE
    $files = @(Get-ChildItem artifacts/test-results/m3-poc -Filter *.trx)
    if ($files.Count -ne 1) { throw 'Exactly one fresh SQL test evidence file is required.' }
    [xml]$trx = Get-Content $files[0].FullName -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    foreach ($counter in @('total', 'executed', 'passed', 'failed')) { $report[$counter] = [int]$counters.$counter }
    $report.skipped = $report.total - $report.executed
    $results = @($trx.TestRun.Results.UnitTestResult)
    $report.discoveredCases = @($results | ForEach-Object { [ordered]@{ name = $_.testName; outcome = $_.outcome } })
    $report.mandatoryCases = @($required | ForEach-Object {
        $case = $_
        $found = @($results | Where-Object { $_.testName -ceq ('SaaS.UnitTests.M3SqlTests.' + $case) })
        $outcome = if ($found.Count -eq 0) { 'Missing' } elseif ($found.Count -ne 1) { 'Duplicate' } else { [string]$found[0].outcome }
        [ordered]@{ name = $case; discovered = $found.Count; outcome = $outcome }
    })
    $report.missingMandatoryCases = @($report.mandatoryCases | Where-Object { $_.discovered -eq 0 } | ForEach-Object { $_.name })
    # Record the full missing/failed inventory BEFORE throwing. Passing native-store
    # subtests must never masquerade as passing API/web/PKCE/production acceptance cases.
    if ($report.testExitCode -ne 0 -or $report.total -lt 1 -or $report.executed -ne $report.total -or $report.passed -ne $report.total -or $report.failed -ne 0) {
        throw 'Every discovered M3 SQL infrastructure case must execute and pass.'
    }
    if (@($report.mandatoryCases | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0) {
        throw 'BLOCKED: mandatory end-to-end M3 SQL infrastructure evidence is missing or invalid.'
    }
    $code = 0
} catch {
    Write-Host ('FAIL: ' + $_.Exception.Message)
    if ($_.Exception.Message -like 'BLOCKED:*') { $report.status = 'BLOCKED' }
} finally {
    if ($started) {
        try {
            & docker rm --force $name | Out-Null
            $report.cleanupExitCode = $LASTEXITCODE
            if ($LASTEXITCODE -ne 0) { $code = 1; $report.status = 'FAIL' }
        } catch { $code = 1; $report.cleanupExitCode = 1; $report.status = 'FAIL' }
    }
    if ($code -eq 0) { $report.status = 'PASS' }
    $report.exitCode = $code
    $report.completedUtc = [DateTimeOffset]::UtcNow
    $temporary = 'artifacts/verification/m3-poc-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $report | ConvertTo-Json -Depth 8 | Set-Content $temporary -Encoding utf8
        Move-Item $temporary artifacts/verification/m3-poc.json -Force
    } catch { $code = 1; Remove-Item artifacts/verification/m3-poc.json -Force -ErrorAction SilentlyContinue }
    $env:MSSQL_SA_PASSWORD = $previousSa
    $env:SAAS_M1_ADMIN_CONNECTION = $previousConnection
    Pop-Location
}
exit $code
