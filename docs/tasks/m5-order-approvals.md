# M5 OrderApprovals implementation plan

Spec: `docs/roadmap/saas-orchard-roadmap-v1.md` revision 1.3, M5 and sections 5.2, 5.5, 5.6, 6.1–6.3. Base: main `5488cbb8e6249363642fad51a6efa1adea9c7dfa` (tree `da300d72feed90d299b355f989c90eb62b5102bd`, includes PR #7 P3 fix).

## Design and proposed implementation choices

Use a separate `SaaS.OrderApprovals` project/context with exclusive `approvals` schema and migration history. It references Orders' contract; Orders, platform and shared theme never reference its implementation. Product host composes the handler. Reuse `TenantTransactionCoordinator`, Orders permissions, command serialization, revisions, receipt partition/fingerprint/retention, and current-authority replay policy. Do not build another command coordinator or dedup store.

Requests are immutable snapshots keyed by OrderId + submitted revision, recording creator, submitting actor and submitted rowversion. Decisions are append-only with the same unique key and FK to their request. A decision requires the exact submitted revision/version, a different actor from the creator, and a non-empty normalized rejection reason. Both contexts enlist on the caller's connection and transaction. Orders owns the one successful business audit and receipt; the handler never commits, retries independently or does external work.

The existing private plan/recovery protocol installs and verifies schema before publishing profile v3. Standard installs the schema but exposes no decision action/menu; retaining the schema makes historical data readable to the entitlement-independent recovery reader. This is an implementation proposal, not an earlier user requirement. Disabling Approval never drops tables or resets markers. Unknown profiles or corrupt/missing approval history fail closed. Upgrading a pre-M5 database with fixture approval history is refused rather than fabricating requests/decisions.

API decisions: POST `/api/v1/orders/{id}/approve` and `/reject`, with Idempotency-Key, expectedRevision, expectedVersion, and reason only for reject. Web decisions use equivalent prefixed POST routes, strict forms and antiforgery. No arbitrary tenant ID, marker, actor or client fields are accepted. Decision controls require current entitlement/permission, visibility and non-ownership. SQL restore/release remains M6.

## Tasks and verification

- [x] T1: Add the failing route test before implementation, then real-approval tests; retain observed route RED evidence. Files: `tests/Unit/SaaS.UnitTests/M5*`, `.github/workflows/m5.yml`. Validate that missing approval behavior is the failure, not a test compile/config error.
- [x] T2: Implement `src/Modules/SaaS.OrderApprovals/{Data,OrderApprovalHandler.cs,ApprovalHistoryVerifier.cs}`. Consume IOrderApprovalHandler/TenantTransaction. Provide ApprovalRequests/Decisions, exclusive migration stream, append-only guards and exact revision validation. Test real SQL atomicity, self-decision, reasons, duplicate decisions, stale revision, audit/handler failures, replay and authorization revocation.
- [x] T3: Compose in `src/SaaS.Web/{Access,Orders}`; update host/solution references and generated NuGet locks. Provide strict API/web actions and permission-aware controls. Test Standard direct calls, forged fields, CSRF, independent tenants, UI/API parity and current authority failure.
- [x] T4: Connect real data to existing plan/recovery flows; update profile manifest/readiness and runtime grants. Test outstanding Pending/Rejected/rework Draft, resubmit/approve/downgrade, feature-off history, restore fixture recovery child gate, drain races, checkpoints, retries and stale workers. No SQL restore claim.
- [x] T5: Generate/commit migration and snapshot using pinned EF tooling; locked restore/build, architecture and full inherited gates plus named mandatory M5 inventory. Publish exact SHA, exit codes, totals and skipped counts in `docs/verification/m5.md`; update runbook and PR. Do not merge without owner review.

## Failure focus

Current permission denial precedes replay/fingerprint disclosure. A lost response with old expected revision replays once; a new key with old revision conflicts. Missing approval schema/history must not become Standard confirmation. Late downgrade workers cannot reopen a newer suspend/delete. The child recovery upgrade must not open its parent gate. Runtime principals cannot alter/delete approval history or migrate schema.

## Environment and evidence

This session's local runtime has no dotnet/PowerShell and cannot reach GitHub directly. The source artifact's git tree was verified equal to main. Build/SQL evidence is obtained through GitHub Actions on pinned toolchain and disposable SQL Server, never simulated with SQLite/InMemory. NOT RUN/BLOCKED is not PASS. Architectural/security choices remain subject to owner review.

## Evidence checkpoints

The component gate on code head `7e21f054502d555cb663c4843d232dbd33117aeb` (tested PR merge `53b7c2343024edf245636dcee778d7584e2c421d`) passed all 32 named real SQL/native HTTP/plan/recovery cases in run `42372365777`, with zero failures or skips. Full same-SHA acceptance also passed in run `42372365772`: 464 discovered/executed/passed tests, zero failures/skips, all prerequisite reports and migration checks PASS; artifact `13574662657` and all seven TRX files were inspected. See `docs/verification/m5.md` for counts, commands, exits and the evidence digest. The final handoff changes only README, this checklist and the verification ledger, not tested runtime/test inputs. ADR 0008 is Proposed and requires owner review.
