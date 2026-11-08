[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm7-evidence-policy.ps1')
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
    try { & $Action } catch { $rejected=$true }
    if (-not $rejected) { throw "Evidence policy did not reject: $Name" }
    $script:count++
}
Assert-M7LoadReport (New-Report) $sha
Assert-M7Thresholds (New-Report) (New-Targets)
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
Must-Reject 'post-hoc lock' { Assert-M7TargetTiming ([DateTimeOffset]'2026-01-01Z') ([DateTimeOffset]'2026-01-03Z') ([DateTimeOffset]'2026-01-02Z') }
Must-Reject 'no preceding baseline' { Assert-M7TargetTiming ([DateTimeOffset]'2026-01-02Z') ([DateTimeOffset]'2026-01-01Z') ([DateTimeOffset]'2026-01-03Z') }
Must-Reject 'latency threshold exceeded' { $t=New-Targets; $t.limits[0].p95Ms=15.0; Assert-M7Thresholds (New-Report) $t }
Must-Reject 'throughput threshold missed' { $t=New-Targets; $t.limits[0].minSuccessfulRequestsPerSecond=1.0; Assert-M7Thresholds (New-Report) $t }
Write-Host "PASS: M7 evidence policy ($script:count negative cases; positive report and threshold cases). Synthetic policy inputs are not load evidence."
