# M7 — reuse Orders preflight SQL login for its own transaction (2026-11-09)

> Narrow, test-first optimization. This does **not** approve pooling, authority snapshot reuse, lock changes or M7 acceptance. PR #14 remains Draft; Issue #12 remains open.

## Source-matched evidence and hypothesis

At source `cb331221614b8d23a3b04b4a549a42fbcb075a13`, M7 PR Candidate run `42567358813` (artifact `13652237104`, 25-file inner inventory, 7/7 mandatory SQL, no HTTP/integrity errors) fails **22/22 performance cells, 88/88 metrics**. Test-only SQL diagnostics show 42,875 physical opens for 1,603 completed requests (**26.7467 opens/request**) and 192,301 commands, with 162 live blocking samples including Orders resource/KEY wait. The `orders.read` and `orders.write` stages themselves account for 588+1,015 opens, and the separate `tenant.transaction` stage for another 1,603 opens. **Source confirms** `OrderService.InTransactionAsync` first opens one connection for `TenantWriteGate.ReadAsync`, closes it, then `TenantTransactionCoordinator.ExecuteAsync` opens a second connection for the same logical operation. It rechecks WriteGate under a transaction-owned shared lock; the preflight version is only a fence, not permission caching.

**Hypothesis:** Retaining the already-open, fixed-source, unpooled preflight connection until after the current transaction commits/rolls back saves *exactly one* physical login per Orders service operation. This does not directly fix the much larger native-identity open cost or current Orders lock hold. No tail-latency benefit is claimed in advance.

## Locked scope and contracts

- **Modify:** `src/Platform/SaaS.Tenancy/Operations/TenantTransactionCoordinator.cs`; add `ExecuteWithPreflightAsync` that itself opens the *same fixed constructor-selected connection*, invokes a preflight callback once before the transaction, then shares that connection with the unchanged internal transaction path. Existing `ExecuteAsync` still opens/owns its own connection. The caller must never supply a connection string or arbitrary open connection. A single non-retrying transaction encloses the action and audit as before.
- **Modify:** `src/Modules/SaaS.Orders/OrderService.cs`, only `InTransactionAsync`: call `ExecuteWithPreflightAsync`; preserve exactly the current `TenantWriteGate.ReadAsync(sql,null,ct)`, TenantId, IsOpen and gate version checks, then the existing `TenantWriteGate.AcquireWriterAsync` post-lock recheck by the coordinator. No extra step is skipped and no result cached.
- **Test-first:** `tests/Unit/SaaS.UnitTests/M4OrderSqlTests.cs`, amend existing mandatory test `Create_persists_one_order_revision_audit_and_receipt_and_reads_the_same_rowversion` to assert one SQL physical open around *only* the Orders Create command (existing harness currentAccess does not open SQL), then still verify Get/Receipt/Revision/Audit. Existing mandatory M4 tests cover closed gate, revoke/replay, parallel different keys and one-winner, handler rollback, and atomic audit failure. Assert the one-open property on failing closed gate too, without adding/renaming the mandatory inventory.
- **Provided interface:** `Task<T> ExecuteWithPreflightAsync<T>(Guid tenantId, Func<SqlConnection,CancellationToken,Task<long>> preflight, Func<TenantTransaction,CancellationToken,Task<T>> action, CancellationToken ct)`. The coordinator owns and disposes `SqlConnection`; the callback returns only a fresh gate version. No new `DbContext`, public factory or SQL provider dependencies.
- **No changes:** TenantId source, SQL roles, nonpooling contract, isolation/locks/timeouts, order/approval business rules, ACL Deny and all post-lock authority checks, native issuer revocation, rowversion, receipt uniqueness, audit ordering and atomicity, schema/migration/index, approved target/load hashes, population or 100 VUs.

## RED → GREEN → evidence gates

1. Commit just the assertion first. M4 SQL real-SQL suite must build but fail with **actual 2 physical opens vs expected 1** in the existing test; keep the RED run/artifact. Verify the unchanged mandatory case list.
2. Implement just the ownership-safe overload and the Orders call. The second WriteGate check still acquires the shared write-gate lock in the transaction and validates the original version. A closed/changed gate must fail without a command commit. Resource/command application locks remain transaction-owned until commit or rollback.
3. Verify M4 SQL exact inventory and inherited M0–M6 tests; check M7 SQL 7/7 and raw data integrity; compare source-matched Candidate artifact, connection count, lock waits/holds and 22/88 target results without claiming that performance improved from CI run-to-run noise.
4. Only if Candidate passes the locked targets and source-matched inherited Full passes, run **separate** Acceptance, source/artifact checksum/negative-path review and explicit owner signoff before merging/closing #12.

## Rollback

If runtime/test or exact-source CI fails, revert only this isolated optimization and retain evidence; no pooling or speculative lock reduction is authorized as a workaround.
