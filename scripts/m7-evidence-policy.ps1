# Pure report/target validation; no I/O or success output when dot-sourced.
Set-StrictMode -Version Latest
function Get-M7Cells {
    foreach ($tenant in @('alpha','beta','gamma','delta')) {
        $operations = @('create','edit','submit','detail','list')
        if ($tenant -in @('beta','gamma')) { $operations += 'approve' }
        foreach ($operation in $operations) { "$tenant/$operation" }
    }
}
function Assert-M7Finite([object]$Value, [double]$Minimum = 0) {
    if ($null -eq $Value -or $Value -is [bool] -or $Value -is [string] -or -not [double]::IsFinite([double]$Value) -or [double]$Value -lt $Minimum) {
        throw 'm7_invalid_numeric_evidence'
    }
}
function Assert-M7LoadReport($Report, [string]$SourceSha) {
    if ($SourceSha -cnotmatch '^[0-9a-f]{40}$' -or $Report.sourceSha -cne $SourceSha -or $Report.schemaVersion -ne 1 -or $Report.status -cne 'PASS') { throw 'm7_invalid_report_provenance_or_status' }
    if ($Report.config.schemaVersion -ne 1 -or $Report.config.virtualUsersPerTenant -ne 25 -or $Report.config.rampSeconds -lt 5 -or $Report.config.measureSeconds -lt 30 -or $Report.config.rampSeconds + $Report.config.measureSeconds -gt 180) { throw 'm7_invalid_load_configuration' }
    if ($Report.configuredVirtualUsers -ne 100 -or $Report.peakVirtualUsers -ne 100 -or $Report.peakConcurrentRequests -lt 1 -or $Report.peakConcurrentRequests -gt 100) { throw 'm7_invalid_population' }
    if (@($Report.missingCells).Count -ne 0 -or @($Report.unexpectedFailures).Count -ne 0 -or @($Report.integrityErrors).Count -ne 0) { throw 'm7_load_correctness_failed' }
    Assert-M7Finite $Report.elapsedIncludingDrainSeconds ($Report.config.rampSeconds + $Report.config.measureSeconds)
    [string[]]$expected = @(Get-M7Cells)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in @($Report.rows)) {
        $cell = "$($row.tenant)/$($row.operation)"
        if ($cell -cnotin $expected -or -not $seen.Add($cell)) { throw 'm7_unknown_or_duplicate_cell' }
        foreach ($count in @($row.totalRequests,$row.measuredRequests,$row.latency.count)) {
            Assert-M7Finite $count 1
            if ([double]$count -ne [math]::Floor([double]$count)) { throw 'm7_fractional_sample_count' }
        }
        if ($row.measuredRequests -gt $row.totalRequests -or $row.latency.count -ne $row.measuredRequests) { throw 'm7_sample_count_mismatch' }
        foreach ($count in @($row.measuredFailures,$row.allPhaseFailures,$row.unexpected5xx,$row.transportFailures)) {
            Assert-M7Finite $count
            if ($count -ne 0) { throw 'm7_unexpected_error' }
        }
        Assert-M7Finite $row.successfulCompletionsPerSecond 0.000001
        $previous = 0.0
        foreach ($value in @($row.latency.p50Ms,$row.latency.p95Ms,$row.latency.p99Ms,$row.latency.maxMs)) {
            Assert-M7Finite $value $previous
            $previous = [double]$value
        }
    }
    if (-not $seen.SetEquals($expected)) { throw 'm7_missing_workload_cell' }
}
function Assert-M7TargetDefinition($Targets) {
    if ($Targets.schemaVersion -ne 1 -or $Targets.loadConfigSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Targets.baseline.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or
        $Targets.baseline.reportSha256 -cnotmatch '^[0-9a-f]{64}$' -or [string]::IsNullOrWhiteSpace($Targets.baseline.reportPath) -or
        [string]::IsNullOrWhiteSpace($Targets.baseline.artifactReference) -or
        [string]::IsNullOrWhiteSpace($Targets.decisionReference) -or $Targets.maxUnexpectedErrors -ne 0) { throw 'm7_invalid_targets' }
    if ($Targets.reviewStatus -cnotin @('Proposed','Approved')) { throw 'm7_invalid_target_review_state' }
    [string[]]$expected = @(Get-M7Cells)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in @($Targets.limits)) {
        $cell = "$($row.tenant)/$($row.operation)"
        if ($cell -cnotin $expected -or -not $seen.Add($cell)) { throw 'm7_unknown_or_duplicate_target' }
        Assert-M7Finite $row.p95Ms 0.000001
        Assert-M7Finite $row.p99Ms ([double]$row.p95Ms)
        Assert-M7Finite $row.minSuccessfulRequestsPerSecond 0.000001
        Assert-M7Finite $row.minMeasuredRequests 1
        if ([double]$row.minMeasuredRequests -ne [math]::Floor([double]$row.minMeasuredRequests)) { throw 'm7_fractional_sample_floor' }
    }
    if (-not $seen.SetEquals($expected)) { throw 'm7_missing_target_cell' }
}
function Assert-M7TargetTiming([DateTimeOffset]$BaselineCompleted, [DateTimeOffset]$LockedAt, [DateTimeOffset]$RunStarted) {
    if ($BaselineCompleted -ge $LockedAt -or $LockedAt -gt $RunStarted) { throw 'm7_targets_not_locked_before_run' }
}
function Assert-M7Targets($Targets) {
    Assert-M7TargetDefinition $Targets
    if ($Targets.reviewStatus -cne 'Approved' -or [string]::IsNullOrWhiteSpace($Targets.reviewedBy)) { throw 'm7_targets_not_owner_approved' }
}
# Assess proposed or approved definitions, but never grant acceptance here.
function Get-M7TargetAssessment($Report, $Targets) {
    Assert-M7LoadReport $Report $Report.sourceSha
    Assert-M7TargetDefinition $Targets
    $rows=[Collections.Generic.List[object]]::new()
    $failedMetrics=0
    foreach ($limit in $Targets.limits) {
        $row=@($Report.rows | Where-Object { $_.tenant -ceq $limit.tenant -and $_.operation -ceq $limit.operation })[0]
        $failures=[Collections.Generic.List[string]]::new()
        if ($row.latency.p95Ms -gt $limit.p95Ms) { $failures.Add('p95') }
        if ($row.latency.p99Ms -gt $limit.p99Ms) { $failures.Add('p99') }
        if ($row.successfulCompletionsPerSecond -lt $limit.minSuccessfulRequestsPerSecond) { $failures.Add('throughput') }
        if ($row.measuredRequests -lt $limit.minMeasuredRequests) { $failures.Add('sample-count') }
        $failedMetrics+=$failures.Count
        $rows.Add([ordered]@{
            tenant=$row.tenant;operation=$row.operation;passed=($failures.Count -eq 0);failures=$failures.ToArray()
            p95Ms=$row.latency.p95Ms;p95LimitMs=$limit.p95Ms;p99Ms=$row.latency.p99Ms;p99LimitMs=$limit.p99Ms
            successfulCompletionsPerSecond=$row.successfulCompletionsPerSecond;minSuccessfulRequestsPerSecond=$limit.minSuccessfulRequestsPerSecond
            measuredRequests=$row.measuredRequests;minMeasuredRequests=$limit.minMeasuredRequests
        })
    }
    $failedCells=@($rows | Where-Object { -not $_.passed }).Count
    return [ordered]@{
        schemaVersion=1;sourceSha=$Report.sourceSha;status=$(if ($failedCells -eq 0) { 'PASS' } else { 'FAIL' })
        acceptance='NOT_GRANTED';reviewStatus=$Targets.reviewStatus;failedCells=$failedCells;failedMetrics=$failedMetrics;rows=$rows.ToArray()
    }
}
function Assert-M7Thresholds($Report, $Targets) {
    Assert-M7Targets $Targets
    $assessment=Get-M7TargetAssessment $Report $Targets
    if ($assessment.status -cne 'PASS') { throw "m7_threshold_failed:$($assessment.failedCells)_cells:$($assessment.failedMetrics)_metrics" }
}
