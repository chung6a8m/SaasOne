# M7 — batch fresh ACL snapshot reads (2026-11-09)

> **For agentic workers:** RED–GREEN–REFACTOR, source-matched CI and independent review are mandatory before merge. This implementation plan preserves the already-approved M7 target/workload.

**Goal:** Reduce repeated SQL client commands when loading the current ACL snapshot without relaxing tenant boundaries, transactional ACL locking, Deny precedence, fresh external authority, or the document-integrity hash.

**Architecture:** `AclRepository.ReadAsync(AccessDbContext db, CancellationToken)` currently issues five separate EF SELECTs on an already-enlisted customer connection: `State`, `Roles`, `RoleGrants`, `Assignments`, `UserGrants`. It is called under a transaction-owned shared or exclusive `saas-acl-admin-v1` lock by `AccessService`, `TransactionAccessReader` and recovery code, sometimes several times per request. Replace those SELECT round trips with **one fixed, read-only SQL command returning five ordered result sets**, retaining every row, the exact count limits and hash comparison. Retain transaction ownership entirely with the caller and never cache any snapshot.

**Tech:** .NET 10, EF Core 10, Microsoft.Data.SqlClient, SQL Server 2022, PowerShell 7.6. Spec: roadmap r1.3 M7/§8; Issue #12, `AGENTS.md`, M3 authorization/security contracts, owner-approved `tests/Load/m7-targets.json`.

## Global constraints

- No migration/model/index change and no new architectural dependency on Orders or Approvals.
- No new user-selected connection; use EF's existing caller-owned connection and enlisted SQL transaction.
- Each `ReadAsync` performs fresh SQL reads of all five tables; no cross-request/session/transaction ACL cache, no cached Allow, no silent fallback on missing table or data.
- Preserve role/grant/assignment/user override row limits (256, 65,536, 4,096, 4,096), exact canonical hash check, TenantId/SecurityGeneration/Revision, invalid/grant/orphan integrity fail closed, UI/API/restore behavior.
- Preserve the existing shared ACL application lock, lock order, post-lock rechecks, transaction visibility/rollback and current external authorization.
- Keep the 100-VU/4-tenant workload and 22 owner-approved target budgets unchanged. Candidate != Acceptance. PR #14 must stay draft; Issue #12 stays open until independent review and actual Acceptance PASS.

## Task 1 — behavioral RED (no mandatory inventory changes)

**Modify test:** `tests/Unit/SaaS.UnitTests/M3AccessSqlTests.cs`. In the existing mandatory `Administration_cannot_elevate_self_or_delegate_access_management` test, scope a test-only SQL diagnostic observer to the fixture's actual customer database **around the existing `service.ReadAsync(Ct)`**. Count only commands containing one of the five fixed ACL table reads (no SQL, parameters or DB name may be exported), assert **one command** for a complete ACL snapshot. The test keeps its prior Revision=1 assertion and existing M3 fail-closed/tamper tests.

- [ ] Commit the regression first.
- [ ] Run M3 components and verify a deliberate RED **five commands versus one**, with no setup/build failure and all other M3 SQL tests green.
- [ ] Preserve RED artifact and source SHA.

## Task 2 — minimal implementation

**Modify:** `src/Platform/SaaS.AccessControl/AccessService.cs`, only `AclRepository.ReadAsync`, using `db.Database.GetDbConnection()` and `db.Database.CurrentTransaction.GetDbTransaction()`, not an additional connection or transaction. Create a single SqlCommand with five literal, bounded SELECT statements in the original State/Roles/RoleGrants/Assignments/UserGrants order; read five result sets with `NextResultAsync`, build the original `AclDocument` and verify all limits, role references and `document.Hash()==state.AclHash`. Match missing State, SQL errors and corrupt snapshots to the existing fail-closed behavior.

**Interfaces:** unchanged `Task<AccessSnapshot> AclRepository.ReadAsync(AccessDbContext db, CancellationToken ct)`; caller owns opened connection and transaction. Other code and rows unchanged.

- [ ] Implement only after observed RED; no SELECT cache, no row suppression.
- [ ] Run new M3 RED→GREEN check and exact mandatory SQL M3 inventory (91/91), whole inherited M0–M6 and M7 seven mandatory SQL/HTTPS.
- [ ] Compare source-matched M7 branch and PR Candidate metric/target files, verify artifact ZIP SHA256, complete TRX inventory, 22 cell thresholds and unchanged target/load hashes. Record measured result even if FAIL.
- [ ] Review changes for source/tenant selection, security, transaction, audit and restore boundaries; do not label Acceptance PASS or merge unless approved targets actually pass and final owner decision is recorded.

## Review focus

1. Missing access.State / missing UserGrants / invalid schema source must return 503, not stale allow.
2. User Deny and role revocation immediately change authorization with same credentials; no cache.
3. Null, missing, duplicate, orphan, oversized, and tampered ACL rows must fail closed; preserve deterministic AclDocument canonical hash.
4. `AccessService.ChangeAsync` remains fully serialized, auditable and rollback-safe; caller transaction stays open for subsequent writes.
5. No current mandatory case is added or renamed, and M7 targets, workload and PR head evidence are never misrepresented.
