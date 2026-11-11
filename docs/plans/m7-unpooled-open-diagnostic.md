# M7 unpooled-open diagnostic

Extends m7-routing-connections.md after the observed routing RED and its implementation at 7e0bac659cd3e719d70ecc8237f67e8f1d5dd4a8. No new production change in this task.

Primary-source inspection of pinned Microsoft.Data.SqlClient 6.1.7 DbConnectionFactory.cs shows s_pendingOpenNonPooled sized to Environment.ProcessorCount. The async non-pooled creation path queues against those slots and creates LongRunning continuations; synchronous Open does not traverse this queue. This is an implementation fact, not yet proof of the application bottleneck or a reason to change the production opener.

Source: https://github.com/dotnet/SqlClient/blob/v6.1.7/src/Microsoft.Data.SqlClient/src/Microsoft/Data/ProviderBase/DbConnectionFactory.cs

Add one test-only SqlM1 diagnostic class, tests/Unit/SaaS.UnitTests/M7/M7ConnectionDiagnosticsTests.cs, using the existing isolated real SQL fixture and its low-privilege customer runtime connection. Run equal 32-worker/8-op batches in async/synchronous/synchronous/async order, after a small warm-up. Each operation creates a new unpooled encrypted connection, executes SELECT 1 and disposes it. Synchronous mode uses bounded dedicated test workers, NOT a replacement production API, thread-pool change or connection pool.

Export only mode, counts, elapsed time, aggregate/percentile open latency, client CPU, driver assembly version and logical processor count via structured test output. Never log connection/SQL parameters, server/login names, credentials, exception payloads or raw diagnostics. SQL text is the fixed harmless SELECT 1 and is not exported. Validate fresh physical-open counts, successful query counts, no pending/error operations, and Pooling=false. Do not assert a speed ratio: slow/fast results are diagnostic observations, not a test to game or an M7 acceptance decision.

The experiment runs in the inherited SQL gate, outside M7 workload/setup, so the load hash, targets, deadline and measured cohort remain byte-identical. A diagnostic failure fails the inherited suite; no skip. No database model, table, migration stream, schema or permission changes. Record the exact source/run and retain TRX output. Review measurement before selecting any scheduling change; no unsafe global pooling, CPU-count spoofing, canceled-task leak or increased connection deadline is authorized by this diagnostic.

Commands: scripts/check.ps1 -Scope M0; scripts/m1-sql-fixture.ps1; inherited Full and M7 Candidate at exact source via pinned CI. Editing environment cannot execute dotnet/pwsh locally. This is test instrumentation, not a production feature implementation; no runtime speedup is claimed before results.
