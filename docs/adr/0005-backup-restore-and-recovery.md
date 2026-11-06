# ADR 0005 — Backup identity, security revalidation and approval recovery

Status: Proposed for owner review; roadmap recovery invariants retained. Owners: tenancy/access-control implementers and operator. Scope: R2-01, R3-01, R3-02, R3-03; roadmap §5.3–5.6.

A customer restore does not restore the platform. Current registry, memberships, generations, recovery authority and operation audit must remain independently authoritative. If platform has also rolled back or cannot be trusted, keep closed and use a separately reviewed platform recovery procedure. Bumping token generation alone cannot make restored ACL current.

## Source and destination gates
The tenant has an immutable singleton TenantId record owned by Tenancy from M1. M6 backup manifests are generated from a verified source, held outside the customer DB in an access-controlled operations store, and bind TenantId, backup set/chain, source identity, restore point and artifact digests. A digest authenticates content, not the manifest writer. The M6 plan fixes format, ownership, trusted delivery and protection against changes between validation and restore.

Before overwriting anything, verify current registry/authorized destination, trusted manifest and the complete artifact chain against the target TenantId. Reject platform DB, mixed tenants, unknown provenance or altered artifacts. Filename/DB name/identical schema are not identity. After restore, while isolated, read the DB identity through a minimal operations path and compare registry + manifest + DB before migrations, seed, rights application or application warm-up. Missing/mismatched identity stops recovery; never seed or rewrite it to make evidence match. No V1 tenant cloning. This is protection against mistaken/substituted backups in the operations trust model, not a claim against an all-powerful DBA.

## Security recovery
Before restore, the authorized operation obtains common resource ownership, persists Restoring/SecurityRevalidationRequired and a new tenant security generation at platform, preserves subscription and drains business work. Block business UI/API and tenant grant/session issuance/refresh. Restored Active or write-gate state cannot reopen traffic.

All restored role definitions/permissions, assignments and user Allow/Deny overrides are historical, not an authorization source. A recovery authority registered in the current platform before recovery approves an explicit complete new ACL; restored ACL, restored audit or a local profile cannot confer that authority. Do not merge old rights or auto-grant default roles. Recovery authority does not imply Orders read access.

Apply the new ACL atomically in the tenant with audit and operation/generation marker. Platform checkpoints include actor, approver, basis, ACL hash and generation. Preserve historical records without using them to grant current access. Missing approval/source/audit or partial apply keeps closed. Resume reconciles ownership/fencing/checkpoints and authorizes again, never opens in finally.

## Restored approval obligations
The required case is backup PendingApproval → Approve → downgrade Standard → restore. After identity verification and safe schema preparation, a recovery reader must recognize uncompleted approval obligations even while the Approvals feature is disabled. Host composes the reader through an abstraction; platform does not reference Orders implementation. Missing reader/schema/marker, unknown state or query failure is not “no obligations”.

The chosen path is an explicitly approved **upgrade to Approval as a child of the restore operation** while the parent gate remains closed. Use the same resource lock, RegistryVersion and fencing, not an unrelated operation requiring an Active tenant. Current platform authority approves and audits schema/feature/entitlement reconciliation. The child never opens the parent gate, clears ApprovalRequired or approves an order. Refusal or any failure keeps recovery closed. Afterwards users still approve the correct order revision through normal policy.

Final opening requires matching data identity, applied current ACL/generation/checkpoints, current memberships, compatible obligations/entitlement, schema/features/readiness and current lifecycle CAS from ADR 0004. Suspended/PendingDeletion/expiry remains authoritative. Only fresh valid-generation credentials may work; other tenants remain unaffected.

## Proof boundaries
M1 proves identity/readiness. M3 uses fixtures for security gate, authority, atomic apply, lifecycle races and nested plan recovery. M4 supplies the obligation reader, M5 tests real data with feature disabled. M6 performs SQL backup/restore, wrong-tenant and tampered-chain tests, revoked-rights-before-backup recovery with both old credentials and new login, and all crash/resume/race cases. M0 documents these contracts; its SQL smoke is not a restore rehearsal.
