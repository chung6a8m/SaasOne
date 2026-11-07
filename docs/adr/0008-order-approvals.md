# ADR 0008 — Revision-bound OrderApprovals and feature lifecycle

- Status: **Proposed — implementation evidence is not owner approval**
- Scope: roadmap revision 1.3, M5; extends ADR 0007 without replacing its coordinator, policy or replay contract

## Decisions proposed for adoption

`SaaS.OrderApprovals` is a separate business library with an independent `ApprovalsDbContext`, migration history and `approvals` schema. Only product composition depends on both business implementations. Orders calls its existing `IOrderApprovalHandler` extension; Platform and shared themes remain business-independent.

Each submission inserts an immutable request keyed by `(OrderId, SubmittedRevision)` with the submitted rowversion, creator and submitting actor. A decision has that same key, references its request, and records the next revision, actor, result, reason and timestamp. The creator cannot decide. Reject requires the existing normalized plain-text reason. Rework returns to Draft without clearing the marker, then a new submission creates a new request. Earlier requests and decisions are retained, not rewritten.

The handler enlists on the caller-owned connection and transaction. It performs no independent commit/retry, audit, receipt or external side effect. Orders remains the sole owner of command serialization, current authorization, revision history, audit and partitioned replay. A failure after a request or decision insertion rolls back all effects. Same-key replay uses original expected tokens and rechecks current action/read rights; an old result is not an old authorization ticket.

## Profile, entitlement and historical data

Profile version 3 includes the Approval schema manifest. Private reconciliation installs and verifies schema before publishing the new profile or opening traffic. Both Standard and Approval install/retain that schema, while the current entitlement gates all decision actions and UI controls. This deliberately separates availability from physical history retention. Runtime can SELECT/INSERT approval history but cannot UPDATE/DELETE it, alter schema or change migration records. The deployment alternative of dropping optional tables was rejected because it destroys obligations and prevents safe recovery.

Recognized M3/M4 manifests can be upgraded through the maintenance gate. Existing ordinary M4 Orders are preserved. An M4 test fixture with approval-required history but no genuine approval records is refused rather than treated as an approved order or backfilled with invented decisions. Unknown future profiles, missing schema or inconsistent histories fail closed.

Downgrade reads persisted Orders and Approval histories without depending on active entitlement. Pending, Rejected and rework Draft obligations block downgrade until the correct revision is approved. Disabling a feature does not clear markers. The M3 protocol retains its shared operation lock, drain barrier, registry/fence checks and resumable checkpoints. Recovery under Standard with restored obligations requires explicit current external approval for a child upgrade; the child does not open the parent recovery gate or restore old ACL authority.

## Transport and consequences

Exact prefixed web/API approve and reject POSTs call the same Orders service. JSON/forms reject extra identity, tenant, client and marker fields. Web uses antiforgery; controls are permission-aware hints, never the authorization boundary. No native CMS administration or generic feature-management endpoint is enabled.

The design trades retained schema/history and extra integrity reads for safe feature transitions and recoverability. It remains a single-customer-database modular monolith; remote approval services, notification delivery/outbox, history-retention compaction, richer approval stages and delegated self-approval are outside M5. Actual SQL backup/restore and release rehearsals remain M6. Cross-node load and second-product reuse remain M7.

Verification commands and actual tested SHA/counts are in `docs/verification/m5.md`. Passing tests do not approve these choices or authorize merge/deployment.
