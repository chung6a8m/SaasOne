# M7 baseline observation at 151ba5d

This records a real, completed branch-push run, not owner latency acceptance or a
production-capacity claim. Issue #12 and draft PR #14 remain open.

## Provenance and verification

- Source: `151ba5decd51cdf72839f83a9b15df1c0902fb25`, clean checkout.
- GitHub run: `42495001912`; artifact: `13623182044`.
- Outer artifact SHA256: `9659f13e0b6eef2b9a9839884aba7a39dca0aa9cd35d3f52f6bcde36ee044aef`.
- Inner evidence ZIP SHA256: `9dea25f7343c87f4e45126930f56e10e684c9756ed92e87e938d9496740f25ac`.
- `sql/load.json` SHA256: `33e056fa8994480804975b332fe0e4628b3fb57e2005e2026b331ebfee4071fa`.
- Committed load-config SHA256: `694f09001e4ba2b40ba4ed07f93786daa8cb8fc0baaf7eaa9753507415f86adc`.
- Verification command: `pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Baseline`.
- Started 2026-11-08T15:29:00Z; completed 2026-11-08T15:36:52Z; exit 0.

The downloaded artifact's outer/inner ZIP hashes and all 19 inventory file hashes were
verified independently. Exact seven-case TRX inventory/counts matched. All 22 workload
cells' counts, nearest-rank percentiles and completion rates were recomputed from the
669 sanitized raw samples, not accepted solely because CI was green.

Syntax, 21 negative evidence-policy cases plus positives, locked solution build and
all 219 non-SQL tests passed (192 unit + 27 architecture; zero failed/skipped).
Independent Portal source/package/native-feature/publish checks passed (152 resolved
package/project entries, 3 native features, 237 published files). The actual generated
module template installed, generated, restored and built with Abstractions only.
All seven mandatory real-SQL/HTTPS cases passed, with zero missing/unexpected/skipped
cases. Published Portal runtime-only readiness/own theme and fail-closed cases passed.

## Measurement and limits

Environment: Ubuntu 22.04.5 x64, SDK 10.0.403, runtime .NET 10.0.14, PowerShell 7.6.9,
4 logical processors, MemTotal 16,372,448 KiB, runner image 20261101.347.1.
SQL Server 16.0.4305.5; image digest
`sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`.
Application and load generator share one process; SQL runs in a local container.

The source-locked observation uses 25 VUs per tenant, 100 total, a 30-second ramp,
120-second measurement and 120-second load-client observation deadline. Account pool:
20 creators and 2 reviewers; 100 creator grants and 4 reviewer grants. These are virtual
sessions, not 100 distinct enrolled users. All four tenants remained Active.

All 669 requests across ramp/measurement/drain passed status and business-payload checks;
zero transport errors, unexpected statuses or HTTP 5xx. There were 123 complete business
transactions including drain, with correct final states and audit/approval counts.
The measured window completed 357 requests (2.975 requests/second) and 23 full business
transactions (0.1917 transactions/second). Total elapsed including drain was 213.635 seconds.
Do not divide all 669 requests or all 123 transactions by the measurement window.

| Tenant | Measured create p95 | Measured approve p95 | Measured completed transactions / second |
|---|---:|---:|---:|
| Alpha (Standard) | 35.324 s | Not enabled | 0.0833 |
| Beta (Approval) | 36.529 s | 46.110 s | 0.0167 |
| Gamma (Approval) | 35.286 s | 48.811 s | 0.0167 |
| Delta (Standard) | 36.214 s | Not enabled | 0.0750 |

Across the 22 cells, observed p95 ranged from 25.618 to 53.911 seconds. Sample counts
per measured cell ranged from 3 to 24; tail estimates are descriptive observations,
not high-confidence population p99 estimates. Do not average per-cell percentiles.
CPU was 270.444 process CPU-seconds (31.648% of four available cores), sampled before
post-load integrity queries; process-lifetime peak working set was 421,195,776 bytes.
These measurements do not establish a SQL CPU/wait/locking root cause.

The earlier 30-second-deadline run remains FAIL and is retained in
`m7-observation-2.md`. Increasing the observation horizon did not improve core
performance or approve a 120-second service latency. This baseline passes correctness
and measurement-completeness checks, but its high latency is not an acceptable-service
claim. No performance target has been chosen or owner-approved.

## Separate PR packaging failure

The concurrent PR run `42495005340` at synthetic merge
`d0ada60b0f4a1757969cb86550898b54644a63cd` logged 219 non-SQL passes and seven SQL passes,
but failed evidence packaging and uploaded no artifact. It is still a failed workflow;
the successful branch artifact must not be presented as its result. The bracketed TRX
filename fix and behavioral packaging regression are scoped in
`docs/plans/m7-packaging-regression.md`; a fresh run is required for that subsequent SHA.

## Next acceptance decision

Retain this artifact before its seven-day CI retention expires. A reviewed baseline can
be committed unchanged under `tests/Load/baselines/` with the hash above. The owner must
choose service targets and decide whether performance work is needed; the current high
latencies must not be disguised by permissive post-hoc limits. Commit the reviewed
22-cell targets and exact configuration hash before the separate Acceptance run.
That run requires fresh inherited M0-M6 evidence at its own SHA and still leaves owner
sign-off pending. Neither this document nor baseline success closes M7.
