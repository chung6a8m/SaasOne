# M7 ordered page resource-lock batching (2026-11-09)

Spec: roadmap r1.3 M7, Issue #12, AGENTS.md, approved target decision. Branch: `codex/m7-acceptance-reuse`; M7 is not accepted. No migration owner changes.

## Observed problem

The source-matched PR Candidate at `e3a36f6c5a15fdf30a113182a589a80aa0c9b89e` has correct 7/7 mandatory SQL tests but fails all 22 approved latency/throughput/sample cells. The 100-VU run reported about 196 SQL commands/request and observed `orders-resource` lock waits. `OrderService.ListAsync` acquires up to 51 ordered shared resource locks using one SQL round trip for each. This change targets only the round trips; it does not assume contention will disappear.

## Task and interfaces

- Test: `tests/Unit/SaaS.UnitTests/M4OrderListSqlTests.cs`. Seed 51 actual rows; assert list retains exactly 50 items + pagination sentinel and that resource lock acquisition uses one SqlClient command, plus existing real-SQL ordering/isolation and shared-lock wait checks.
- Implement: `src/Platform/SaaS.Tenancy/Operations/TenantWriteGate.cs`: `SqlTransactionLocks.AcquireOrderedSharedAsync(SqlConnection, SqlTransaction, IReadOnlyList<string>, int, CancellationToken)`. Validate transaction connection, bounds (0..51), resource characters and uniqueness; issue a single parameterized T-SQL batch that invokes `sp_getapplock` once **per** resource, in supplied order, `Shared`, `Transaction`, `public`, same 30,000ms timeout per lock. Return the same negative-result error mapping as the single lock path. Empty resources must not run an SQL batch; error/cancellation disposes the command, transaction owner still rolls back.
- Consume: `src/Modules/SaaS.Orders/OrderService.cs` `ListAsync`, where `ids.Order()` is already defined and must be retained. Replace 51 client calls with the bounded batch only. No change to list permissions, fresh post-lock authority, DB state/snapshot/history check, pagination, transaction or lock lifetime.
- RED: commit regression before implementation and verify it fails on full 51-row page for resource-command count, not setup/compiler.
- GREEN: run M4 SQL (and inherited M0–M6) and M7 Candidate on exact PR merge SHA; verify source, no skipped mandatory tests, artifacts and unchanged workload/target hashes. Keep M7 acceptance blocked unless all 22 cells pass.

Boundaries: no cross-request authorization cache, no pooling, no fallback on lock failure, no weaker permission or integrity guard, no migration change, no lock-mode/order change. No production readiness or signoff claimed.
