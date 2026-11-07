# M5 OrderApprovals operations

## Composition and ownership

The product host composes `SaaS.OrderApprovals` through `IOrderApprovalHandler`. Orders does not reference its implementation; platform and shared theme remain product-independent. `ApprovalsDbContext` exclusively owns `approvals.Requests`, `approvals.Decisions` and its `approvals.__EFMigrationsHistory`. Request and decision PKs are `(OrderId, SubmittedRevision)`; each decision references a request. Historical rows are append-only at both EF and runtime SQL permission boundaries.

No runtime migrations, independent commits, independent retries or distributed transactions are introduced. Both contexts use the existing tenant connection/transaction; Order/Audit/Receipt remain owned by the Orders command. Failures after either approval write roll back the entire command.

## Deployment/profile upgrade

Use the existing private `access-install` / `ops-plan` channel under the M3 common resource lock, registry fencing and durable drain gate. The reconciler installs the separate Approvals migration before publishing application profile version 3. Both Standard and Approval retain schema and historical data. Standard has no usable approval action or decision controls; runtime credentials cannot rewrite history, delete history or migrate schema.

Version 1 (M3) and version 2 (M4) manifests are recognized only for controlled upgrade. A pre-M5 fixture that already contains approval-required history but no genuine requests/decisions is **not** automatically upgraded or backfilled. Missing/unknown schema, future profile, inconsistent revision histories and orphan/missing approval records remain closed and require operator investigation. Do not erase Orders markers, synthesize decisions or manually toggle a plan to bypass this guard. Downgrade is allowed only after every recorded obligation is confirmed.

## Decisions and replay

POST `/api/v1/orders/{id}/approve` accepts exactly `expectedRevision` and `expectedVersion`; `/reject` additionally requires `reason`. Both require one `Idempotency-Key`. Web routes use the same service and require antiforgery. No tenant, creator, actor, client or marker fields are accepted. The creator cannot approve or reject their own order. Rejection reasons use the existing normalization and maximum 1000 characters, and are stored as plain text.

A decision records the exact submitted revision and rowversion, then the resulting revision. A rejected order must be edited to a new Draft revision and submitted again, retaining its obligation and prior decisions. Retrying the same command after a lost response uses the existing partitioned receipt and original expected tokens; it does not re-run state preconditions or create another audit. Current action, resource visibility, Deny, identity, lifecycle and entitlement checks still apply before replay or fingerprint conflict disclosure. Existing seven-day receipt replay retention applies; expired receipts are not permission to re-execute.

## Recovery boundary

The closed maintenance reader validates Orders and Approvals independently of the active entitlement. Under Standard, restored pending/rejected/rework data requires an explicitly approved child upgrade. Schema/features are reconciled without opening the parent recovery operation; current external ACL replacement and final lifecycle CAS still control traffic. M5 tests use persisted SQL data as a restore **fixture**. Actual backup/restore and release rehearsal remain M6.

## Verification

Run `pwsh -NoProfile -File scripts/check-m5.ps1` on the pinned toolchain with Docker available. This runs all inherited gates, checks the Approvals migration snapshot, and executes every named M5 policy/SQL/native-HTTP/plan/recovery case. `scripts/m5-sql-fixture.ps1` is a component gate, not a substitute for full acceptance. Missing tests, skipped tests, wrong SQL patch or unavailable prerequisites fail the gate. Evidence is stored under `artifacts/verification` with exact source SHA and counts. See `docs/verification/m5.md` for actual results; workflow start or successful compilation is not milestone acceptance.
