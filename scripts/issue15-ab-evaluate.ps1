[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ControlSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$CandidateSha
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$path = Join-Path $root 'artifacts/issue15-ab'
$sequence = @('A','B','B','A','A','B')
$trials = [System.Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schemaVersion=1; status='FAIL'; decision='BLOCKED'; acceptance='NOT_ASSESSED'
    scope='Issue15 List P3 ABBAAB on ONE GitHub runner; pinned SQL 2022; native HTTPS 100 VU; SQL profiler OFF'
    harnessSha=(& git -C $root rev-parse HEAD).Trim()
    controlSha=$ControlSha; candidateSha=$CandidateSha
    constraints='Different freshly provisioned disposable SQL databases with same seed shape; both variants use the same benchmark harness, runner, toolchain and workload. Only OrderService.cs production source varies. Not a same-data-state transactional replay or production capacity assertion.'
    trials=@(); comparisons=@(); issues=@()
}
$code = 1
try {
    $issues = [System.Collections.Generic.List[string]]::new()
    for ($i=0; $i -lt $sequence.Count; $i++) {
        $number = ('trial-{0:D2}' -f ($i+1))
        $dir = Join-Path $path $number
        $loadFile = Join-Path $dir 'load.json'
        $manifestFile = Join-Path $dir 'source.json'
        if (-not (Test-Path $loadFile) -or -not (Test-Path $manifestFile))
            { throw "Missing trial data $number" }
        $load = Get-Content $loadFile -Raw | ConvertFrom-Json
        $manifest = Get-Content $manifestFile -Raw | ConvertFrom-Json
        $type = [string]$sequence[$i]
        $expected = if ($type -eq 'A') { $ControlSha } else { $CandidateSha }
        if ($manifest.variant -cne $type -or $manifest.sourceCommitSha -cne $expected
            -or $manifest.harnessSha -cne $report.harnessSha
            -or $manifest.sourceFileSha256 -notmatch '^[0-9a-f]{64}$')
            { throw "Source identity invalid for $number" }
        if ($load.status -cne 'PASS' -or $load.sourceSha -cne $report.harnessSha
            -or $load.config.virtualUsersPerTenant -ne 25
            -or $load.config.rampSeconds -ne 30 -or $load.config.measureSeconds -ne 120
            -or $load.config.thinkMilliseconds -ne 250
            -or $load.sqlProfilerEnabled -ne $false -or $load.peakVirtualUsers -ne 100
            -or @($load.rows).Count -ne 22
            -or @($load.missingCells).Count -ne 0 -or @($load.unexpectedFailures).Count -ne 0
            -or @($load.integrityErrors).Count -ne 0)
            { throw "Invalid 100-VU, four-tenant, no-profiler workload for $number" }
        foreach ($cell in @($load.rows)) {
            if ($null -eq $cell.latency -or $cell.measuredFailures -ne 0
                -or $cell.allPhaseFailures -ne 0 -or $cell.transportFailures -ne 0)
                { throw "Invalid $number business/transport coverage" }
            if ($cell.measuredRequests -lt 100)
                { $issues.Add("$number/$($cell.tenant)/$($cell.operation):sample_floor_missed") }
        }
        $list = @($load.rows | Where-Object { $_.operation -eq 'list' })
        if ($list.Count -ne 4) { throw "Missing List cells in $number" }
        $trials.Add([ordered]@{
            trial=$number; variant=$type; sourceCommitSha=$expected
            sourceFileSha256=[string]$manifest.sourceFileSha256
            requestCount=@($load.rows | ForEach-Object { $_.totalRequests } | Measure-Object -Sum).Sum
            logicalProcessors=$load.environment.logicalProcessors
            processCpuSeconds=[double]$load.environment.processCpuSeconds
            list=@($list | Sort-Object tenant | ForEach-Object {
                [ordered]@{
                    tenant=[string]$_.tenant; p95Ms=[double]$_.latency.p95Ms
                    p99Ms=[double]$_.latency.p99Ms
                    sampleCount=[int]$_.measuredRequests
                    successfulCompletionsPerSecond=[double]$_.successfulCompletionsPerSecond
                }
            })
        })
    }
    function Median([double[]]$values) {
        if ($values.Count -ne 3) { throw 'Expected 3 trials per variant for paired medians.' }
        return [double](@($values | Sort-Object)[1])
    }
    $comparisons = [System.Collections.Generic.List[object]]::new()
    $wins = 0
    $regressions = 0
    foreach ($tenant in @('alpha','beta','gamma','delta')) {
        $a = @($trials | Where-Object variant -eq 'A' | ForEach-Object { $_.list | Where-Object tenant -eq $tenant })
        $b = @($trials | Where-Object variant -eq 'B' | ForEach-Object { $_.list | Where-Object tenant -eq $tenant })
        if ($a.Count -ne 3 -or $b.Count -ne 3) { throw "Missing paired tenant $tenant" }
        $baseline = Median ([double[]]@($a | ForEach-Object p95Ms))
        $candidate = Median ([double[]]@($b | ForEach-Object p95Ms))
        $beforeThroughput = Median ([double[]]@($a | ForEach-Object successfulCompletionsPerSecond))
        $afterThroughput = Median ([double[]]@($b | ForEach-Object successfulCompletionsPerSecond))
        $pct = 100 * ($candidate - $baseline) / [Math]::Max(1, $baseline)
        if ($pct -le -5) { $wins++ }
        if ($pct -ge 5) { $regressions++ }
        $comparisons.Add([ordered]@{
            tenant=$tenant; baselineMedianP95Ms=$baseline; candidateMedianP95Ms=$candidate
            changeP95Percent=[Math]::Round($pct, 2)
            baselineMedianRps=$beforeThroughput; candidateMedianRps=$afterThroughput
        })
    }
    $controlCpu = Median ([double[]]@($trials | Where-Object variant -eq 'A' | ForEach-Object processCpuSeconds))
    $candidateCpu = Median ([double[]]@($trials | Where-Object variant -eq 'B' | ForEach-Object processCpuSeconds))
    $cpuChangePct = 100 * ($candidateCpu - $controlCpu) / [Math]::Max(1, $controlCpu)

    $report.trials = @($trials)
    $report.comparisons = @($comparisons)
    $report.cpu = [ordered]@{
        baselineMedianProcessCpuSeconds=$controlCpu
        candidateMedianProcessCpuSeconds=$candidateCpu
        changePercent=[Math]::Round($cpuChangePct, 2)
    }
    $report.issues = @($issues)
    $report.acceptance = 'NOT_ASSESSED'
    if ($issues.Count -gt 0) {
        $report.decision = 'INCONCLUSIVE_SAMPLE_FLOOR'
    } elseif ($regressions -ge 2 -or $cpuChangePct -ge 10) {
        $report.decision = 'REVERT_P3_RECOMMENDED'
    } elseif ($wins -ge 3 -and $regressions -eq 0 -and $cpuChangePct -le 5) {
        $report.decision = 'KEEP_P3_AS_EXPERIMENT_ONLY'
    } else {
        $report.decision = 'INCONCLUSIVE_DEFAULT_REVERT_P3'
    }
    $report.status = 'PASS'
    $code = 0
} catch {
    $report.issues = @((('BLOCKED: ' + $_.Exception.Message) -replace '(?i)password=[^\s;]+','password=[redacted]'))
    $report.status = 'BLOCKED'
}
finally {
    $report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $path 'report.json') -Encoding utf8
    Write-Host ("Issue15 AB: status=" + $report.status + " decision=" + $report.decision)
}
exit $code
