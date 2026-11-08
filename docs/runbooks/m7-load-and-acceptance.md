# M7 — Four-tenant load and acceptance

## Run an implementation baseline

Use a clean checkout, the exact SDK in `global.json`, PowerShell 7.6.9, x64 and Docker configured
for Linux containers. The runner creates a fresh pinned SQL Server container; it does not accept
an external SQL target. Never run multiple verification processes in the same working directory.

```powershell
pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Baseline
```

The command checks PowerShell syntax and evidence-policy negatives, runs M0/non-SQL architecture
and unit checks, independently publishes Portal, generates/builds a fresh module, then runs every
case in `scripts/m7-mandatory-cases.json`. Zero, missing, duplicate, unexpected or skipped cases fail.
The load configuration is `tests/Load/m7-baseline.json`, not environment-injected numbers.
A successful Baseline report is **not M7 acceptance** and does not establish production capacity.

The fixture uses real Kestrel HTTPS, pinned ephemeral localhost certificates, native Orchard/OpenIddict
code/PKCE grants, controlled provisioning/migration and real SQL Server. Alpha/Delta use Standard;
Beta/Gamma use Approval. All four are Active during load. Lifecycle tests restore their state and
are run outside the measured all-Active window. Expected denial/fault responses belong to correctness
tests, not successful load transactions.

There are 25 virtual users per tenant (100 total), a 30-second ramp, a 120-second measurement window
and a 120-second load-client observation deadline. The earlier 60-second measurement / 30-second
client-deadline experiment failed and remains failed; see `docs/verification/m7-observation-2.md`
for its exact evidence, the reason for a longer observation and the interpretation limits.
**The client deadline is not an accepted latency target.** Identity setup and correctness clients
retain their original 30-second deadline. No production timeout or guard is changed.

Five creator accounts per tenant supply 100 separate grants; two reviewer accounts, shared across
the two approval tenants, supply four reviewer grants. Thus 100 virtual sessions are **not 100
separate enrolled people**. Extra actors prove shared-user role differences, explicit Gamma Deny,
Alpha-only membership and absent membership. The private operator uses the existing guarded
operations path; its privileged credentials are never given to the independent Portal process.

Each load transaction creates, edits and submits an order, approves on Approval tenants, then reads
detail and a permission-filtered list. The driver validates identities, canonical order numbers,
revisions, final state and persisted audit/approval counts. It does not treat a bare HTTP 200 as
successful business work. All phases' transport failures, unexpected statuses and payload failures
fail the technical gate; drain failures are not hidden. There are no automatic retries.

## Interpret the evidence

Fresh ZIPs are written to `artifacts/m7-runs/` with SHA256 sidecars. Each contains source SHA,
clean-source proof, commands/exit codes, mandatory TRX inventory/counts, product package/native
feature/publish inventories, sanitized SQL/test output, workload JSON and a file/hash inventory.
The workflow retains uploaded artifacts for seven days; copy an approved evidence bundle to the
project's retained evidence store before expiry. No credentials, certificates, token payloads,
App_Data, database backups or arbitrary child output belong in the bundle.

`sql/load.json` contains 22 tenant/operation cells. Each reports sample count, nearest-rank
p50/p95/p99/max, successful completions/second and error counts. Latencies include all requests
**started** inside the measurement window, including their drain time. Throughput counts successful
requests **completed** inside that window divided by its seconds. These are intentionally different
cohorts. `load-samples.json` preserves sanitized per-request timings/statuses for independent review.
Do not average percentiles across tenants or treat ramp/drain time as measurement duration. Small
sample counts do not support a high-confidence population p99 claim.

The topology is explicitly limited: load generator and application share one process/host; SQL is
a separate local Docker container. Reports record CPU/core count, memory, GC, runtime/OS, runner
image, SQL image digest and version. They are not distributed-client, internet-latency or production
capacity measurements. CPU/GC deltas end before post-load integrity queries; peak working set is
a process-lifetime high-water mark. SQL-container CPU/wait profiling is separate, not inferred
from application CPU alone.

## Lock targets before acceptance

First retain a **passing** baseline `load.json` from a source commit that is an ancestor of the
acceptance checkout. A PR synthetic merge SHA is not automatically an ancestor of its source branch;
use the branch push baseline, or rerun on main after integration. Commit the unchanged sanitized
baseline under `tests/Load/baselines/` and retain the original artifact and checksums.

The owner chooses numeric targets after reviewing that baseline, workload, hardware and bottlenecks.
Create and commit a target JSON document with this contract (there are deliberately no default
latency/throughput values to auto-pass):

- `schemaVersion`: 1; `loadConfigSha256`: lowercase SHA256 of the committed load configuration.
- `baseline`: `sourceSha`, `reportPath`, `reportSha256`, and retained `artifactReference`.
- `reviewedBy` and `decisionReference`: the real owner/review record, not an implementer's approval.
- `maxUnexpectedErrors`: 0.
- `limits`: exactly one entry for each of the 22 cells, with `tenant`, `operation`, positive numeric
  `p95Ms`, `p99Ms` (at least p95), and `minSuccessfulRequestsPerSecond`.

The target commit must be later than baseline completion and earlier than the acceptance run. Git
history must be available. Missing/untracked/altered targets, missing baseline, incorrect hashes,
wrong source ancestry, changed workload, empty cells and invalid statistics block/fail the gate.
The target file hash is rechecked after execution. A reviewer identity field is provenance to review,
not cryptographic proof of owner approval and not permission to close the milestone automatically.

```powershell
pwsh -NoProfile -File scripts/check-m7.ps1 -Mode Acceptance -Targets tests/Load/m7-targets.json
```

Acceptance also runs `scripts/local-verify.ps1 -Scope Full` for inherited M0–M6 gates at the same
source, including restore/revocation/replay/audit/feature/migration failure cases. It packages that
fresh inherited evidence and compares the new load against the precommitted targets. Even technical
success leaves `ownerReview=PENDING`; PR review, merge and the explicit #12 sign-off remain human
release decisions. A missing target is BLOCKED, never a skip or invented threshold.
