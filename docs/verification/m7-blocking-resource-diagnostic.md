# M7 blocking-resource diagnostic ledger

Plan docs/plans/m7-blocking-resource-diagnostic.md was committed at e9637d1004cd73d793a625d16e03e0d90311a10f before implementation. This is diagnostic evidence, not latency acceptance.

## Behavioral RED

Test-only head 71eaa24492e1fd74338bf7d80aeadd92e26b02af, PR merge f2e5cb2c75e336bce1a028599c112d16ed38571c. M1 run 42545964259, artifact 13643611857. Outer SHA256 a80a28c0f104efac954bc7ee96a1b438ce1d08dbe31b2150cd55fe6fa83ccbb3 was checked after download.

Non-SQL: 198 unit +27 architecture =225 Passed. SQL M1: 53 discovered/executed, 52 passed, 1 failed, zero skipped. Only Blocking_probe_observes_a_real_wait_without_exporting_identifiers_or_closing_its_connection failed: expected the held WriteGate row in the sampled resources, but received an empty collection. This is behavioral RED with a real blocking SQL transaction, not a setup/compilation error.

## Implementation boundary

The probe reads live waiting-lock metadata and fixed object buckets, never SQL text/plans, raw resource hashes/descriptions or private identifiers. Its validated fixture database map is loaded privately from the fixed platform registry before load/profiling, rather than changing the load-driver API. The observer connection is pre-opened before application operation counting. One sampled query per second has a five-second timeout; shutdown drains a current query instead of canceling it into a false operation error.

Report schema 2 separates successful monitoring-query count and adjusted application-command count. Client command durations include monitor overhead and cannot be interpreted as pure application timings; open counts exclude the pre-opened monitor. Task observations are samples, not unique requests; repeated wait durations are not summed. Sampling errors/zero samples fail; zero waits alone is valid. No workload/target/production query/lock/isolation/model/migration change.

## Earlier gates retained

Routing change 7e0bac659cd3e719d70ecc8237f67e8f1d5dd4a8: M2 run 42541749315 / artifact 13641527536 at 63c422eec7f9f87b16e8128abac07cbd08aba701 passed 47/47 M2 SQL and 51/51 M1 SQL. The routing readiness and disabled-login regressions passed. Candidate run 42541749293 / artifact 13641798011 still failed 22 cells/88 metrics. Source-bound open count was 31.0900058445/request; 9.4833333333 measured requests/s; per-cell p95 8455.9195–19624.2379 ms. All 24 inventory hashes were checked.

Combined-native implementation fb8e097003c164ca0cb9b9adac0695eefc7ed674: M2 run 42545305755 / artifact 13642643517 at 10b38ba79276647582e38d4afe01ef5363a051a2 passed 48/48 M2 SQL and 52/52 M1 SQL. One native connection preserves the same query count, account-error order and immediate grant revocation. Candidate run 42545305904 / artifact 13643641060 still failed 22 cells/88 metrics. Source-bound count was 26.7364690722 opens/request, 8.4083333333 measured requests/s; p95 8893.078–20896.8761 ms. Fewer opens did not establish a consistent latency improvement; do not equate mechanism proof to accepted performance.

The separate login diagnostic's initial duplicate-fixture failure and native test's missing-import compile failure remain FAIL. Their later fixes are not retroactive GREEN. All prior RED ledgers and owner-approved numeric targets remain unchanged.

Fresh CI at this diagnostic implementation SHA must establish GREEN and produce the resource report. Runtime testing is on pinned CI, not locally. A passing diagnostic does not grant M7 Acceptance or independent reviewer/final owner sign-off.
