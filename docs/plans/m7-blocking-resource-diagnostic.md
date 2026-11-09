# M7 blocking-resource diagnostic plan

Goal: identify the remaining SQL lock contention before changing queries, indexes, isolation or transaction boundaries. This extends the approved M7 performance work. Spec: roadmap revision 1.3 M7, AGENTS.md, m7-target-approval.md. No production runtime change is authorized by this diagnostic itself.

Evidence: routing-only PR observation at 7e0bac6 still failed all approved targets, despite reducing opens/request from about 43 to 31. It recorded substantial LCK_M_S/LCK_M_X wait deltas. The isolated ABBA login diagnostic did not show a material warmed synchronous-open advantage. Aggregate waits alone do not identify the resource or justify a scheduling/pooling change.

## Scope and interfaces

Create tests/Unit/SaaS.UnitTests/M7/M7BlockingProbe.cs. Constructor receives an already-open private-fixture admin SqlConnection plus fixed database-to-label mappings. Only saas_platform and saas_t_<32 lowercase hex> identifiers and fixed labels platform/alpha/beta/gamma/delta/test are accepted. ReadAsync(CancellationToken) reads waiting lock metadata joined to sys.partitions/sys.objects, returning only fixed resource buckets, wait/resource/mode categories, observation counts and maximum observed wait. No SQL text, query plans, parameter values, session IDs, resource descriptions/hashes, database/user/login names, credentials or raw exceptions are exported. Unknown objects map to Other.

Create M7BlockingProbeTests.cs: non-SQL identifier/label rejection and a real SQL behavioral test in the EXISTING M1SqlTests partial fixture. Hold a no-op update lock on the fixture WriteGate row, start a waiting reader, and require the probe to identify tenancy.WriteGate within a bounded poll loop. Roll back/release the waiter in finally. An empty probe must fail the test; no test-only duplicate database initialization. SQL errors fail, not skip.

After behavioral RED, implement the metadata query. The query uses only SQL-quoted, validated fixture database identifiers, fixed labels and system metadata. It does not change SQL schema, data, isolation, permissions or production guards. The probe does not own the supplied connection and must not close it.

Integrate into M7SqlProfileSession and M7LoadDriver: open ONE private admin observation connection before starting the client-operation observer; poll once per second through load/ramp/drain; stop and await polling before taking the final snapshot. Keep the loop finite with a five-second observation-query timeout. Delay cancellation must not cancel an in-flight query and manufacture a client operation error; always drain/close on disposal. Record successful observation-query count separately. Client command totals/timings include those monitoring queries and must be labeled; adjusted application command count subtracts exactly that count. Application open counts exclude the pre-opened observation transport. Server counter deltas can include administrative snapshot traffic and must not be equated blindly to application opens.

Export sampled task observations, sample count, maximum observed wait, and query overhead. Do NOT add repeated wait durations across snapshots or call the result total blocked time/CPU utilization. Zero waiting observations is legitimate; zero executed samples or a sampling error fails the diagnostic. Monitoring introduces overhead; record it and do not attribute all latency differences to a runtime optimization.

## Test and evidence sequence

1. Commit harmless empty probe plus tests; observe the real waiting-row test fail for no observations, not compilation/setup.
2. Implement probe and run the same test GREEN; integrate polling without changing targets, workload, VU/account population or deadline.
3. Fresh pinned M0, M1/M2, M7 Candidate and inherited Full at exact source. Download/hash/TRX/statistics checks; preserve native/routing and diagnostic failures. Keep seven mandatory M7 cases unchanged.
4. Use resource attribution to scope a later controlled change only if the observations justify it. No performance or Acceptance PASS follows from a passing probe test.

Sources: Microsoft Learn sys.dm_tran_locks and SQL Server blocking guidance document resource_associated_entity_id/HoBt mapping and the waiting-task join. Sampling materializes live lock-manager data and has overhead; it is confined to the disposable test instance.
https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-tran-locks-transact-sql
https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/performance/understand-resolve-blocking

Migration owner: none; all model/history streams unchanged. Editing environment lacks dotnet/pwsh/Docker and cannot resolve external package hosts; no local runtime execution is claimed. PR #14 remains draft and Issue #12 unaccepted until separate Acceptance, source-matched Full and final review/sign-off.
