[CmdletBinding()]
param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$ManifestSha256)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/m6-common.ps1"
. "$PSScriptRoot/m3-evidence-inventory.ps1"
$root = Split-Path $PSScriptRoot -Parent
$image = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
$name = 'saas-m6-' + [guid]::NewGuid().ToString('N')
$backupRoot = Join-Path ([IO.Path]::GetTempPath()) $name
$previous = @{}
$required = @(Get-Content "$PSScriptRoot/m6-mandatory-cases.json" -Raw | ConvertFrom-Json)
$report = [ordered]@{ scope = 'M6 real SQL release and restore'; status = 'FAIL'; sourceSha = $null; image = $image; digest = $null; testExitCode = $null; cleanupExitCode = $null; mandatoryCases = @(); backupInventory = @() }
$started = $false; $runAttempted = $false; $proof = $null; $code = 1
Push-Location $root
try {
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' })) {
        $previous[$item.Name] = $item.Value
        [Environment]::SetEnvironmentVariable($item.Name, $null)
    }
    $report.sourceSha = Get-M6Sha
    $proof = Open-M6Package $Package $ManifestSha256
    if ($proof.manifest.sourceSha -cne $report.sourceSha) { throw 'package_source_mismatch' }
    if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED:docker_missing' }
    $results = Join-Path $root 'artifacts/test-results/m6-poc'
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item $results -ItemType Directory -Force | Out-Null
    New-Item $backupRoot -ItemType Directory | Out-Null
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($backupRoot, [IO.UnixFileMode]'UserRead,UserWrite,UserExecute') }
    $env:MSSQL_SA_PASSWORD = 'Aa1!' + [guid]::NewGuid().ToString('N')
    $env:SAAS_BACKUP_ROOT = $backupRoot; $env:SAAS_SQL_BACKUP_ROOT = '/saas-backups'
    $env:SAAS_BACKUP_STORE_ACK = 'private-shared-store-v1'
    $env:SAAS_BACKUP_MANIFEST_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $env:SAAS_M6_RELEASE_PACKAGE = $proof.root; $env:SAAS_RELEASE_MANIFEST_SHA256 = $ManifestSha256
    Invoke-M6Checked docker @('pull',$image)
    $report.digest = (& docker image inspect $image --format '{{index .RepoDigests 0}}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($report.digest)) { throw 'image_identity_missing' }
    # Root is confined to this disposable fixture so a private host-owned bind mount works on Linux/Windows.
    # Never use this account model or these generated secrets in a production deployment.
    $runAttempted = $true
    & docker run --detach --name $name --user '0:0' --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD --publish '127.0.0.1::1433' --mount "type=bind,source=$backupRoot,target=/saas-backups" $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sql_fixture_start_failed' }
    $started = $true; $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        "IF CONVERT(varchar(32), SERVERPROPERTY('ProductVersion')) <> '16.0.4305.5' THROW 51000, 'Wrong SQL patch', 1; SELECT 1;" | & docker exec -i $name /bin/sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }; Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'BLOCKED:sql_fixture_not_ready' }
    $binding = (& docker port $name 1433/tcp | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'sql_fixture_invalid_binding' }
    $env:SAAS_M1_ADMIN_CONNECTION = "Server=127.0.0.1,$($Matches[1]);Initial Catalog=master;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;Pooling=False"
    & dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlM6' --logger trx --results-directory $results
    $report.testExitCode = $LASTEXITCODE
    $files = @(Get-ChildItem $results -Filter *.trx)
    if ($files.Count -ne 1) { throw 'exactly_one_fresh_m6_trx_required' }
    $trx = Read-M6Trx $files[0].FullName
    $inventory = Get-M3EvidenceInventory -Results $trx.results -Required $required
    $report.mandatoryCases = $inventory.mandatoryCases
    $report.missingMandatoryCases = $inventory.missingMandatoryCases; $report.unexpectedCases = $inventory.unexpectedCases
    $report.total = $trx.total; $report.passed = $trx.passed; $report.nonPassed = $trx.nonPassed
    if ($report.testExitCode -ne 0 -or $trx.nonPassed -ne 0 -or -not $inventory.valid) { throw 'm6_mandatory_evidence_failed' }
    $code = 0
} catch {
    $report.error = Protect-M6Text $_.Exception.Message
    if ($report.error -like 'BLOCKED:*') { $report.status = 'BLOCKED'; $code = 2 }
    Write-Host $report.error
} finally {
    try {
        if (Test-Path $backupRoot) {
            $report.backupInventory = @(Get-ChildItem $backupRoot -Recurse -Filter manifest.json -File | ForEach-Object {
                $document = Get-Content $_.FullName -Raw | ConvertFrom-Json -Depth 20
                [ordered]@{ manifestSha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash; manifest = $document.manifest }
            })
        }
    } catch { $report.backupInventory = @(); $report.inventoryError = 'backup_inventory_unreadable'; $code = 1 }
    if ($runAttempted -and -not $started) {
        & docker container inspect $name --format '{{.Id}}' 2>$null | Out-Null
        $started = $LASTEXITCODE -eq 0
    }
    if ($started) {
        try {
            & docker rm --force $name | Out-Null; $report.cleanupExitCode = $LASTEXITCODE
            if ($LASTEXITCODE -ne 0) { $code = 1; $report.status = 'FAIL' }
        } catch { $code = 1; $report.status = 'FAIL'; $report.cleanupExitCode = 1 }
    }
    Close-M6Package $proof
    try { if (Test-Path $backupRoot) { Remove-Item $backupRoot -Recurse -Force } }
    catch { $code = 1; $report.status = 'FAIL'; $report.cleanupError = 'private_backup_cleanup_failed' }
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' })) { [Environment]::SetEnvironmentVariable($item.Name,$null) }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key]) }
    if ($code -eq 0) { $report.status = 'PASS' }
    $report.exitCode = $code; $report.completedUtc = [DateTimeOffset]::UtcNow
    Write-M6Json (Join-Path $root 'artifacts/verification/m6-poc.json') $report
    Pop-Location
}
exit $code
