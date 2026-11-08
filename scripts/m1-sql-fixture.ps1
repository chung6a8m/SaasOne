# Disposable SQL Server acceptance gate; never connects to an existing deployment.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'sql-image.ps1')
$root = Split-Path $PSScriptRoot -Parent
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m1-' + [Guid]::NewGuid().ToString('N')
$previousSa = $env:MSSQL_SA_PASSWORD
$previousConnection = $env:SAAS_M1_ADMIN_CONNECTION
$started = $false
$code = 1
$report = [ordered]@{ scope = 'M1 SQL'; status = 'FAIL'; sourceSha = $null; image = $image; digest = $null; testExitCode = $null; cleanupExitCode = $null; total = 0; executed = 0; passed = 0; failed = 0; skipped = 0; mandatoryCases = @() }
Push-Location $root
try {
    New-Item artifacts/verification -ItemType Directory -Force | Out-Null
    Remove-Item artifacts/verification/m1-sql.json -Force -ErrorAction SilentlyContinue
    Remove-Item artifacts/test-results/m1-sql -Recurse -Force -ErrorAction SilentlyContinue
    New-Item artifacts/test-results/m1-sql -ItemType Directory -Force | Out-Null
    $report.sourceSha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $report.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record source identity.' }
    if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED: Docker is required.' }
    $env:MSSQL_SA_PASSWORD = 'Aa1!' + [Guid]::NewGuid().ToString('N')
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:MSSQL_SA_PASSWORD" }
    $report.digest = Get-SaasPinnedSqlImageDigest $image
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
    & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlM1' --logger trx --results-directory artifacts/test-results/m1-sql
    $report.testExitCode = $LASTEXITCODE
    $files = @(Get-ChildItem artifacts/test-results/m1-sql -Filter *.trx)
    if ($files.Count -ne 1) { throw 'Exactly one fresh SQL test evidence file is required.' }
    [xml]$trx = Get-Content $files[0].FullName -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    foreach ($counter in @('total', 'executed', 'passed', 'failed')) { $report[$counter] = [int]$counters.$counter }
    $report.skipped = $report.total - $report.executed
    if ($report.testExitCode -ne 0 -or $report.total -lt 48 -or $report.executed -ne $report.total -or $report.passed -ne $report.total -or $report.failed -ne 0) { throw 'All 48 or more mandatory M1 SQL cases must execute and pass.' }
    $required = @(
        'Four_real_customer_databases_are_provisioned_and_routed_through_Orchard',
        'Concurrent_retry_cannot_take_over_a_live_owner',
        'A_newer_suspension_wins_over_inflight_provision_completion',
        'Audit_failure_cannot_publish_a_successful_checkpoint_and_retry_can_reconcile',
        'A_newly_provisioned_tenant_is_available_without_restarting_the_host',
        'Fresh_runtime_processes_start_with_only_low_privilege_connections',
        'Wrong_database_TenantId_and_nonce_are_rejected_without_overwriting_identity',
        'An_existing_seed_baseline_prevents_retry_from_repairing_missing_native_history',
        'A_still_active_tagged_SQL_owner_and_cross_host_takeover_are_both_rejected',
        'Named_shell_lookup_and_names_do_not_resolve_unrelated_secrets',
        'Invalid_customer_registration_does_not_block_other_shells',
        'Corrupt_platform_baseline_keeps_all_readiness_closed',
        'Late_runtime_secret_requires_a_controlled_process_restart'
    )
    foreach ($case in $required) {
        $matches = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName.EndsWith('.' + $case, [StringComparison]::Ordinal) -and $_.outcome -eq 'Passed' })
        if ($matches.Count -ne 1) { throw "Missing mandatory passing evidence: $case" }
    }
    $isolation = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*.Cold_start_isolates_a_customer_secret_fault(*' })
    if ($isolation.Count -ne 6 -or @($isolation | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) { throw 'All six cold-process secret/lifecycle isolation cases must pass.' }
    $report.mandatoryCases = $required + @($isolation | ForEach-Object { $_.testName })
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
    $temporary = 'artifacts/verification/m1-sql-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $report | ConvertTo-Json -Depth 8 | Set-Content $temporary -Encoding utf8
        Move-Item $temporary artifacts/verification/m1-sql.json -Force
    } catch { $code = 1; Remove-Item artifacts/verification/m1-sql.json -Force -ErrorAction SilentlyContinue }
    $env:MSSQL_SA_PASSWORD = $previousSa
    $env:SAAS_M1_ADMIN_CONNECTION = $previousConnection
    Pop-Location
}
exit $code
