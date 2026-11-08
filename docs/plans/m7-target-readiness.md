# M7 target-readiness implementation plan

> Execution: inline, task-by-task with behavioral RED/GREEN checks. This extends `m7-task-plan.md` and addresses PR #14 review 5818648969; it does not accept M7 or promote an ADR.

**Goal:** Preserve the reviewed baseline, make the remaining performance gap measurable against precommitted engineering proposals, and make a genuine separately approved Acceptance run executable without confusing it with a baseline or proposal rehearsal.

**Architecture:** Keep the existing platform, security, SQL, product and workload unchanged. Extend only the PowerShell evidence contract and CI orchestration. Baseline remains measurement-only; Candidate evaluates proposed targets but can never grant acceptance; Acceptance additionally requires an explicit approved target record and fresh inherited Full evidence at the exact source.

**Tech stack:** Existing pinned .NET/PowerShell/SQL Server/GitHub Actions toolchain; no new package or migration.

**Spec:** roadmap revision 1.3 M7 / sections 8 and 9.1, Issue #12, AGENTS.md, `docs/runbooks/m7-load-and-acceptance.md`, PR review 5818648969.

## Constraints and decisions

The user requested implementation of the remaining steps toward M7 acceptance. This authorizes implementation and evidence collection, not fabrication of a human performance-target approval. New numerical budgets remain clearly marked proposals. `reviewedBy` must not be filled with the repository owner's name merely because the connector runs under that account. Final owner review remains pending even after technical acceptance passes.

Keep 25 VUs per tenant / 100 total, the 30-second ramp, 120-second measured window, 250-ms think time and 120-second observation deadline. Keep all four tenants Active during load and lifecycle separate. Do not weaken current-state checks, cache Allow, enable pooling or change core runtime without a separately justified performance plan. No source/database/model/migration changes are authorized by this plan.

Alternatives considered: accepting baseline with permissive post-hoc limits is rejected; blocking all implementation until an owner supplies numbers leaves objective engineering work undone. Choose a strictly non-accepting Candidate rehearsal with declared proposals and complete gap reporting, while keeping actual Acceptance fail-closed pending approval.

Proposed budgets, selected for interactive behavior rather than to fit the slow baseline: detail/list p95 <= 1000 ms and p99 <= 2000 ms; create/edit/submit/approve p95 <= 2000 ms and p99 <= 4000 ms; each of the 22 cells >= 1 successful completion/second and >= 100 measured requests; zero unexpected errors. These are engineering proposals, not previously approved product requirements, universal UX standards or production SLOs. A sample floor prevents a handful of observations passing tail gates; it is not a statistical confidence claim.

## Review focus

- A nonempty `reviewedBy` string must not let a Proposed target pass Acceptance.
- A low-latency report with too few samples must fail target evaluation.
- Every failing metric/cell must be retained, not only the first thrown threshold error.
- Baseline provenance must come from branch ancestor `443e9ac2f0a51022de550437957f94a1e7dc6c23`, not the non-ancestor PR synthetic merge.
- Candidate success must never be labeled technical acceptance or close Issue #12.

## Task 1: Retain baseline and declare target proposals

Affected: `tests/Load/baselines/443e9ac2.load.json`, `tests/Load/baselines/443e9ac2.provenance.json`, `tests/Load/m7-targets.json`, `docs/decisions/m7-performance-targets.md`.

Consumes: branch run 42496484263 / artifact 13622894577, original sanitized `sql/load.json`, outer/inner/file SHA256 inventories and unchanged workload bytes.
Provides: byte-preserved baseline JSON, immutable artifact/checksum references, all 22 proposed target cells with `reviewStatus=Proposed`, no claimed owner approval, and rationale/limitations.

- [ ] Reverify archive hashes and every inventory entry before copying the baseline.
- [ ] Compare every baseline cell to sanitized raw samples and record the complete gap, never rewrite historical PASS/FAIL.
- [ ] Commit the proposed targets and baseline before any new Candidate/Acceptance workload starts.

## Task 2: Evidence-policy and orchestration contracts

Affected: `scripts/m7-evidence-policy.ps1`, `scripts/test-m7-evidence.ps1`, `scripts/test-m7-target-readiness.ps1`, `scripts/check-m7.ps1`, `scripts/m7-sql-fixture.ps1`, `.github/workflows/m7.yml`.

Consumes: existing `Assert-M7LoadReport`, `Assert-M7TargetTiming`, safe tracked-path and hash/ancestry checks, `Write-M7EvidencePackage`.
Provides: `Assert-M7TargetDefinition(Targets)` for structural validation; `Assert-M7Targets(Targets)` additionally enforces Approved status and real review metadata; `Get-M7TargetAssessment(Report, Targets)` returns all 22 per-cell outcomes with latency/throughput/sample failures. No helper performs I/O when dot-sourced.

- [ ] Add behavioral tests first: Proposed rejects Acceptance; Approved with missing reviewer rejects; incomplete/invalid sample floor rejects; exact boundaries pass; undersampled data fails; multiple simultaneous failures are all reported; historical baseline fails the proposal.
- [ ] Observe RED on pinned CI, then implement minimal policy changes and rerun to GREEN.
- [ ] Baseline accepts no target; Candidate uses tracked/provenance-locked definitions but cannot claim acceptance; Acceptance requires approved metadata before expensive work, retains its same-source inherited Full gate and final target-hash recheck.
- [ ] Persist `thresholds.json` before failing a threshold gate, including source/target hashes and all cell failures. Failure remains nonzero, without `continue-on-error`.
- [ ] Add CI policy preflight and selectable mode. Automatic PR/push run Candidate, not Acceptance; manual Acceptance remains an explicit operation. Keep permissions read-only and full history.

Candidate runs the M7 correctness/product/template/load gates and target assessment. The existing PR workflows separately exercise inherited M0-M6. It does not claim the embedded inherited Full requirement of Acceptance has run; Acceptance itself continues to execute and package that exact-source Full run.

## Task 3: Verify, review and record remaining blockers

Affected: `docs/runbooks/m7-load-and-acceptance.md`, `docs/verification/m7-target-readiness.md`, PR #14 and Issue #12 comments.

Commands: `pwsh -NoProfile -File scripts/check-powershell-syntax.ps1`; `pwsh -NoProfile -File scripts/test-m7-evidence.ps1`; `pwsh -NoProfile -File scripts/test-m7-target-readiness.ps1`; `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Candidate -Targets tests/Load/m7-targets.json`. After genuine target approval, use the same command with `-Mode Acceptance`.

- [ ] Inspect policy GREEN, exact mandatory TRX counts, correctness and threshold artifacts at the new SHA; distinguish branch and synthetic-merge sources.
- [ ] Independently verify archive/file hashes and assessment calculations, and record runtime/environment limitations.
- [ ] Report all current failed or running checks. Do not reuse prior inherited PASS as evidence for a new source.
- [ ] Keep draft/unmerged and Issue #12 open if targets are unapproved, thresholds fail, inherited gates are unfinished or final sign-off is absent.

Migration owner: not applicable; every existing exclusive stream stays unchanged. Performance optimization is a subsequent evidence-led task, not an excuse to change guards during this evidence-contract work.

## Execution ledger

Initial source: 443e9ac2f0a51022de550437957f94a1e7dc6c23. Source archive SHA and branch artifact outer/inner hashes plus 19 file hashes were reverified locally. Review environment has no dotnet/pwsh/Docker and cannot fetch the pinned binary directly; runtime tests therefore execute on pinned CI. The local Git snapshot is for editing only and is not remote runtime provenance.
