# ADR 0006 — Audit atomicity, retained approvals and replay policy

Status: Proposed for owner review; normative roadmap behavior unchanged. Owners: auditing/access-control M3, Orders M4, Approvals M5. Scope: R06, R2-02, R2-03, R3-03; roadmap §6.1–6.3.

Successful tenant business changes and their audit commit atomically in the customer DB. Audit has one migration owner, never independently commits or silently falls back to platform DB. Audit failure rolls business work back; rejected/access logging is a separate flow. Platform operational audit is separate and owned by Tenancy from M1. Do not log raw secrets, tokens or connections. Audit is not declared tamper-proof merely because the UI cannot delete it.

If multiple EF contexts participate, share DbConnection/DbTransaction with one lifetime and retry the whole unit under its execution strategy; never retry individual SaveChanges independently. Supply the transaction coordinator before the first such consumer in M3/M4, not automatically M5. A single adequate context does not justify a generic coordinator in M0.

## Orders obligations
Orders owns revision/concurrency and a server-only persistent ApprovalRequired marker. First submission needing approval sets true atomically with PendingApproval and the matching-revision request. Reject requires a reason, the creator cannot self-approve, PendingApproval content cannot change, and only one matching-revision decision succeeds. Editing Rejected creates a new Draft revision and preserves the marker/history. Confirmed is immutable in V1; the marker remains historical after confirmation.

Need approval = current plan requires it OR marker is true. Only Standard with marker false may submit directly to Confirmed. Missing handler/schema/feature for an obligation fails closed, never falls back to confirmation. Draft never submitted does not gain an obligation simply from having been created on Approval. Unknown/missing historical marker requires reviewed migration/recovery, not default false.

Downgrade/recovery use `ApprovalRequired && Status != Confirmed`, including Rejected and rework Draft; unknown state fails closed. There is no V1 cancel/close path for unapproved orders, so an unresolved rejected order can deliberately block downgrade. Do not erase the marker, delete the order or disable the module to bypass it. Orders exposes contracts, not references to Approvals implementation. M4 freezes model and extension signatures before dependent implementation; M5 proves real approvals.

## Idempotency and replay
Partition key: TenantId + actor/client + operation + idempotency key. The normalized fingerprint includes resource, payload and originally supplied expected revision. Store dedup result in the same transaction as business/audit; uniqueness/serialization prevents double execution. TTL/retention, normalization and per-command resource-policy mapping are fixed in the M4 plan before code, not assumed infinite.

First validate current credential/generations/tenant/membership/entitlement and authoritative permissions. Search only the caller's partition. For a committed result, load its stored resource identity and require current action permission **and** current access to that resource/result (own/others/Deny and other security rules). Only then disclose a matching result or a different-fingerprint 409. Replay does not rerun new-command status/revision preconditions, mutate business state or create another success audit. A newly revoked right or Deny still blocks replay.

If no committed result exists, apply the complete new-command authorization, state/revision/business preconditions and durable write gate. A race that discovers a committed result returns through the reauthorized replay branch rather than producing a false stale-revision error. A new key with old expected revision still conflicts normally.

Proofs: audit failure rollback, parallel/different-payload keys, lost response then old-revision replay, revoked-action/read-right replay denial, other actor/tenant, source failure, old revision approval rejection, reject/edit/resubmit retaining history, both downgrade orderings, missing handler and atomic Order/Approval/Audit failure. M4 uses fixtures where necessary; M5 repeats real Approve.
