[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm7-evidence-policy.ps1')
. (Join-Path $PSScriptRoot 'm6-common.ps1')
. (Join-Path $PSScriptRoot 'm7-evidence-package.ps1')
$sha='a' * 40
function New-Report {
    return @{
        schemaVersion=1; sourceSha=$sha; status='PASS'; configuredVirtualUsers=100; peakVirtualUsers=100; peakConcurrentRequests=42; elapsedIncludingDrainSeconds=91.0
        config=@{ schemaVersion=1; virtualUsersPerTenant=25; rampSeconds=30; measureSeconds=60 }
        missingCells=@(); unexpectedFailures=@(); integrityErrors=@()
        rows=@(Get-M7Cells | ForEach-Object {
            $parts=$_.Split('/')
            @{tenant=$parts[0];operation=$parts[1];totalRequests=3;measuredRequests=2;measuredFailures=0;allPhaseFailures=0;unexpected5xx=0;transportFailures=0;successfulCompletionsPerSecond=0.03
              latency=@{count=2;p50Ms=10.0;p95Ms=20.0;p99Ms=20.0;maxMs=20.0}}
        })
    }
}
function New-Targets {
    return @{
        schemaVersion=1; loadConfigSha256=('b'*64); reviewedBy='test fixture'; decisionReference='test-only'; maxUnexpectedErrors=0
        baseline=@{sourceSha=$sha;reportSha256=('c'*64);reportPath='tests/Load/baselines/fixture.json';artifactReference='test-only'}
        limits=@(Get-M7Cells | ForEach-Object { $parts=$_.Split('/'); @{tenant=$parts[0];operation=$parts[1];p95Ms=30.0;p99Ms=40.0;minSuccessfulRequestsPerSecond=0.01} })
    }
}
$script:count=0
function Must-Reject([string]$Name, [scriptblock]$Action) {
    $rejected=$false
    try { & $Action } catch {
        if ($_.Exception.Message -notlike 'm7_*') { throw }
        $rejected=$true
    }
    if (-not $rejected) { throw "Evidence policy did not reject: $Name" }
    $script:count++
}
Assert-M7LoadReport (New-Report) $sha
Assert-M7Thresholds (New-Report) (New-Targets)
Assert-M7TargetTiming ([DateTimeOffset]'2026-01-01T00:00:00Z') ([DateTimeOffset]'2026-01-02T00:00:00Z') ([DateTimeOffset]'2026-01-03T00:00:00Z')
Must-Reject 'empty rows' { $r=New-Report; $r.rows=@(); Assert-M7LoadReport $r $sha }
Must-Reject 'missing tenant' { $r=New-Report; $r.rows=@($r.rows | Where-Object tenant -ne delta); Assert-M7LoadReport $r $sha }
Must-Reject 'missing approve' { $r=New-Report; $r.rows=@($r.rows | Where-Object operation -ne approve); Assert-M7LoadReport $r $sha }
Must-Reject 'duplicate cell' { $r=New-Report; $r.rows+=@($r.rows[0]); Assert-M7LoadReport $r $sha }
Must-Reject 'zero discovery equivalent' { $r=New-Report; $r.rows[0].measuredRequests=0; Assert-M7LoadReport $r $sha }
Must-Reject 'sample mismatch' { $r=New-Report; $r.rows[0].latency.count=1; Assert-M7LoadReport $r $sha }
Must-Reject 'hidden ramp error' { $r=New-Report; $r.rows[0].allPhaseFailures=1; Assert-M7LoadReport $r $sha }
Must-Reject 'unexpected server error' { $r=New-Report; $r.rows[0].unexpected5xx=1; Assert-M7LoadReport $r $sha }
Must-Reject 'wrong SHA' { Assert-M7LoadReport (New-Report) ('d'*40) }
Must-Reject 'reduced population' { $r=New-Report; $r.peakVirtualUsers=99; Assert-M7LoadReport $r $sha }
Must-Reject 'missing integrity proof' { $r=New-Report; $r.integrityErrors=@('query-failed'); Assert-M7LoadReport $r $sha }
foreach ($bad in @([double]::NaN,[double]::PositiveInfinity,-1.0)) {
    Must-Reject 'invalid latency' { $r=New-Report; $r.rows[0].latency.p95Ms=$bad; Assert-M7LoadReport $r $sha }
}
Must-Reject 'inverted quantiles' { $r=New-Report; $r.rows[0].latency.p99Ms=1.0; Assert-M7LoadReport $r $sha }
Must-Reject 'missing target cell' { $t=New-Targets; $t.limits=@($t.limits | Where-Object operation -ne approve); Assert-M7Targets $t }
Must-Reject 'unreviewed targets' { $t=New-Targets; $t.reviewedBy=''; Assert-M7Targets $t }
Must-Reject 'post-hoc lock' { Assert-M7TargetTiming ([DateTimeOffset]'2026-01-01T00:00:00Z') ([DateTimeOffset]'2026-01-03T00:00:00Z') ([DateTimeOffset]'2026-01-02T00:00:00Z') }
Must-Reject 'no preceding baseline' { Assert-M7TargetTiming ([DateTimeOffset]'2026-01-02T00:00:00Z') ([DateTimeOffset]'2026-01-01T00:00:00Z') ([DateTimeOffset]'2026-01-03T00:00:00Z') }
Must-Reject 'latency threshold exceeded' { $t=New-Targets; $t.limits[0].p95Ms=15.0; Assert-M7Thresholds (New-Report) $t }
Must-Reject 'throughput threshold missed' { $t=New-Targets; $t.limits[0].minSuccessfulRequestsPerSecond=1.0; Assert-M7Thresholds (New-Report) $t }
Write-Host "PASS: M7 evidence policy ($script:count negative cases; positive report, threshold and timing cases). Synthetic policy inputs are not load evidence."

# Behavioral regression for the PR-only packaging failure: colliding TRX names can
# contain [1], which must not match the different file named result1.trx.
$fixture=[IO.Path]::Combine([IO.Path]::GetTempPath(), 'm7-package-[literal]-'+[guid]::NewGuid().ToString('N'))
$archive=$null
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    $expected=@{}
    foreach ($name in @('result[1].trx','result1.trx','nested[2]/report.json')) {
        $path=[IO.Path]::Combine($fixture,$name)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        $bytes=[Text.Encoding]::UTF8.GetBytes('Synthetic fixture only: '+$name)
        [IO.File]::WriteAllBytes($path,$bytes)
        $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $expected[$name]=$digest
        if ((Get-M7FileDigest $path) -cne $digest) { throw 'm7_literal_path_digest_mismatch' }
    }
    Write-M7EvidencePackage -Root $fixture -SourceSha $sha
    $manifest=Get-Content -LiteralPath (Join-Path $fixture 'hashes.json') -Raw | ConvertFrom-Json
    if ($manifest.files.Count -ne $expected.Count) { throw 'm7_literal_path_inventory_incomplete' }
    foreach ($entry in $manifest.files) {
        if (-not $expected.ContainsKey($entry.path) -or $expected[$entry.path] -cne $entry.sha256) { throw 'm7_literal_path_inventory_wrong_digest' }
    }
    $archive=[IO.Compression.ZipFile]::OpenRead($fixture+'.zip')
    if ($archive.Entries.Count -ne $expected.Count+1) { throw 'm7_literal_path_archive_incomplete' }
    foreach ($name in $expected.Keys) {
        $entry=$archive.GetEntry($name)
        if ($null -eq $entry) { throw 'm7_literal_path_archive_missing_entry' }
        $stream=$entry.Open()
        try { $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -cne $expected[$name]) { throw 'm7_literal_path_archive_wrong_digest' }
    }
    $sidecar=[IO.File]::ReadAllText($fixture+'.zip.sha256')
    if (-not $sidecar.StartsWith((Get-M7FileDigest ($fixture+'.zip'))+'  ',[StringComparison]::Ordinal)) { throw 'm7_literal_path_sidecar_mismatch' }
    Write-Host 'PASS: literal-path evidence packaging preserves bracketed TRX names, decoys, nested directories, archive bytes and sidecar hashes.'
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ([IO.Directory]::Exists($fixture)) { [IO.Directory]::Delete($fixture,$true) }
    foreach ($path in @($fixture+'.zip',$fixture+'.zip.sha256')) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
}
