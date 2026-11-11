# M7 evidence-packaging regression plan

Extends `m7-task-plan.md`; no new runtime/product/database/migration scope.

The branch push at `151ba5decd51cdf72839f83a9b15df1c0902fb25` passed the baseline
command, 219 non-SQL cases and all seven SQL cases. Its retained artifact is measurement
evidence, not a latency acceptance decision. The concurrent PR run `42495005340`
logged seven SQL passes but failed final packaging; no PR evidence bundle was uploaded,
so that workflow remains FAIL.

The PR's parallel test projects emitted a colliding TRX filename ending in
`net10.0[1].trx`. The original package loop used wildcard-aware `Get-FileHash -Path`;
the push run's two TRX filenames had different timestamps and no bracket suffix.
The observed failure is the regression's RED evidence.

Affected files: `scripts/m7-evidence-package.ps1`, `scripts/check-m7.ps1`,
`scripts/check-m7-product.ps1`, `scripts/test-m7-evidence.ps1`.

Consumed/provided interfaces: the helper provides `Get-M7FileDigest(Path)` and
`Write-M7EvidencePackage(Root, SourceSha)`, reuses `Write-M6Json`, and retains the
existing hashes.json / ZIP / SHA256-sidecar contracts. File paths are literal;
links and existing package manifests/archives are rejected. The command uses
sanitized packaging diagnostics while keeping packaging failure nonzero.

Behavioral regression: a bracketed root, a real `result[1].trx` alongside the distinct
`result1.trx` decoy, a bracketed nested path, exact inventory/ZIP contents and independent
SHA256/sidecar checks. The same packaging helper is used by the regression and final
verification command. Product artifact hashing also becomes literal-path safe.

Commands: `pwsh -NoProfile -File scripts/check-powershell-syntax.ps1`,
`pwsh -NoProfile -File scripts/test-m7-evidence.ps1`, then the full fresh
`pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Baseline` on branch and PR.

Migration owner: not applicable; no model, table, schema/history or migration changes.
Load population, observation deadline, latency targets and core guards are unchanged.
At plan revision the fix's runtime results are not yet recorded; verify the new SHA
before claiming success. A previous green source SHA is not evidence for this change.
