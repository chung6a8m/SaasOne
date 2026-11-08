# Disposable SQL Server acceptance gate; never connects to an existing deployment.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'sql-image.ps1')
$root = Split-Path $PSScriptRoot -Parent
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m2-' + [Guid]::NewGuid().ToString('N')
$previousSa = $env:MSSQL_SA_PASSWORD
$previousConnection = $env:SAAS_M1_ADMIN_CONNECTION
$started = $false
$code = 1
$required = @(
    'Authorization_code_entrypoint_challenges_Default_with_Pkce',
    'One_central_login_signs_into_Alpha_and_Beta_with_independent_cookies',
    'Unknown_membership_cannot_authorize_Gamma',
    'Same_email_never_links_different_issuer_subjects',
    'Jwt_format_signature_issuer_audience_scope_and_expiry_are_validated',
    'Alpha_access_token_cannot_call_Beta_even_with_both_memberships',
    'Issuer_store_revocation_blocks_the_next_API_and_web_request',
    'Refresh_is_bound_to_its_tenant_and_rotates_once',
    'Refresh_reuse_revokes_only_its_family_and_related_session',
    'Concurrent_refresh_cannot_leave_two_valid_chains',
    'Lost_refresh_response_then_reuse_requires_new_login',
    'Membership_remove_readd_never_revives_old_credentials',
    'Suspend_reopen_and_expiry_extension_never_revive_old_credentials',
    'Tenant_logout_preserves_other_tenants_and_global_logout_revokes_all',
    'Required_source_failure_returns_dependency_unavailable_without_cached_allow',
    'Restart_preserves_revocation_and_persisted_key_validation',
    'Callback_logout_and_return_urls_are_strictly_allowlisted',
    'Mutation_requires_antiforgery_and_two_tabs_keep_their_context',
    'Native_password_lockout_then_unlock_does_not_revive_prior_grants',
    'Native_access_token_revocation_also_invalidates_its_related_web_session',
    'Native_access_revocation_reaches_web_first_and_survives_restart',
    'Rotated_native_history_detects_old_access_revocation_without_cross_family_effect',
    'Native_access_binding_violations_are_rejected_by_index_only_query',
    'Normal_access_expiry_does_not_shorten_the_independent_web_session',
    'Explicit_clock_is_shared_by_the_authority_in_every_shell',
    'Identity_runtime_cannot_manage_memberships_clients_registry_or_schema',
    'Completed_identity_initialization_is_verify_only_when_native_history_is_missing',
    'Completed_identity_retry_is_read_only_and_does_not_resolve_client_secrets',
    'Product_host_supports_Sso_Api_and_refresh_with_only_runtime_credentials',
    'Product_readiness_and_login_close_when_native_history_is_corrupt',
    'Private_identity_commands_revoke_membership_and_account_without_restoring_old_grants',
    'Product_host_rejects_a_native_callback_that_is_not_in_the_authoritative_registry'
)
$report = [ordered]@{
    scope = 'M2 identity'; status = 'FAIL'; sourceSha = $null; image = $image; digest = $null
    command = 'dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter Category=SqlM2 --logger trx --results-directory artifacts/test-results/m2-poc'
    testExitCode = $null; cleanupExitCode = $null; total = 0; executed = 0; passed = 0; failed = 0; skipped = 0
    mandatoryCases = @($required | ForEach-Object { [ordered]@{ name = $_; discovered = 0; outcome = 'NotExecuted' } })
    discoveredCases = @(); missingMandatoryCases = @($required)
}
Push-Location $root
try {
    New-Item artifacts/verification -ItemType Directory -Force | Out-Null
    Remove-Item artifacts/verification/m2-poc.json -Force -ErrorAction SilentlyContinue
    Remove-Item artifacts/test-results/m2-poc -Recurse -Force -ErrorAction SilentlyContinue
    New-Item artifacts/test-results/m2-poc -ItemType Directory -Force | Out-Null
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
    & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlM2' --logger trx --results-directory artifacts/test-results/m2-poc
    $report.testExitCode = $LASTEXITCODE
    $files = @(Get-ChildItem artifacts/test-results/m2-poc -Filter *.trx)
    if ($files.Count -ne 1) { throw 'Exactly one fresh SQL test evidence file is required.' }
    [xml]$trx = Get-Content -LiteralPath $files[0].FullName -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    foreach ($counter in @('total', 'executed', 'passed', 'failed')) { $report[$counter] = [int]$counters.$counter }
    $report.skipped = $report.total - $report.executed
    $results = @($trx.TestRun.Results.UnitTestResult)
    $report.discoveredCases = @($results | ForEach-Object { [ordered]@{ name = $_.testName; outcome = $_.outcome } })
    $report.mandatoryCases = @($required | ForEach-Object {
        $case = $_
        $found = @($results | Where-Object { $_.testName -ceq ('SaaS.UnitTests.M2IdentityPocTests.' + $case) })
        $outcome = if ($found.Count -eq 0) { 'Missing' } elseif ($found.Count -ne 1) { 'Duplicate' } else { [string]$found[0].outcome }
        [ordered]@{ name = $case; discovered = $found.Count; outcome = $outcome }
    })
    $report.missingMandatoryCases = @($report.mandatoryCases | Where-Object { $_.discovered -eq 0 } | ForEach-Object { $_.name })
    # Record the full missing/failed inventory BEFORE throwing. Passing native-store
    # subtests must never masquerade as passing API/web/PKCE/production acceptance cases.
    if ($report.testExitCode -ne 0 -or $report.total -lt 1 -or $report.executed -ne $report.total -or $report.passed -ne $report.total -or $report.failed -ne 0) {
        throw 'Every discovered M2 identity case must execute and pass.'
    }
    if (@($report.mandatoryCases | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0) {
        throw 'BLOCKED: mandatory end-to-end M2 identity evidence is missing or invalid.'
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
    $temporary = 'artifacts/verification/m2-poc-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $report | ConvertTo-Json -Depth 8 | Set-Content $temporary -Encoding utf8
        Move-Item $temporary artifacts/verification/m2-poc.json -Force
    } catch { $code = 1; Remove-Item artifacts/verification/m2-poc.json -Force -ErrorAction SilentlyContinue }
    $env:MSSQL_SA_PASSWORD = $previousSa
    $env:SAAS_M1_ADMIN_CONNECTION = $previousConnection
    Pop-Location
}
exit $code
