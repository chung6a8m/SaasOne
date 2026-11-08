# Validate tooling-only RID lockfile extensions in a disposable build worktree.
# Never consider modified dependency versions or base framework graphs acceptable.
function Test-M6RidLockDelta {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OriginalJson,
          [Parameter(Mandatory)][string]$GeneratedJson,
          [ValidateSet('win-x64','linux-x64')][string]$RuntimeIdentifier)

    $original = $OriginalJson | ConvertFrom-Json -AsHashtable -Depth 64
    $generated = $GeneratedJson | ConvertFrom-Json -AsHashtable -Depth 64
    if ($null -eq $original -or $null -eq $generated -or
        $original.version -ne $generated.version -or
        $null -eq $original.dependencies -or $null -eq $generated.dependencies) {
        throw 'bundle_lock_invalid'
    }
    if (@($generated.Keys | Where-Object { @($original.Keys) -cnotcontains $_ }).Count -gt 0 -or
        @($original.Keys | Where-Object { @($generated.Keys) -cnotcontains $_ }).Count -gt 0) {
        throw 'bundle_lock_metadata_changed'
    }
    foreach ($target in @($original.dependencies.Keys)) {
        if (@($generated.dependencies.Keys) -cnotcontains $target) { throw 'bundle_lock_target_removed' }
        $previous = $original.dependencies[$target] | ConvertTo-Json -Depth 64 -Compress
        $current = $generated.dependencies[$target] | ConvertTo-Json -Depth 64 -Compress
        if ($previous -cne $current) { throw 'bundle_lock_baseline_graph_changed' }
    }
    $added = @($generated.dependencies.Keys | Where-Object { @($original.dependencies.Keys) -cnotcontains $_ })
    if ($added.Count -gt 1 -or ($added.Count -eq 1 -and $added[0] -cne ('net10.0/' + $RuntimeIdentifier))) {
        throw 'bundle_lock_unexpected_runtime_graph'
    }
    # The additional RID graph cannot silently replace the version/hash of a
    # package already pinned in the platform-neutral net10.0 graph.
    if ($added.Count -eq 1 -and @($original.dependencies.Keys) -ccontains 'net10.0') {
        $baseline = $original.dependencies['net10.0']
        $runtime = $generated.dependencies[$added[0]]
        foreach ($name in @($runtime.Keys)) {
            if (@($baseline.Keys) -ccontains $name) {
                foreach ($property in @('resolved','contentHash')) {
                    $oldValue = $baseline[$name][$property]
                    $newValue = $runtime[$name][$property]
                    if ($null -ne $oldValue -and $null -ne $newValue -and $oldValue -cne $newValue) {
                        throw 'bundle_lock_rid_dependency_changed'
                    }
                }
            }
        }
    }
    return $true
}

function Assert-M6BundleWorktreeChanges {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Worktree,
          [ValidateSet('win-x64','linux-x64')][string]$RuntimeIdentifier)

    $changes = @(& git -C $Worktree status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'bundle_worktree_status_failed' }
    $validated = @()
    foreach ($line in $changes) {
        if ($line.Length -lt 4 -or $line.Substring(0, 2) -cne ' M') {
            throw 'bundle_worktree_unexpected_change'
        }
        $relative = $line.Substring(3)
        if ($relative -cnotmatch '^src/(Modules|Platform)/SaaS\.[A-Za-z]+/packages\.lock\.json$') {
            throw 'bundle_worktree_unexpected_change'
        }
        $original = (& git -C $Worktree show "HEAD:$relative" | Out-String)
        if ($LASTEXITCODE -ne 0) { throw 'bundle_lock_original_unavailable' }
        $currentFile = Join-Path $Worktree $relative
        $current = Get-Content -LiteralPath $currentFile -Raw
        $null = Test-M6RidLockDelta $original $current $RuntimeIdentifier
        $validated += [ordered]@{ path = $relative; generatedSha256 = (Get-FileHash -LiteralPath $currentFile -Algorithm SHA256).Hash }
    }
    return ,$validated
}
