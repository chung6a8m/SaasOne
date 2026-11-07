# One local entry point. Only this run's allowlisted, redacted evidence enters the ZIP.
[CmdletBinding()]
param([ValidateSet('Full', 'Components')][string]$Scope = 'Full')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'm6-common.ps1')
$root = Split-Path $PSScriptRoot -Parent
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N')
$run = Join-Path $root "artifacts/local-verify/$runId"
[IO.Directory]::CreateDirectory($run) | Out-Null
if (-not $IsWindows) { [IO.File]::SetUnixFileMode($run, [IO.UnixFileMode]448) }
$commands = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[string]]::new()
$suites = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ formatVersion = 1; runId = $runId; scope = $Scope; status = 'FAIL'; sourceSha = $null; sourceShaAfter = $null
    startedUtc = [DateTimeOffset]::UtcNow; completedUtc = $null; sourceClean = $false; dirtyPaths = @(); tools = [ordered]@{}
    commands = $commands; suites = $suites; issues = $issues; ownerReview = 'PENDING'; runtimeAcceptance = 'NOT_PROVEN'; exitCode = 1 }
$code = 1; $lock = $null; $fresh = $false
$previous = @{}
# Known caller-provided secret values are removed in addition to pattern redaction; never serialize this map.
$knownSecrets = @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' -and $_.Value.Length -ge 8 } | ForEach-Object Value)
function Protect-LocalText([string]$Text) {
    foreach ($value in $knownSecrets) { $Text = $Text.Replace($value, '[REDACTED_CONFIG]') }
    Protect-M6Text $Text
}
function Invoke-LocalGate([string]$Name, [string[]]$Arguments) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $path = Join-Path $run "$Name.log"
    $writer = [IO.StreamWriter]::new($path, $false, [Text.UTF8Encoding]::new($false))
    $native = 1
    try {
        & pwsh -NoProfile -File @Arguments 2>&1 | ForEach-Object {
            $line = Protect-LocalText ([string]$_)
            $writer.WriteLine($line); $writer.Flush(); Write-Host $line
        }
        $native = $LASTEXITCODE
    } finally {
        $writer.Dispose(); $timer.Stop()
        $commands.Add([ordered]@{ name = $Name; executable = 'pwsh'; arguments = $Arguments; exitCode = $native; durationMs = $timer.ElapsedMilliseconds })
    }
    if ($native -ne 0) { throw "gate_failed:$Name`:$native" }
}
function Copy-SafeEvidence([string]$Source, [string]$Relative) {
    if ((Get-Item -LiteralPath $Source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'evidence_link_forbidden' }
    $destination = Join-Path $run $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    # JSON redaction preserves structure by operating only on string values through a recursive walk.
    if ([IO.Path]::GetExtension($Source) -eq '.json') {
        function Protect-Value($Value) {
            if ($null -eq $Value) { return $null }
            if ($Value -is [string]) { return (Protect-LocalText $Value) }
            if ($Value -is [Collections.IDictionary]) {
                $safe = [ordered]@{}
                foreach ($key in $Value.Keys) {
                    if ($key -match '(?i)^(password|pwd|connectionString|aclJson|approvedAcl|authentication|privateKey|accessToken|refreshToken|idToken)$') { $safe[$key] = '[REDACTED]' }
                    else { $safe[$key] = Protect-Value $Value[$key] }
                }
                return $safe
            }
            if ($Value -is [Collections.IEnumerable]) { return ,@($Value | ForEach-Object { Protect-Value $_ }) }
            return $Value
        }
        Write-M6Json $destination (Protect-Value (Get-Content -LiteralPath $Source -Raw | ConvertFrom-Json -AsHashtable -Depth 64))
    } elseif ([IO.Path]::GetExtension($Source) -eq '.trx') {
        $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($Source, $settings)
        try { $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
        foreach ($node in $xml.SelectNodes('//*[local-name()="Message" or local-name()="StackTrace" or local-name()="StdOut" or local-name()="StdErr"]')) {
            $node.InnerText = Protect-LocalText $node.InnerText
        }
        # Do not change testcase names/outcomes/counters: evidence validation depends on exact values.
        $xml.Save($destination)
        $results = @($xml.SelectNodes('//*[local-name()="UnitTestResult"]'))
        $passed = @($results | Where-Object outcome -CEQ 'Passed').Count
        $suites.Add([ordered]@{ file = $Relative; total = $results.Count; passed = $passed; nonPassed = $results.Count - $passed })
    } else {
        [IO.File]::WriteAllText($destination, (Protect-LocalText (Get-Content -LiteralPath $Source -Raw)), [Text.UTF8Encoding]::new($false))
    }
}
Push-Location $root
try {
    $lockPath = Join-Path $root 'artifacts/local-verification.lock'
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    foreach ($folder in @('verification', 'test-results')) {
        $path = Join-Path $root "artifacts/$folder"
        if (Test-Path -LiteralPath $path) {
            $archive = Join-Path $root "artifacts/local-archive/$runId/$folder"
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archive)) | Out-Null
            Move-Item -LiteralPath $path -Destination $archive
        }
    }
    $fresh = $true
    foreach ($tool in @('git', 'dotnet')) {
        if (-not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) { throw "BLOCKED:missing_$tool" }
    }
    $report.sourceSha = Get-M6Sha
    $dirty = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'source_status_failed' }
    $report.dirtyPaths = @($dirty | ForEach-Object { Protect-LocalText ([string]$_) })
    $report.sourceClean = $dirty.Count -eq 0
    if (-not $report.sourceClean) { throw 'BLOCKED:clean_checkout_required' }
    $pin = Get-Content global.json -Raw | ConvertFrom-Json
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED:dotnet_sdk_unavailable' }
    $report.tools = [ordered]@{ sdk = $sdk; powershell = $PSVersionTable.PSVersion.ToString()
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() }
    if ($sdk -ne $pin.sdk.version -or $pin.sdk.rollForward -ne 'disable' -or $pin.sdk.allowPrerelease) { throw 'BLOCKED:sdk_pin_mismatch' }
    if ($PSVersionTable.PSVersion.ToString() -ne '7.6.9') { throw 'BLOCKED:powershell_7.6.9_required' }
    if ($report.tools.architecture -ne 'X64' -or -not ($IsWindows -or $IsLinux)) { throw 'BLOCKED:windows_or_linux_x64_required' }
    if ($Scope -eq 'Full') {
        if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) { throw 'BLOCKED:docker_required' }
        $dockerVersion = (& docker version --format '{{.Server.Version}}' 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dockerVersion)) { throw 'BLOCKED:docker_server_unavailable' }
        $report.tools.dockerServer = $dockerVersion
    }
    foreach ($item in @(Get-ChildItem Env: | Where-Object Name -Match '^(SAAS_|MSSQL_)')) {
        $previous[$item.Name] = $item.Value; [Environment]::SetEnvironmentVariable($item.Name, $null)
    }
    if ($Scope -eq 'Full') {
        Invoke-LocalGate 'm6-full' @('scripts/check-m6.ps1')
        $acceptance = Get-Content artifacts/verification/m6-acceptance.json -Raw | ConvertFrom-Json
        if ($acceptance.sourceSha -cne $report.sourceSha -or $acceptance.status -cne 'PASS') { throw 'acceptance_evidence_invalid' }
        $report.status = 'PASS'; $report.runtimeAcceptance = 'AUTOMATED_GATES_PASSED_OWNER_REVIEW_PENDING'
    } else {
        Invoke-LocalGate 'components' @('scripts/check.ps1', '-Scope', 'M0')
        $report.status = 'PASS_COMPONENTS_NOT_ACCEPTANCE'; $report.runtimeAcceptance = 'NOT_PROVEN'
    }
    $code = 0
} catch {
    $safe = Protect-LocalText $_.Exception.Message; $issues.Add($safe)
    if ($safe.StartsWith('BLOCKED:')) { $report.status = 'BLOCKED'; $code = 2 } else { $report.status = 'FAIL'; $code = 1 }
    Write-Host $safe
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    try {
        if ($fresh) {
            foreach ($file in @(Get-ChildItem artifacts/verification -Filter *.json -File -ErrorAction SilentlyContinue)) {
                Copy-SafeEvidence $file.FullName ('verification/' + $file.Name)
            }
            foreach ($file in @(Get-ChildItem artifacts/test-results -Filter *.trx -File -Recurse -ErrorAction SilentlyContinue)) {
                Copy-SafeEvidence $file.FullName ('test-results/' + [IO.Path]::GetRelativePath((Join-Path $root 'artifacts/test-results'), $file.FullName))
            }
            $buildReport = Join-Path $root 'artifacts/verification/m6-release-build.json'
            if (Test-Path -LiteralPath $buildReport) {
                $built = Get-Content -LiteralPath $buildReport -Raw | ConvertFrom-Json
                if ($built.status -eq 'PASS' -and $built.sourceSha -ceq $report.sourceSha) {
                    $package = $null
                    try {
                        $package = Open-M6Package $built.package $built.manifestSha256
                        Copy-SafeEvidence (Join-Path $package.root 'release.json') 'release/release.json'
                        foreach ($entry in @($package.manifest.files | Where-Object { $_.path -eq 'catalog.json' -or $_.path -match '^sql/[a-z]+\.sql
                            Copy-SafeEvidence (Resolve-M6Artifact $package.root $entry.path) ('release/' + $entry.path)
                        }
                    } finally { Close-M6Package $package }
                }
            }
        }
        if (Get-Command git -CommandType Application -ErrorAction SilentlyContinue) {
            $report.sourceShaAfter = Get-M6Sha
            $after = @(& git status --porcelain=v1 --untracked-files=all)
            if ($LASTEXITCODE -ne 0 -or $after.Count -gt 0 -or ($null -ne $report.sourceSha -and $report.sourceShaAfter -cne $report.sourceSha)) {
                $issues.Add('source_changed_or_dirty'); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
            }
        }
        $invalidSuites = @($suites | Where-Object { $_.total -le 0 -or $_.nonPassed -ne 0 })
        if ($code -eq 0 -and $suites.Count -eq 0) {
            $issues.Add('test_evidence_zero_or_nonpassing'); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
        }
        if ($code -eq 0 -and $invalidSuites.Count -gt 0) {
            $issues.Add('test_evidence_zero_or_nonpassing'); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
        }
    } catch {
        $issues.Add('evidence_collection_failed:' + (Protect-LocalText $_.Exception.Message)); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
    }
    $report.completedUtc = [DateTimeOffset]::UtcNow; $report.exitCode = $code
    try {
        Write-M6Json (Join-Path $run 'summary.json') $report
        $summary = "# Local verification $runId`n`nStatus: $($report.status)`nScope: $Scope`nSource: $($report.sourceSha)`nRuntime: $($report.runtimeAcceptance)`nOwner review: PENDING`nExit: $code`n`n"
        $summary += "| Suite | Total | Passed | Other |`n|---|---:|---:|---:|`n"
        foreach ($suite in $suites) { $summary += "| $($suite.file) | $($suite.total) | $($suite.passed) | $($suite.nonPassed) |`n" }
        $summary += "`n## Issues`n" + ($issues -join "`n") + "`n`nThe ZIP excludes backups, credentials, App_Data, environment dumps and binaries. Review redacted logs before sharing. Release binaries remain local under artifacts/releases.`n"
        [IO.File]::WriteAllText((Join-Path $run 'SUMMARY.md'), $summary, [Text.UTF8Encoding]::new($false))
        $inventory = @(Get-ChildItem -LiteralPath $run -Recurse -File | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($run, $_.FullName).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        Write-M6Json (Join-Path $run 'checksums.json') $inventory
        $zip = $run + '.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($run, $zip)
        [IO.File]::WriteAllText($zip + '.sha256', (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + "`n")
        Write-Host "Evidence: $zip"; Write-Host "Summary: $(Join-Path $run 'SUMMARY.md')"
    } catch { $code = 1; Write-Host ('evidence_package_failed:' + $_.Exception.GetType().Name) }
    if ($null -ne $lock) { $lock.Dispose() }
    Pop-Location
}
exit $code
 })) {
                            Copy-SafeEvidence (Resolve-M6Artifact $package.root $entry.path) ('release/' + $entry.path)
                        }
                    } finally { Close-M6Package $package }
                }
            }
        }
        if (Get-Command git -CommandType Application -ErrorAction SilentlyContinue) {
            $report.sourceShaAfter = Get-M6Sha
            $after = @(& git status --porcelain=v1 --untracked-files=all)
            if ($LASTEXITCODE -ne 0 -or $after.Count -gt 0 -or ($null -ne $report.sourceSha -and $report.sourceShaAfter -cne $report.sourceSha)) {
                $issues.Add('source_changed_or_dirty'); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
            }
        }
        if ($code -eq 0 -and ($suites.Count -eq 0 -or @($suites | Where-Object { $_.total -le 0 -or $_.nonPassed -ne 0 }).Count -gt 0)) {
            $issues.Add('test_evidence_zero_or_nonpassing'); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
        }
    } catch {
        $issues.Add('evidence_collection_failed:' + (Protect-LocalText $_.Exception.Message)); $code = 1; $report.status = 'FAIL'; $report.runtimeAcceptance = 'NOT_PROVEN'
    }
    $report.completedUtc = [DateTimeOffset]::UtcNow; $report.exitCode = $code
    try {
        Write-M6Json (Join-Path $run 'summary.json') $report
        $summary = "# Local verification $runId`n`nStatus: $($report.status)`nScope: $Scope`nSource: $($report.sourceSha)`nRuntime: $($report.runtimeAcceptance)`nOwner review: PENDING`nExit: $code`n`n"
        $summary += "| Suite | Total | Passed | Other |`n|---|---:|---:|---:|`n"
        foreach ($suite in $suites) { $summary += "| $($suite.file) | $($suite.total) | $($suite.passed) | $($suite.nonPassed) |`n" }
        $summary += "`n## Issues`n" + ($issues -join "`n") + "`n`nThe ZIP excludes backups, credentials, App_Data, environment dumps and binaries. Review redacted logs before sharing. Release binaries remain local under artifacts/releases.`n"
        [IO.File]::WriteAllText((Join-Path $run 'SUMMARY.md'), $summary, [Text.UTF8Encoding]::new($false))
        $inventory = @(Get-ChildItem -LiteralPath $run -Recurse -File | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($run, $_.FullName).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        Write-M6Json (Join-Path $run 'checksums.json') $inventory
        $zip = $run + '.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($run, $zip)
        [IO.File]::WriteAllText($zip + '.sha256', (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + "`n")
        Write-Host "Evidence: $zip"; Write-Host "Summary: $(Join-Path $run 'SUMMARY.md')"
    } catch { $code = 1; Write-Host ('evidence_package_failed:' + $_.Exception.GetType().Name) }
    if ($null -ne $lock) { $lock.Dispose() }
    Pop-Location
}
exit $code
