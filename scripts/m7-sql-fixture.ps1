[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Product','Workload')][string]$Suite,
      [ValidateSet('Baseline','Acceptance')][string]$Mode = 'Baseline', [string]$ProductDll)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/sql-image.ps1"
. "$PSScriptRoot/m6-common.ps1"
. "$PSScriptRoot/m3-evidence-inventory.ps1"
$root = Split-Path $PSScriptRoot -Parent
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m7-' + [guid]::NewGuid().ToString('N')
$previous = @{}
$label = $Suite.ToLowerInvariant()
$results = Join-Path $root "artifacts/test-results/m7-$label"
$export = Join-Path $root 'artifacts/m7-evidence'
$required = @(Get-Content "$PSScriptRoot/m7-$label-mandatory-cases.json" -Raw | ConvertFrom-Json)
$report = [ordered]@{ scope = "M7 $Suite"; mode = $Mode; status = 'FAIL'; sourceSha = $null; sourceTree = $null; image = $image; digest = $null; testExitCode = $null; cleanupExitCode = $null; total = 0; executed = 0; passed = 0; failed = 0; skipped = 0; mandatoryCases = @(); missingMandatoryCases = $required }
$started = $false; $attempted = $false; $code = 1
Push-Location $root
try {
    $report.sourceSha = Get-M6Sha
    $report.sourceTree = (& git rev-parse 'HEAD^{tree}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $report.sourceTree -cnotmatch '^[a-f0-9]{40}$') { throw 'source_tree_unknown' }
    if ((& git status --porcelain | Out-String).Trim()) { throw 'clean_checkout_required' }
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' })) {
        $previous[$item.Name] = $item.Value; [Environment]::SetEnvironmentVariable($item.Name, $null)
    }
    if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED:docker_required' }
    if ($Suite -eq 'Product' -and ([string]::IsNullOrWhiteSpace($ProductDll) -or -not (Test-Path -LiteralPath $ProductDll))) { throw 'BLOCKED:published_product_required' }
    if ($Suite -eq 'Workload' -and $Mode -eq 'Acceptance') {
        $targetPath = Join-Path $root 'tests/Load/m7-targets.json'
        if (-not (Test-Path $targetPath)) { throw 'BLOCKED:committed_baseline_targets_required' }
        $target = Get-Content $targetPath -Raw | ConvertFrom-Json
        & git merge-base --is-ancestor $target.baselineSource $report.sourceSha
        if ($LASTEXITCODE -ne 0) { throw 'baseline_source_must_be_an_ancestor' }
    }
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item $results -ItemType Directory -Force | Out-Null
    New-Item $export -ItemType Directory -Force | Out-Null
    foreach ($suffix in @('json','trx','log')) { Remove-Item "$export/$label.$suffix" -Force -ErrorAction SilentlyContinue }
    if ($Suite -eq 'Workload') { Remove-Item "$export/load.json" -Force -ErrorAction SilentlyContinue }
    $paths = @(& git ls-files -- '*.cs' '*.csproj' '*.slnx' '**/packages.lock.json' 'Directory.*' 'global.json' 'tests/Load/m7-load.json' 'scripts/m7*.ps1')
    if ($LASTEXITCODE -ne 0 -or $paths.Count -lt 1) { throw 'source_input_inventory_missing' }
    $report.sourceInputs = @($paths | Sort-Object | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $canonical = ($report.sourceInputs | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
    $report.sourceDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    $env:SAAS_M7_SOURCE_SHA = $report.sourceSha
    $env:SAAS_M7_SOURCE_DIGEST = $report.sourceDigest
    $env:SAAS_M7_MODE = $Mode
    $env:SAAS_M7_OUTPUT = Join-Path $results 'load.json'
    $env:SAAS_M7_PRODUCT_DLL = $ProductDll
    $env:MSSQL_SA_PASSWORD = 'Aa1!' + [guid]::NewGuid().ToString('N')
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:MSSQL_SA_PASSWORD" }
    $report.digest = Get-SaasPinnedSqlImageDigest $image
    $attempted = $true
    & docker run --detach --name $name --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD --publish '127.0.0.1::1433' $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sql_fixture_start_failed' }
    $started = $true; $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        "IF CONVERT(varchar(32), SERVERPROPERTY('ProductVersion')) <> '16.0.4305.5' THROW 51000, 'Wrong SQL patch', 1; SELECT 1;" | & docker exec -i $name /bin/sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }; Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'BLOCKED:required_sql_patch_not_ready' }
    $binding = (& docker port $name 1433/tcp | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'sql_fixture_invalid_binding' }
    $env:SAAS_M1_ADMIN_CONNECTION = "Server=127.0.0.1,$($Matches[1]);Initial Catalog=master;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;Pooling=False"
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:SAAS_M1_ADMIN_CONNECTION" }
    $filter = "Category=SqlM7&M7Suite=$Suite"
    $report.command = "dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj -c Release --no-build --filter $filter --logger trx"
    & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj -c Release --no-build --filter $filter --logger trx --results-directory $results *> "$results/runner.log"
    $report.testExitCode = $LASTEXITCODE
    $files = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -File)
    if ($files.Count -ne 1) { throw 'exactly_one_fresh_m7_trx_required' }
    $trx = Read-M6Trx $files[0].FullName
    $inventory = Get-M3EvidenceInventory -Results $trx.results -Required $required
    $report.total = $trx.total; $report.executed = $trx.total; $report.passed = $trx.passed; $report.failed = $trx.nonPassed
    $report.mandatoryCases = $inventory.mandatoryCases
    $report.missingMandatoryCases = $inventory.missingMandatoryCases; $report.unexpectedCases = $inventory.unexpectedCases
    # Preserve names/counters; only free-form output is redacted, so evidence remains machine-readable.
    foreach ($node in $trx.xml.SelectNodes('//*[local-name()="Message" or local-name()="StackTrace" or local-name()="StdOut" or local-name()="StdErr"]')) { $node.InnerText = Protect-M6Text $node.InnerText }
    $trx.xml.Save("$export/$label.trx")
    if ($report.testExitCode -ne 0 -or $trx.nonPassed -ne 0 -or -not $inventory.valid) { throw 'm7_mandatory_evidence_failed' }
    if ($Suite -eq 'Workload') {
        if (-not (Test-Path $env:SAAS_M7_OUTPUT)) { throw 'workload_metrics_missing' }
        $load = Get-Content $env:SAAS_M7_OUTPUT -Raw | ConvertFrom-Json -Depth 32
        if ($load.sourceSha -cne $report.sourceSha -or $load.sourceDigest -cne $report.sourceDigest -or $load.mode -cne $Mode) { throw 'workload_metrics_source_mismatch' }
        Copy-Item $env:SAAS_M7_OUTPUT "$export/load.json"
    }
    if ((& git status --porcelain | Out-String).Trim()) { throw 'verification_changed_source' }
    $code = 0
} catch {
    $report.error = Protect-M6Text $_.Exception.Message
    if ($report.error -like 'BLOCKED:*') { $report.status = 'BLOCKED'; $code = 2 }
    Write-Host $report.error
} finally {
    if ($attempted -and -not $started) { & docker container inspect $name --format '{{.Id}}' 2>$null | Out-Null; $started = $LASTEXITCODE -eq 0 }
    if ($started) {
        try { & docker rm --force $name | Out-Null; $report.cleanupExitCode = $LASTEXITCODE; if ($LASTEXITCODE -ne 0) { $code = 1; $report.status = 'FAIL' } }
        catch { $code = 1; $report.cleanupExitCode = 1; $report.status = 'FAIL' }
    }
    if (Test-Path "$results/runner.log") {
        New-Item $export -ItemType Directory -Force | Out-Null
        Protect-M6Text (Get-Content "$results/runner.log" -Raw) | Set-Content "$export/$label.log" -Encoding utf8NoBOM
    }
    if ($Suite -eq 'Workload' -and (Test-Path "$results/load.json")) { Copy-Item "$results/load.json" "$export/load.json" -Force }
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' })) { [Environment]::SetEnvironmentVariable($item.Name, $null) }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    if ($code -eq 0) { $report.status = if ($Suite -eq 'Workload' -and $Mode -eq 'Baseline') { 'BASELINE_RECORDED' } else { 'PASS' } }
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    Write-M6Json "$export/$label.json" $report
    Pop-Location
}
exit $code
