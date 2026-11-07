# ADR 0007 — Orders command, obligation and replay contract

- Status: **Proposed — implementation evidence is not owner approval**
- Scope: roadmap revision 1.3, M4; extension seam for M5, not an M5 implementation
- Supersedes: none; M0–M3 identity, isolation, operation and authorization contracts remain in force

## Context

Orders must be reusable across products and tenant-isolated without coupling Platform, shared contracts or themes to business modules. UI and API must share state transitions, authorization, optimistic concurrency, atomic audit and replay behavior. A rejected order must not lose its approval obligation merely because it is edited, a package changes, or an approval handler is absent.

## Decisions proposed for adoption

### Composition and data ownership

`SaaS.Orders` is a compiled business library under `src/Modules`, referenced only by product composition and tests. It does not enable native CMS features and does not reference OrderApprovals. The host explicitly combines Access and Orders routes and their persisted recovery readers. No Platform/shared-theme dependency points back to Orders.

Orders owns `orders.Orders`, `orders.Revisions`, `orders.Receipts` and its independent EF migration history. Auditing adds nullable resource, correlation and change-detail columns through its own stream. Orders never migrates an auditing/platform/access table. Runtime has no DDL, cannot delete orders, and cannot rewrite/delete revision, receipt or audit history.

The proposed minimum business fields are Number, CustomerName, Amount, Currency and plain-text Notes. These are implementation scope choices, not previously confirmed domain requirements. Line items, taxes, payments, cancellation, deletion, rich text and uploads are excluded. Currency validates a three-letter shape, not an authoritative ISO catalog.

### State and authorization

Create starts Draft at revision 1. Every accepted edit/transition increments revision once and obtains a SQL Server rowversion. The persisted creator is immutable. Edit permits Draft or Rejected and returns Draft without clearing `ApprovalRequired`.

Submit without an existing obligation and without current Approval entitlement confirms directly. Current Approval entitlement or an existing marker requires both current Approval entitlement and an enabled, ready handler; otherwise submission fails closed. PendingApproval and Confirmed content cannot be edited. Decisions match the submitted revision/token, require an authorized non-creator, and Reject requires a reason. M4 exposes no public decision route and registers no production handler.

Create requires Create plus ViewOwn. Edit and Submit require the corresponding selected Own/Others action and read permission, selected from the persisted creator. Approve/Reject require Decide, selected read access and actor != creator. Deny on the selected permission cannot be bypassed by testing its alternate. These same current policies govern replay; state/revision/token execution preconditions do not.

### Transaction and replay

The existing tenant coordinator owns one SQL connection and transaction for order, revision history, approval hook writes, business audit and receipt. Contexts enlist on that transaction and do not independently retry. Locks are transaction-owned: write gate, ACL snapshot, command partition, then resource. Current platform/identity/ACL authority is reread after waiting and after an extension executes.

The partition hash includes tenant, authenticated actor, authenticated client, action and case-sensitive key. A separate request hash includes normalized original payload, resource, expected revision and expected rowversion. The original result snapshot is stored with the mutation. A same-key waiter checks the receipt after serialization, authorizes against the current resource and current sources, then returns the stored result without executing another transition or audit. Different requests conflict only after that authorization. A failed/missing authority never reuses an old allow.

The replay window is seven days from the completion timestamp recorded inside the transaction. Expired keys remain tombstones and return an authorized conflict. M4 does not delete receipts automatically and does not permit silent reuse of expired keys. Storage growth, customer-data retention and a future privileged compaction protocol are explicit operational costs. The seven-day value is a product/operations choice requiring owner approval.

### Approval extension and recovery

An approval handler receives the caller's transaction and immutable revision-bound submission/decision records. It must await all work, enlist its contexts, and must not commit, open a separate write connection, independently retry or perform irreversible external side effects. Future notifications require a transaction-bound outbox, not a network call from the hook.

The Orders reader is independent of current entitlement and handler registration. It verifies database identity, migration and physical schema, current/history consistency and sticky markers. Pending, Rejected and rework Draft with a marker remain outstanding; Confirmed retains its history but is not outstanding. Missing/unknown state returns Unknown, never an empty Clear.

The host preserves a closed table inventory. A verified pre-Orders foundation can upgrade; an installed M4 profile with missing Orders schema, an unknown future profile, or an unfamiliar business table blocks private reconciliation. Recovery uses current platform authority and the shared operation/write-gate protocol; it does not trust restored ACL/audit as current approval.

### Transport

Server-rendered, script-free pages and bearer API call the same service. Web writes use antiforgery, encoded HTML and server-generated form keys. API input is strict JSON: exact fields, no duplicates, no unknown server-owned properties. Both transports are bounded at 32 KiB, sufficient for maximum valid percent-encoded Unicode form fields. List pages contain at most 50 authorized records, with a bounded page index 0–1000 and no total count for inaccessible resources.

## Consequences and alternatives

A single local transaction is simpler to reason about than a distributed event-first write, and reused receipts solve ambiguous client outcomes without retrying individual EF contexts. It requires the future approval module to share the same customer database/transaction, which is intentional for this modular monolith. A remote approvals service would need a different explicitly reviewed consistency contract.

Persisted snapshots and tombstones use more storage than recomputing results from current rows. Recomputing was rejected because it cannot faithfully replay a committed older result or safely distinguish a lost response after state advances. UI buttons are not authorization boundaries; denied actions are still rejected by the shared service.

Marker-only test handlers and SQL request-table fixtures prove the extension seam but are not production Approvals. Operational backup/restore implementation remains M6 and cross-node/load validation remains M7. This ADR does not approve deployment, data-retention policy or the new business field model.
