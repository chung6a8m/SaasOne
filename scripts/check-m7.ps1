# Implementation/baseline verification is intentionally separate from owner acceptance.
[CmdletBinding()]
param([ValidateSet('Baseline','Candidate','Acceptance')][string]$Mode='Baseline', [string]$Targets)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
. (Join-Path $PSScriptRoot 'm6-common.ps1')
. (Join-Path $PSScriptRoot 'm7-evidence-policy.ps1')
. (Join-Path $PSScriptRoot 'm7-evidence-package.ps1')
$root=Split-Path $PSScriptRoot -Parent
$runId=[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[guid]::NewGuid().ToString('N')
$run=Join-Path $root "artifacts/m7-runs/$runId"
[void][IO.Directory]::CreateDirectory($run)
$commands=[Collections.Generic.List[object]]::new()
$report=[ordered]@{schemaVersion=1;mode=$Mode;status='FAIL';sourceSha=$null;sourceClean=$false;sourceCleanAfter=$false;inheritedFullRequired=($Mode -eq 'Acceptance');inheritedFullStatus='NOT_RUN';startedUtc=[DateTimeOffset]::UtcNow;completedUtc=$null;commands=$commands;acceptance='NOT_ASSESSED';ownerReview='PENDING';exitCode=1}
$code=1; $lock=$null; $targetValue=$null; $targetPath=$null; $targetHash=$null
$knownSecrets=@(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' -and $_.Name -notmatch '(SOURCE_SHA|EVIDENCE_DIRECTORY)$' -and $_.Value.Length -ge 8 } | ForEach-Object Value)
function Protect-RunText([string]$Text) {
    foreach ($value in $knownSecrets) { $Text=$Text.Replace($value,'[REDACTED_CONFIG]') }
    Protect-M6Text $Text
}
function Invoke-M7Gate([string]$Name,[string[]]$Arguments) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $native=1
    $writer=[IO.StreamWriter]::new((Join-Path $run "$Name.log"),$false,[Text.UTF8Encoding]::new($false))
    try {
        & pwsh -NoProfile -File @Arguments 2>&1 | ForEach-Object { $line=Protect-RunText ([string]$_); $writer.WriteLine($line); $writer.Flush(); Write-Host $line }
        $native=$LASTEXITCODE
    } finally {
        $writer.Dispose(); $timer.Stop()
        $commands.Add([ordered]@{name=$Name;executable='pwsh';arguments=$Arguments;exitCode=$native;durationMs=$timer.ElapsedMilliseconds})
    }
    if ($native -ne 0) { throw "m7_gate_failed:$Name`:$native" }
}
function Copy-M7Text([string]$Source,[string]$Relative) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "m7_missing_evidence:$Relative" }
    if ((Get-Item -LiteralPath $Source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'm7_evidence_link_forbidden' }
    $destination=Join-Path $run $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    if ([IO.Path]::GetExtension($Source) -eq '.trx') {
        $trx=Read-M6Trx $Source
        foreach ($node in $trx.xml.SelectNodes('//*[local-name()="Message" or local-name()="StackTrace" or local-name()="StdOut" or local-name()="StdErr"]')) {
            $node.InnerText=Protect-RunText $node.InnerText
        }
        # Preserve testcase names, outcomes and counters verbatim, including long names.
        $trx.xml.Save($destination)
    } else {
        [IO.File]::WriteAllText($destination,(Protect-RunText ([IO.File]::ReadAllText($Source))),[Text.UTF8Encoding]::new($false))
    }
}
function Require-TrackedPath([string]$Relative) {
    $full=Resolve-M6Artifact $root $Relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'BLOCKED:committed_evidence_file_missing' }
    & git ls-files --error-unmatch -- $Relative | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED:evidence_file_not_tracked' }
    if ((Get-Item -LiteralPath $full).Length -gt 5242880) { throw 'm7_evidence_file_too_large' }
    return $full
}
Push-Location -LiteralPath $root
try {
    $lock=[IO.File]::Open((Join-Path $root 'artifacts/m7-verification.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $report.sourceSha=Get-M6Sha
    $dirty=@(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'BLOCKED:clean_checkout_required' }
    $report.sourceClean=$true
    if ($Mode -ne 'Baseline') {
        if ([string]::IsNullOrWhiteSpace($Targets)) { throw 'BLOCKED:reviewed_precommitted_targets_required' }
        $targetPath=Require-TrackedPath $Targets
        $targetHash=(Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $targetValue=Get-Content -LiteralPath $targetPath -Raw | ConvertFrom-Json
        Assert-M7TargetDefinition $targetValue
        if ($Mode -eq 'Acceptance') {
            try { Assert-M7Targets $targetValue }
            catch {
                if ($_.Exception.Message -ceq 'm7_targets_not_owner_approved') { throw 'BLOCKED:owner_approved_targets_required' }
                throw
            }
        } else { $report.acceptance='CANDIDATE_ONLY_NOT_ACCEPTANCE' }
        $baselinePath=Require-TrackedPath $targetValue.baseline.reportPath
        if ((Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $targetValue.baseline.reportSha256) { throw 'm7_baseline_hash_mismatch' }
        $baseline=Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
        Assert-M7LoadReport $baseline $targetValue.baseline.sourceSha
        if ($baseline.mode -cne 'Baseline' -or $baseline.sourceSha -ceq $report.sourceSha) { throw 'm7_prior_baseline_required' }
        & git merge-base --is-ancestor $baseline.sourceSha HEAD
        if ($LASTEXITCODE -ne 0) { throw 'BLOCKED:baseline_ancestor_or_history_missing' }
        $lockedAt=(& git log -1 --format=%cI -- $Targets | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($lockedAt)) { throw 'BLOCKED:target_commit_time_missing' }
        Assert-M7TargetTiming ([DateTimeOffset]$baseline.completedUtc) ([DateTimeOffset]$lockedAt) $report.startedUtc
        if ((Get-FileHash -LiteralPath tests/Load/m7-baseline.json -Algorithm SHA256).Hash.ToLowerInvariant() -cne $targetValue.loadConfigSha256) { throw 'm7_load_configuration_changed' }
        $report.targets=[ordered]@{path=$Targets;sha256=$targetHash;committedUtc=$lockedAt;baselineSha=$baseline.sourceSha;decisionReference=$targetValue.decisionReference;reviewStatus=$targetValue.reviewStatus;reviewedBy=$targetValue.reviewedBy}
        Copy-M7Text $targetPath 'targets.json'
        Copy-M7Text $baselinePath 'baseline/load.json'
        if ($Mode -eq 'Acceptance') {
            $beforeFull=@(Get-ChildItem artifacts/local-verify -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
            # Full inherited M0-M6 evidence is mandatory at this exact source, not a prior green SHA.
            Invoke-M7Gate 'inherited-full' @('scripts/local-verify.ps1','-Scope','Full')
            $inherited=@(Get-ChildItem artifacts/local-verify -Directory | Where-Object { $_.Name -notin $beforeFull })
            if ($inherited.Count -ne 1) { throw 'm7_inherited_evidence_missing_or_ambiguous' }
            $summary=Join-Path $inherited[0].FullName 'summary.json'
            $prior=Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json
            if ($prior.sourceSha -cne $report.sourceSha -or $prior.status -cne 'PASS' -or $prior.exitCode -ne 0) { throw 'm7_inherited_evidence_mismatch' }
            Copy-M7Text $summary 'inherited-full.json'
            $inheritedZip=$inherited[0].FullName+'.zip'
            if (-not (Test-Path -LiteralPath $inheritedZip)) { throw 'm7_inherited_package_missing' }
            [IO.File]::Copy($inheritedZip,(Join-Path $run 'inherited-full.zip'))
            $report.inheritedFullStatus='PASS'
        }
    } elseif (-not [string]::IsNullOrWhiteSpace($Targets)) { throw 'm7_targets_not_allowed_in_baseline_mode' }
    Invoke-M7Gate 'syntax' @('scripts/check-powershell-syntax.ps1')
    Invoke-M7Gate 'evidence-policy' @('scripts/test-m7-evidence.ps1')
    Invoke-M7Gate 'target-policy' @('scripts/test-m7-target-readiness.ps1')
    Invoke-M7Gate 'm0' @('scripts/check.ps1','-Scope','M0')
    Copy-M7Text (Join-Path $root 'artifacts/verification/m0.json') 'm0.json'
    foreach ($trx in Get-ChildItem artifacts/test-results/m0 -Filter *.trx -File) { Copy-M7Text $trx.FullName ('m0/'+$trx.Name) }
    Invoke-M7Gate 'product' @('scripts/check-m7-product.ps1')
    Copy-M7Text (Join-Path $root 'artifacts/m7/product/report.json') 'product/report.json'
    Copy-M7Text (Join-Path $root 'artifacts/m7/product/features.json') 'product/features.json'
    Invoke-M7Gate 'template' @('scripts/check-m7-template.ps1')
    try { Invoke-M7Gate 'sql' @('scripts/m7-sql-fixture.ps1','-Mode',$Mode) }
    finally {
        foreach ($name in @('sql.json','matrix.json','product-runtime.json','load.json','load-samples.json','test.log','tests.trx')) {
            $file=Join-Path $root "artifacts/m7/evidence/$name"
            if (Test-Path $file) { Copy-M7Text $file "sql/$name" }
        }
    }
    $load=Get-Content (Join-Path $root 'artifacts/m7/evidence/load.json') -Raw | ConvertFrom-Json
    Assert-M7LoadReport $load $report.sourceSha
    if ($load.mode -cne $Mode) { throw 'm7_report_mode_mismatch' }
    if ((Get-M6Sha) -cne $report.sourceSha -or @(& git status --porcelain=v1 --untracked-files=all).Count -ne 0) { throw 'm7_source_changed_during_run' }
    $report.sourceCleanAfter=$true
    if ($Mode -ne 'Baseline') {
        if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $targetHash) { throw 'm7_targets_changed_during_run' }
        $assessment=Get-M7TargetAssessment $load $targetValue
        $assessment.mode=$Mode
        $assessment.targetSha256=$targetHash
        $assessment.targetPath=$Targets
        $assessment.targetCommittedUtc=$report.targets.committedUtc
        $assessment.baselineSourceSha=$targetValue.baseline.sourceSha
        # Retain ALL metric/cell failures before making the process fail.
        Write-M6Json (Join-Path $run 'thresholds.json') $assessment
        $report.thresholds=[ordered]@{status=$assessment.status;failedCells=$assessment.failedCells;failedMetrics=$assessment.failedMetrics}
        if ($assessment.status -cne 'PASS') {
            $report.acceptance=$(if ($Mode -eq 'Candidate') { 'CANDIDATE_TARGETS_FAILED_NOT_ACCEPTANCE' } else { 'TARGETS_FAILED' })
            throw "m7_threshold_failed:$($assessment.failedCells)_cells:$($assessment.failedMetrics)_metrics"
        }
        if ($Mode -eq 'Acceptance') {
            Assert-M7Thresholds $load $targetValue
            $report.acceptance='TECHNICAL_GATES_PASSED_OWNER_REVIEW_REQUIRED'
        } else { $report.acceptance='CANDIDATE_TARGETS_PASSED_NOT_ACCEPTANCE' }
    } else { $report.acceptance='BASELINE_ONLY_TARGETS_NOT_ASSESSED' }
    $report.status='PASS'; $code=0
} catch {
    $report.error=Protect-RunText $_.Exception.Message
    if ($_.Exception.Message -like 'BLOCKED:*') { $report.status='BLOCKED'; $code=2 }
    Write-Host (Protect-RunText ('FAIL: '+$_.Exception.Message))
} finally {
    $report.exitCode=$code; $report.completedUtc=[DateTimeOffset]::UtcNow
    try {
        Write-M6Json (Join-Path $run 'verification.json') $report
        Write-M7EvidencePackage -Root $run -SourceSha ([string]$report.sourceSha)
    } catch {
        # Keep the failure nonzero and expose only a sanitized diagnostic, not raw data.
        Write-Host (Protect-RunText ('FAIL: M7 evidence packaging failed: '+$_.Exception.Message))
        $code=1
    }
    if ($null -ne $lock) { $lock.Dispose() }
    Pop-Location
}
exit $code
