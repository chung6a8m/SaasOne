# M7: Batch fresh five-owner migration history verification — Implementation Plan

> For agentic workers: execute this plan using RED–GREEN–REFACTOR, source-matched CI and an independent review gate. Do not change owner-approved targets.

**Goal:** Reduce SQL client round trips for M7 request-entry and post-lock profile verification while still re-reading and checking the entire current migration history of all five schema owners on every invocation.

**Architecture:** Product host `ApplicationAccessProfile.VerifyAsync` currently calls `TenancyConnections.VerifySchemaAsync` five times on the caller's already-open SQL connection, then `ApprovalHistoryVerifier.DemandColumnsAsync` and application manifest lookup. Keep the existing EF `GetMigrations()` values for each owner's expected history but fetch all five actual migration tables in one fixed, read-only UNION ALL SQL command and compare the sorted, exact per-owner sequences. Leave the column and manifest checks unchanged and fresh. This method has no transaction owner or connection-lifetime changes.

**Tech stack:** .NET 10, EF Core 10, Microsoft.Data.SqlClient, SQL Server 2022, PowerShell 7.6, GitHub Actions.

**Spec:** `docs/roadmap/saas-orchard-roadmap-v1.md` r1.3 §M7 and §8; Issue #12; `AGENTS.md`; owner-approved `tests/Load/m7-targets.json`; ADR 0003 data/migration ownership and 0002 source authority.

## Global constraints

- Product host only; platform/modules/themes must not acquire a dependency on the product, Orders or Approvals.
- No migration, model or history table changes; owner labels are the fixed existing five: tenancy/access/audit/orders/approvals.
- Never cache a positive schema, identity, authorization or role decision; re-run every migration-history query on every request.
- On absent/extra/duplicate migration, missing table, bad column, mismatched manifest, canceled query or unavailable database: fail closed with original boundary classification.
- Preserve the caller-owned connection, tenant routing, physical SQL principals, `Pooling=false`, current ACL/revocation timing, post-lock rechecks, mandatory test inventory, 100-VU workload, and approved target budgets.
- Do not claim M7 Acceptance from a Candidate or from an inherited Full on a different SHA.

## Review focus

1. Removed or appended owner migration record must reject the next verification; tests assert mismatch instead of silently accepting a subset.
2. A missing history table or required Approvals column must reject the profile, never return an empty valid history.
3. Expected migration list is read from each exact owner EF context, not duplicated hardcoded migration IDs.
4. No command may implicitly close/commit a caller's connection or transaction; every read is fresh.
5. Source-matched M0–M6, M7 SQL mandatory seven, non-SQL, product graph, test inventory and evidence hash must remain intact.

## Task 1: behavioral RED for client-command budget

**Test:** `tests/Unit/SaaS.UnitTests/M5UpgradeSqlTests.cs`, existing mandatory `Existing_M4_Standard_orders_upgrade_without_data_loss_or_fabricated_approval_history`. Add a test-only SqlClient observer scoped to the exact caller-owned SQL connection; wrap its existing `ApplicationAccessProfile.VerifyAsync` call and require **3 client SQL commands**: one complete five-owner histories query, one Approvals-column check and one current application profile check. This is a strict, reproducible single-method budget, not the whole workload. Keep the existing test name and original upgrade/history assertions.

- [ ] Commit test first.
- [ ] Run M5 components/full SQL test, confirm RED from observed command count (not build/setup error).
- [ ] Keep this RED evidence; never relabel it.

## Task 2: minimal read-only history batch

**Implementation:** `src/SaaS.Web/Access/ApplicationAccessProfile.cs`.

- [ ] Replace only `foreach (var db in new DbContext[] ...) await TenancyConnections.VerifySchemaAsync(db, ct)` inside `VerifyAsync` with a dedicated `VerifySchemaHistoriesAsync(DbConnection connection, IReadOnlyList<(string Owner, DbContext Context)> contexts, CancellationToken ct)` method (or an equivalent private focused helper).
- [ ] For every `Context.Database.GetMigrations()`, require non-empty expected history; compare ordinal ordered sequence with actual rows.
- [ ] Execute one fixed, literal SELECT/UNION ALL with owner tags and MigrationId from all five `[owner].[__EFMigrationsHistory]` tables; preserve exact per-owner duplicates/counts, reject extra/unknown owner, do not swallow SQL errors.
- [ ] Leave `ApprovalHistoryVerifier.DemandColumnsAsync` and current manifest query intact. No new source of tenant IDs, no schema writes, no cached allow.
- [ ] Run fresh M5, inherited M0–M6 and M7 Candidate at the new exact head and PR merge SHA, verify zero failed/skipped mandatory cases and raw workload numbers; do not merge until acceptance gates pass.

**Owner / status:** Existing migration owners unchanged. Performance tactic is an implementation selection, not a new architectural approval or latency SLA. Issue #12 remains open / PR #14 draft until exact-source separate Acceptance, independent review and owner signoff.
