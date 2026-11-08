[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm7-evidence-policy.ps1')
$sha='a'*40
function New-ReadyTargets {
    return @{
        schemaVersion=1; loadConfigSha256=('b'*64); reviewStatus='Approved'; reviewedBy='Synthetic test reviewer'; decisionReference='synthetic-test-only'; maxUnexpectedErrors=0
        baseline=@{sourceSha=$sha;reportSha256=('c'*64);reportPath='synthetic.json';artifactReference='synthetic-test-only'}
        limits=@(Get-M7Cells | ForEach-Object {
            $parts=$_.Split('/')
            @{tenant=$parts[0];operation=$parts[1];p95Ms=20.0;p99Ms=40.0;minSuccessfulRequestsPerSecond=1.0;minMeasuredRequests=100}
        })
    }
}
function New-ReadyReport {
    return @{
        schemaVersion=1;sourceSha=$sha;status='PASS';configuredVirtualUsers=100;peakVirtualUsers=100;peakConcurrentRequests=42;elapsedIncludingDrainSeconds=151.0
        config=@{schemaVersion=1;virtualUsersPerTenant=25;rampSeconds=30;measureSeconds=120}
        missingCells=@();unexpectedFailures=@();integrityErrors=@()
        rows=@(Get-M7Cells | ForEach-Object {
            $parts=$_.Split('/')
            @{tenant=$parts[0];operation=$parts[1];totalRequests=150;measuredRequests=100;measuredFailures=0;allPhaseFailures=0;unexpected5xx=0;transportFailures=0;successfulCompletionsPerSecond=1.0
              latency=@{count=100;p50Ms=10.0;p95Ms=20.0;p99Ms=40.0;maxMs=40.0}}
        })
    }
}
$script:cases=0
function Must-Reject([string]$Name,[scriptblock]$Action) {
    $rejected=$false
    try { & $Action } catch { if ($_.Exception.Message -notlike 'm7_*') { throw }; $rejected=$true }
    if (-not $rejected) { throw "Target policy accepted: $Name" }
    $script:cases++
}
# This is the first regression: a nonempty reviewer string currently admits an
# explicitly Proposed target to Acceptance. The intended RED is this assertion.
Must-Reject 'Proposed target with a nonempty reviewer is not approved' {
    $t=New-ReadyTargets; $t.reviewStatus='Proposed'; Assert-M7Targets $t
}
Must-Reject 'Approved target without reviewer' { $t=New-ReadyTargets; $t.reviewedBy=''; Assert-M7Targets $t }
Must-Reject 'unknown approval state' { $t=New-ReadyTargets; $t.reviewStatus='pending'; Assert-M7TargetDefinition $t }
foreach ($invalid in @(0,-1,0.5,[double]::NaN,[double]::PositiveInfinity,'100',$true)) {
    Must-Reject 'invalid sample floor' { $t=New-ReadyTargets; $t.limits[0].minMeasuredRequests=$invalid; Assert-M7TargetDefinition $t }
}
$t=New-ReadyTargets
Assert-M7Targets $t
$a=Get-M7TargetAssessment (New-ReadyReport) $t
if ($a.status -cne 'PASS' -or $a.rows.Count -ne 22 -or $a.failedCells -ne 0 -or $a.failedMetrics -ne 0) { throw 'Exact target boundaries must pass all 22 cells.' }
$script:cases++
# A proposal can be evaluated, but evaluation is not approval.
$t.reviewStatus='Proposed'; $t.reviewedBy=$null
Assert-M7TargetDefinition $t
$a=Get-M7TargetAssessment (New-ReadyReport) $t
if ($a.status -cne 'PASS' -or $a.reviewStatus -cne 'Proposed' -or $a.acceptance -cne 'NOT_GRANTED') { throw 'Candidate evaluation confused with approval.' }
$script:cases++
$r=New-ReadyReport
$r.rows[0].measuredRequests=99; $r.rows[0].latency.count=99
$a=Get-M7TargetAssessment $r $t
if ($a.status -cne 'FAIL' -or $a.failedCells -ne 1 -or $a.rows[0].failures.Count -ne 1 -or $a.rows[0].failures[0] -cne 'sample-count') { throw 'Undersampled report must fail even with fast latency.' }
$script:cases++
$r.rows[0].latency.p95Ms=41.0; $r.rows[0].latency.p99Ms=42.0; $r.rows[0].latency.maxMs=42.0
$r.rows[0].successfulCompletionsPerSecond=0.5
$r.rows[21].successfulCompletionsPerSecond=0.5
$a=Get-M7TargetAssessment $r $t
if ($a.status -cne 'FAIL' -or $a.rows.Count -ne 22 -or $a.failedCells -ne 2 -or $a.failedMetrics -ne 5 -or $a.rows[0].failures.Count -ne 4) { throw 'Assessment must preserve all simultaneous metric and cell failures.' }
$script:cases++
Must-Reject 'Acceptance thresholds include sample floor' {
    $t=New-ReadyTargets; $r=New-ReadyReport; $r.rows[0].measuredRequests=99; $r.rows[0].latency.count=99
    Assert-M7Thresholds $r $t
}
Must-Reject 'Candidate cannot hide invalid correctness evidence' { $r=New-ReadyReport; $r.rows[0].allPhaseFailures=1; Get-M7TargetAssessment $r (New-ReadyTargets) }
# Real retained observations are only a regression input, never a new run.
$root=Split-Path $PSScriptRoot -Parent
$proposal=Get-Content -LiteralPath (Join-Path $root 'tests/Load/m7-targets.json') -Raw | ConvertFrom-Json
$baseline=Get-Content -LiteralPath (Join-Path $root $proposal.baseline.reportPath) -Raw | ConvertFrom-Json
$a=Get-M7TargetAssessment $baseline $proposal
if ($a.status -cne 'FAIL' -or $a.failedCells -ne 22 -or $a.acceptance -cne 'NOT_GRANTED') { throw 'Historical slow baseline must not pass the new engineering proposal.' }
$script:cases++
Write-Host "PASS: M7 target readiness ($script:cases behavioral cases). Synthetic inputs and historical assessments are not fresh acceptance evidence."
