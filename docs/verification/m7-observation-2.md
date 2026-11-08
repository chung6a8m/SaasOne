# M7 observation experiment 2 — not latency acceptance

## Preceding failure retained

Source `71b7ade358fa1438adf8073225bd8f43c5a8f1fe`, branch push run `42494138021`,
artifact `13622836448`, outer SHA256
`0bcc14a850d8d7c917b4251a63fca6a8f5a402ba4bb12288ce5a20cb33444f9c`.

The clean-source run passed syntax, 21 negative evidence-policy cases, 218 non-SQL tests
(191 unit and 27 architecture), the independent locked Portal publish/graph checks and actual
module-template install/generate/build smoke. SQL discovered seven mandatory cases: **six passed,
one failed, zero skipped**. Published Portal runtime-only readiness, own-theme rendering,
missing/wrong source, overprivileged principal and release-barrier cases now pass.

The remaining failure is the 100-VU load. All 22 tenant/operation cells were exercised, but the
30-second HTTP measurement deadline censored 22 requests (six edits, nine submissions, two approvals,
five lists). There were no observed HTTP 5xx or invalid-payload responses. All failures are still
errors; zero 5xx does not turn the run into a pass. The run completed 95 business transactions
including drain; several measured completion rates were zero and the persisted final-state/audit
checks correctly failed for interrupted transactions. It is not an accepted baseline.

Observed create p95 was 25.589–28.582 seconds across tenants; several other operation p95s reached
the 30-second cancellation boundary. This establishes high latency/censoring in this fixture,
not the cause of that latency. No SQL wait or CPU attribution has been measured. Do not describe
the application as able to sustain an acceptable interactive latency at 100 concurrent VUs.

## Next experiment locked in source before execution

Keep the same four tenants, 25 VUs each, account pool, 30-second ramp, 250-ms think time, native
HTTPS/identity, SQL patch, core guards, workload, no retries and zero-unexpected-error requirements.
Change only the observation horizon: measurement is 120 seconds rather than 60; the load client's
explicit `requestTimeoutSeconds` is 120 rather than 30. The request deadline is bounded and recorded
in the committed load configuration. Identity setup, correctness tests, SQL command/connect
limits and production runtime remain unchanged. A separate disposable HttpClient applies the
load-only deadline. CPU/GC counters are captured before post-load integrity queries.

Rationale: the first valid workload was censored at 30 seconds and many complete business flows
finished after the short measured window. This new experiment measures the slow tail and complete
flows instead of inferring their latency from cancelled requests. It is **not** a fix to core
performance and does **not** establish a 120-second service-level objective. The preceding run
remains FAIL. A later zero-error observation cannot be compared as a same-configuration latency
improvement or used to claim the old deadline was met.

All error, correctness, required-cell and final-state/audit assertions remain enforced. New tests
reject absent, unbounded or out-of-range observation deadlines. A fresh result may still fail.
Only after a valid observation may the owner select service latency/throughput targets. Those
reviewed targets and the exact load-configuration hash must be committed before the separate
Acceptance command; no target is selected by this document.

Command: `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Baseline`.
The result for this experiment is not recorded in this planning document; consult the artifact
at the exact tested SHA and the updated PR #14 / issue #12 evidence record.
