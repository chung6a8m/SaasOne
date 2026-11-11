# Only a fresh, disposable and pinned SQL Server is accepted. No external DB target.
[CmdletBinding()]
param([ValidateSet('Baseline','Candidate','Acceptance')][string]$Mode = 'Baseline', [switch]$Issue15Poc)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'sql-image.ps1')
. (Join-Path $PSScriptRoot 'm6-common.ps1')
. (Join-Path $PSScriptRoot 'm3-evidence-inventory.ps1')
$root = Split-Path $PSScriptRoot -Parent
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m7-' + [Guid]::NewGuid().ToString('N')
$previous = @{}
Get-ChildItem Env: | Where-Object { $_.Name -like 'SAAS_*' -or $_.Name -like 'MSSQL_*' } | ForEach-Object { $previous[$_.Name] = $_.Value }
$started = $false
$code = 1
$required = @(Get-Content (Join-Path $PSScriptRoot 'm7-mandatory-cases.json') -Raw | ConvertFrom-Json)
$report = [ordered]@{
    schemaVersion=1; scope='M7 four-tenant real SQL and HTTPS'; mode=$Mode; acceptance='NOT_ASSESSED'; status='FAIL'; sourceSha=$null
    image=$image; sqlVersion='16.0.4305.5'; digest=$null; testExitCode=$null; cleanupExitCode=$null
    command='dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter Category=SqlM7 --logger trx --results-directory artifacts/test-results/m7'
    total=0; passed=0; nonPassed=0; mandatoryCases=@(); missingMandatoryCases=@($required); unexpectedCases=@()
}
Push-Location $root
try {
    Get-ChildItem Env: | Where-Object { $_.Name -like 'SAAS_*' -or $_.Name -like 'MSSQL_*' } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    $out = Join-Path $root 'artifacts/m7/evidence'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    [void](New-Item $out -ItemType Directory -Force)
    if (Test-Path artifacts/test-results/m7) { Remove-Item artifacts/test-results/m7 -Recurse -Force }
    [void](New-Item artifacts/test-results/m7 -ItemType Directory -Force)
    $report.sourceSha = Get-M6Sha
    $report.environment = [ordered]@{
        sdk=(& dotnet --version | Out-String).Trim(); powershell=$PSVersionTable.PSVersion.ToString()
        os=[Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        logicalProcessors=[Environment]::ProcessorCount; runnerImage=$env:ImageVersion; runnerOs=$env:RUNNER_OS
    }
    if (Test-Path /proc/meminfo) { $report.environment.memory = @(Get-Content /proc/meminfo | Where-Object { $_ -match '^MemTotal:' }) }
    $product = Join-Path $root 'artifacts/m7/product/publish/SaaS.Portal.dll'
    if (-not (Test-Path $product)) { throw 'BLOCKED: run check-m7-product.ps1 first.' }
    if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED: Docker is required.' }
    $env:MSSQL_SA_PASSWORD = 'Aa1!' + [Guid]::NewGuid().ToString('N')
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:MSSQL_SA_PASSWORD" }
    $report.digest = Get-SaasPinnedSqlImageDigest $image
    & docker run --detach --name $name --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD --publish '127.0.0.1::1433' $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SQL container failed to start.' }
    $started = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        "IF CONVERT(varchar(32), SERVERPROPERTY('ProductVersion')) <> '16.0.4305.5' THROW 51000, 'Wrong SQL patch', 1; SELECT 1;" | & docker exec -i $name /bin/sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'BLOCKED: pinned SQL patch did not become ready.' }
    $binding = (& docker port $name 1433/tcp | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Invalid loopback SQL binding.' }
    $env:SAAS_M1_ADMIN_CONNECTION = "Server=127.0.0.1,$($Matches[1]);Initial Catalog=master;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;Pooling=False"
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::add-mask::$env:SAAS_M1_ADMIN_CONNECTION" }
    $env:SAAS_M7_SOURCE_SHA = $report.sourceSha
    $env:SAAS_M7_EVIDENCE_DIRECTORY = $out
    $env:SAAS_M7_PRODUCT_DLL = $product
    $env:SAAS_M7_LOAD_CONFIG = Join-Path $root 'tests/Load/m7-baseline.json'
    $env:SAAS_M7_MODE = $Mode
    $rawLog = Join-Path $root 'artifacts/test-results/m7/test.raw.log'
    & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlM7' --logger trx --results-directory artifacts/test-results/m7 *> $rawLog
    $report.testExitCode = $LASTEXITCODE
    $safeLog = Protect-M6Text ([IO.File]::ReadAllText($rawLog))
    [IO.File]::WriteAllText((Join-Path $out 'test.log'), $safeLog)
    Write-Host $safeLog
    $files = @(Get-ChildItem artifacts/test-results/m7 -Filter *.trx -File)
    if ($files.Count -ne 1) { throw 'Exactly one fresh M7 TRX is required.' }
    $trx = Read-M6Trx $files[0].FullName
    [IO.File]::WriteAllText((Join-Path $out 'tests.trx'), (Protect-M6Text ([IO.File]::ReadAllText($files[0].FullName))))
    $report.total=$trx.total; $report.passed=$trx.passed; $report.nonPassed=$trx.nonPassed
    $inventory = Get-M3EvidenceInventory -Results $trx.results -Required $required
    $report.mandatoryCases=$inventory.mandatoryCases; $report.missingMandatoryCases=$inventory.missingMandatoryCases; $report.unexpectedCases=$inventory.unexpectedCases
    if ($report.testExitCode -ne 0 -or $trx.nonPassed -ne 0 -or -not $inventory.valid) { throw 'Every mandatory M7 case must execute exactly once and pass.' }
    foreach ($file in @('matrix.json','product-runtime.json','load.json','load-samples.json')) {
        if (-not (Test-Path (Join-Path $out $file))) { throw "Missing mandatory M7 report: $file" }
        $value = Get-Content (Join-Path $out $file) -Raw | ConvertFrom-Json
        if ($value.sourceSha -cne $report.sourceSha) { throw 'M7 source evidence mismatch.' }
    }

    if ($Issue15Poc) {
        # Supplemental POC is intentionally NOT part of Category=SqlM7:
        # the unchanged seven-case manifest is checked above, exactly.
        $pocDirectory = Join-Path $root 'artifacts/test-results/issue15'
        if (Test-Path $pocDirectory) { Remove-Item $pocDirectory -Recurse -Force }
        [void](New-Item $pocDirectory -ItemType Directory -Force)
        $pocRawLog = Join-Path $pocDirectory 'test.raw.log'
        & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlIssue15' --logger trx --results-directory $pocDirectory *> $pocRawLog
        $pocExitCode = $LASTEXITCODE
        $pocSafeLog = Protect-M6Text ([IO.File]::ReadAllText($pocRawLog))
        [IO.File]::WriteAllText((Join-Path $out 'issue15-test.log'), $pocSafeLog)
        Write-Host $pocSafeLog
        $pocFiles = @(Get-ChildItem $pocDirectory -Filter *.trx -File)
        if ($pocFiles.Count -ne 1) { throw 'Issue15 POC requires exactly one fresh TRX.' }
        $pocTrx = Read-M6Trx $pocFiles[0].FullName
        [IO.File]::WriteAllText((Join-Path $out 'issue15-tests.trx'),
            (Protect-M6Text ([IO.File]::ReadAllText($pocFiles[0].FullName))))
        $report.issue15Poc = [ordered]@{
            sourceSha=$report.sourceSha; category='SqlIssue15'; total=$pocTrx.total
            passed=$pocTrx.passed; nonPassed=$pocTrx.nonPassed; exitCode=$pocExitCode
        }
        if ($pocExitCode -ne 0 -or $pocTrx.total -ne 1 -or $pocTrx.passed -ne 1 -or $pocTrx.nonPassed -ne 0) {
            throw 'Issue15 physical-open and fresh-authority SQL POC failed.'
        }
    }
    $code=0
} catch {
    Write-Host (Protect-M6Text ('FAIL: ' + $_.Exception.Message))
    if ($_.Exception.Message -like 'BLOCKED:*') { $report.status='BLOCKED' }
} finally {
    if ($started) {
        & docker rm --force $name | Out-Null
        $report.cleanupExitCode=$LASTEXITCODE
        if ($LASTEXITCODE -ne 0) { $code=1 }
    }
    if ($code -eq 0) { $report.status='PASS' }
    $report.exitCode=$code; $report.completedUtc=[DateTimeOffset]::UtcNow
    try { Write-M6Json -Path (Join-Path $root 'artifacts/m7/evidence/sql.json') -Value $report }
    catch { $code=1 }
    Get-ChildItem Env: | Where-Object { $_.Name -like 'SAAS_*' -or $_.Name -like 'MSSQL_*' } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    Pop-Location
}
exit $code
