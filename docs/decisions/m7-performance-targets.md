# M7 performance targets — proposal, not approval

Status: **Proposed / owner review pending**. This records an engineering decision in response to the request to implement the remaining steps toward M7 acceptance. It is not a claim that the user previously selected these numbers, a formal APPROVED PR review, an independent review or a production SLO.

## Retained baseline

Use the branch-push baseline at `443e9ac2f0a51022de550437957f94a1e7dc6c23`, run `42496484263`, artifact `13622894577`. The exact sanitized load report is `tests/Load/baselines/443e9ac2.load.json`; provenance and checksums are beside it. The original outer ZIP has also been retained as an attachment to the implementation conversation. Copy it to the owner-controlled evidence store for durable retention; GitHub's seven-day artifact retention is not that store.

Outer and inner archive hashes and all 19 inventory entries were reverified. All 22 cell statistics were recomputed from 696 sanitized samples. This is historical evidence only, not a new execution. The branch source is an ancestor of this work; the PR synthetic merge is not used as the ancestry baseline.

The baseline has zero request failures but p95 latencies in tens of seconds and too few per-cell observations to support a meaningful tail-performance acceptance. It is not evidence that those latencies are appropriate for interactive use.

## Engineering proposal

| Operation | p95 upper bound | p99 upper bound | Successful completions/s, per tenant/operation | Measured requests, per cell |
|---|---:|---:|---:|---:|
| detail, list | 1,000 ms | 2,000 ms | 1 | 100 |
| create, edit, submit, approve where enabled | 2,000 ms | 4,000 ms | 1 | 100 |

Zero unexpected transport, HTTP, payload or correctness errors remain mandatory in every phase. No missing workload cell, skipped mandatory test or reduced VU count is permitted. Do not average tenant percentiles or use drain completions as measured-window throughput.

Rationale: reads should support prompt interactive navigation; state-changing/audited operations have a larger budget. The throughput floor asks for continuing useful progress in each tenant/operation, not merely eventual completion. The sample floor prevents 3–24 observed requests from satisfying a tail gate. These are proposed product-engineering budgets, not universal UX standards or statistically guaranteed population percentiles. The slow baseline fails all four proposed metrics in all 22 cells; the proposal was not fitted to make it pass.

Workload and topology are unchanged: 25 VUs/tenant, four Active tenants, 30-second ramp, 120-second measurement, 250-ms think time, 120-second observation deadline; 20 creator accounts and two reviewers behind 100 virtual sessions. Application and generator share a process, with local SQL Server. This remains a bounded fixture experiment, not production capacity evidence.

## Approval and execution contract

`tests/Load/m7-targets.json` is committed with `reviewStatus=Proposed` and `reviewedBy=null`. Candidate mode may validate and measure these definitions, but its report always says acceptance is not granted. A technical success in Candidate cannot authorize merge or close Issue #12.

Actual Acceptance requires a separately recorded owner target decision: confirm or replace the proposed budgets, name the real reviewer in `reviewedBy`, point `decisionReference` to that approval record and set `reviewStatus=Approved`. Commit that decision before starting a new Acceptance run. Do not retrofit an approval or loosen limits on a completed run. A reviewer string/status is reviewable provenance, not cryptographic proof; the final dossier must check the actual referenced decision.

Acceptance then runs fresh inherited Full and M7 gates at one exact source, verifies baseline ancestry and workload/target hashes, retains every threshold outcome, and still leaves final owner sign-off pending. Unapproved targets are BLOCKED, not a skip; threshold failures are FAIL, not baseline acceptance. No conditional ADR is promoted.

## Next performance work

A fresh Candidate run quantifies the gap; it is not an optimization. Profile SQL logon/connection costs, waits/blocking and application/driver resource use before choosing a runtime change. Do not infer a root cause from aggregate latency alone. Any pooling, request-scope reuse or guard change needs a separately reviewed plan and the inherited negative tests. Keep all failure history unchanged.
