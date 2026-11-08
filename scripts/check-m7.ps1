# Implementation/baseline verification is intentionally separate from owner acceptance.
[CmdletBinding()]
param([ValidateSet('Baseline','Acceptance')][string]$Mode='Baseline', [string]$Targets)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
. (Join-Path $PSScriptRoot 'm6-common.ps1')
. (Join-Path $PSScriptRoot 'm7-evidence-policy.ps1')
$root=Split-Path $PSScriptRoot -Parent
$runId=[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[guid]::NewGuid().ToString('N')
$run=Join-Path $root "artifacts/m7-runs/$runId"
[void][IO.Directory]::CreateDirectory($run)
$commands=[Collections.Generic.List[object]]::new()
$report=[ordered]@{schemaVersion=1;mode=$Mode;status='FAIL';sourceSha=$null;sourceClean=$false;startedUtc=[DateTimeOffset]::UtcNow;completedUtc=$null;commands=$commands;acceptance='NOT_ASSESSED';ownerReview='PENDING';exitCode=1}
$code=1; $lock=$null; $targetValue=$null; $targetPath=$null; $targetHash=$null
$knownSecrets=@(Get-ChildItem Env: | Where-Object { $_.Name -match '^(SAAS_|MSSQL_)' -and $_.Value.Length -ge 8 } | ForEach-Object Value)
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
    [IO.File]::WriteAllText($destination,(Protect-RunText ([IO.File]::ReadAllText($Source))),[Text.UTF8Encoding]::new($false))
}
function Require-TrackedPath([string]$Relative) {
    $full=Resolve-M6Artifact $root $Relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'BLOCKED:committed_evidence_file_missing' }
    & git ls-files --error-unmatch -- $Relative | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED:evidence_file_not_tracked' }
    if ((Get-Item -LiteralPath $full).Length -gt 5242880) { throw 'm7_evidence_file_too_large' }
    return $full
}
Push-Location $root
try {
    $lock=[IO.File]::Open((Join-Path $root 'artifacts/m7-verification.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $report.sourceSha=Get-M6Sha
    $dirty=@(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'BLOCKED:clean_checkout_required' }
    $report.sourceClean=$true
    if ($Mode -eq 'Acceptance') {
        if ([string]::IsNullOrWhiteSpace($Targets)) { throw 'BLOCKED:reviewed_precommitted_targets_required' }
        $targetPath=Require-TrackedPath $Targets
        $targetHash=(Get-FileHash $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $targetValue=Get-Content -LiteralPath $targetPath -Raw | ConvertFrom-Json
        Assert-M7Targets $targetValue
        $baselinePath=Require-TrackedPath $targetValue.baseline.reportPath
        if ((Get-FileHash $baselinePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $targetValue.baseline.reportSha256) { throw 'm7_baseline_hash_mismatch' }
        $baseline=Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
        Assert-M7LoadReport $baseline $targetValue.baseline.sourceSha
        if ($baseline.mode -cne 'Baseline' -or $baseline.sourceSha -ceq $report.sourceSha) { throw 'm7_prior_baseline_required' }
        & git merge-base --is-ancestor $baseline.sourceSha HEAD
        if ($LASTEXITCODE -ne 0) { throw 'BLOCKED:baseline_ancestor_or_history_missing' }
        $lockedAt=(& git log -1 --format=%cI -- $Targets | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($lockedAt)) { throw 'BLOCKED:target_commit_time_missing' }
        Assert-M7TargetTiming ([DateTimeOffset]$baseline.completedUtc) ([DateTimeOffset]$lockedAt) $report.startedUtc
        if ((Get-FileHash tests/Load/m7-baseline.json -Algorithm SHA256).Hash.ToLowerInvariant() -cne $targetValue.loadConfigSha256) { throw 'm7_load_configuration_changed' }
        $report.targets=[ordered]@{path=$Targets;sha256=$targetHash;committedUtc=$lockedAt;baselineSha=$baseline.sourceSha;decisionReference=$targetValue.decisionReference}
        Copy-M7Text $targetPath 'targets.json'
        # Full inherited M0-M6 evidence is mandatory at this exact source, not a prior green SHA.
        Invoke-M7Gate 'inherited-full' @('scripts/local-verify.ps1','-Scope','Full')
        $inherited=@(Get-ChildItem artifacts/local-verify -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'report.json') } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
        if ($inherited.Count -ne 1) { throw 'm7_inherited_evidence_missing' }
        $prior=Get-Content (Join-Path $inherited[0].FullName 'report.json') -Raw | ConvertFrom-Json
        if ($prior.sourceSha -cne $report.sourceSha -or $prior.status -cne 'PASS') { throw 'm7_inherited_evidence_mismatch' }
        Copy-M7Text (Join-Path $inherited[0].FullName 'report.json') 'inherited-full.json'
    } elseif (-not [string]::IsNullOrWhiteSpace($Targets)) { throw 'm7_targets_only_apply_to_acceptance_mode' }
    Invoke-M7Gate 'syntax' @('scripts/check-powershell-syntax.ps1')
    Invoke-M7Gate 'evidence-policy' @('scripts/test-m7-evidence.ps1')
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
    if ($Mode -eq 'Acceptance') {
        if ((Get-FileHash $targetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $targetHash) { throw 'm7_targets_changed_during_run' }
        Assert-M7Thresholds $load $targetValue
        $report.acceptance='TECHNICAL_GATES_PASSED_OWNER_REVIEW_REQUIRED'
    } else { $report.acceptance='BASELINE_ONLY_TARGETS_NOT_ASSESSED' }
    if ((Get-M6Sha) -cne $report.sourceSha -or @(& git status --porcelain=v1 --untracked-files=all).Count -ne 0) { throw 'm7_source_changed_during_run' }
    $report.status='PASS'; $code=0
} catch {
    $report.error=Protect-RunText $_.Exception.Message
    if ($_.Exception.Message -like 'BLOCKED:*') { $report.status='BLOCKED'; $code=2 }
    Write-Host (Protect-RunText ('FAIL: '+$_.Exception.Message))
} finally {
    $report.exitCode=$code; $report.completedUtc=[DateTimeOffset]::UtcNow
    try {
        Write-M6Json (Join-Path $run 'verification.json') $report
        $files=@(Get-ChildItem -LiteralPath $run -Recurse -File | Sort-Object FullName | ForEach-Object {
            @{path=[IO.Path]::GetRelativePath($run,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        })
        Write-M6Json (Join-Path $run 'hashes.json') @{schemaVersion=1;sourceSha=$report.sourceSha;files=$files}
        $zip=$run+'.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($run,$zip)
        [IO.File]::WriteAllText($zip+'.sha256',(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()+"  $runId.zip`n")
    } catch { Write-Host 'FAIL: M7 evidence packaging failed.'; $code=1 }
    if ($null -ne $lock) { $lock.Dispose() }
    Pop-Location
}
exit $code
