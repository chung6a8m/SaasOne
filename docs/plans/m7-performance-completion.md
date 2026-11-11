# M7 performance completion plan

Goal: execute the owner's approved targets without weakening correctness or security, preserving exact-source evidence and historical failures.
Spec: roadmap revision 1.3 M7 / section 8, AGENTS.md, Issue #12, docs/decisions/m7-target-approval.md. Starting head: 64b9afb719fb4d4eede3566f358d0d49cacd085c. Work remains on codex/m7-acceptance-reuse; main is untouched.

## Boundaries and approach

First instrument the unchanged load path; do not infer a root cause from aggregate latency. A test-only SqlClient diagnostic collector records connection-open and command durations/counts/errors, never SQL text, parameters, connection strings, subjects, credentials or exception messages. Capture aggregate SQL wait deltas and counters from the disposable fixture only. No production telemetry endpoint or permissions change. Compare connection establishment, command execution and SQL waits before selecting a change. Reject raising timeout, reducing population, relaxing approved targets or enabling cross-request pooling to manufacture PASS.

## Task 1 — approved precommitted targets

Files: tests/Load/m7-targets.json; docs/decisions/m7-target-approval.md; this plan. Preserve every numeric budget and workload/baseline hash. Set Approved, name the actual owner and reference the recorded decision. Approval is not final acceptance.

## Task 2 — profiling and safety tests

Files: tests/Unit/SaaS.UnitTests/M7/M7SqlProfile.cs, M7SqlProfileTests.cs, M7LoadAcceptanceTests.cs; scripts/check-m7.ps1 evidence-copy list. Consumes DiagnosticListener SQL before/after/error events and the disposable fixture admin connection. Provides sanitized sql-profile.json with source SHA, observed counts, errors, durations and server wait/counter deltas, with explicit concurrent-sum interpretation. Collector tests cover pairing, duplicate/orphan/error events, unrelated events and no sensitive payload export. Require populated actual connection/command observations during load. Existing seven mandatory SQL tests and workload stay unchanged.

Run the unchanged runtime with profiling under Candidate first. Record real CI artifacts and checksum them. These profiling results must precede a performance fix; runtime results are unavailable locally because dotnet/pwsh are absent. Static inspection/local snapshots are not remote runtime provenance.

## Task 3 — controlled performance change

After evidence identifies the dominant source, append exact affected files and behavioral regression to this plan before changing runtime. Prefer reducing redundant physical opens inside an existing bounded operation without caching authorization decisions or changing transaction/lock order. Every new authorization must still read current authority. Connection lifetime must be disposed on success/error/cancellation, never shared concurrently or across requests/tenants. Keep Pooling=false, database selection and principal/schema/identity/release checks. No database model/migration/schema change is planned. If evidence indicates another cause, record that finding rather than guessing a fix.

Observe a regression fail before implementation, then pass after. Run all inherited negative/revocation/source-failure/restore/plan-change/replay gates plus M7. No missing/zero/mandatory-skip evidence.

## Task 4 — acceptance dossier

Commands: pwsh -NoProfile -File scripts/check-powershell-syntax.ps1; scripts/check.ps1 -Scope M0; scripts/check-m7.ps1 -Mode Candidate -Targets tests/Load/m7-targets.json; scripts/check-m7.ps1 -Mode Acceptance -Targets tests/Load/m7-targets.json (includes fresh inherited Full). Use pinned CI toolchain. Require all numeric targets, sample floors, source/target/workload hashes and clean checkout. Compare PR synthetic merge evidence separately from branch evidence. Retain failed experiments; no retroactive approval of old runs. Update PR #14 and Issue #12 with real outcome, remaining blockers and final review; keep draft/unmerged unless every integration condition is satisfied.

Migration owner: none added; all existing streams remain untouched.
