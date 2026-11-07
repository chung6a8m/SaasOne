# M3 completion — continuation of the approved milestone scope

Continue `codex/m3-authorization-lifecycle-audit` / PR #5; do not merge or accept ADRs automatically.

## Historical continuation checkpoint

Remote `43a60e9`: M0/M1/M2 and 30 real-SQL M3 component cases pass. The complete M3 gate intentionally remains red. The original M1 fault fixture restored only one migration after deleting a now multi-migration stream; preserve the entire history instead of weakening readiness.

## Implementation contracts

- `TenantOperations.BeginRecoveryAsync`, `ApproveRecoveryAsync`, `ResumeRecoveryAsync`: platform-owned operation, shared tenant lease, new credential and ACL generations, current external authority registered before the operation, explicit full ACL replacement and transaction audit. Resume re-fences and closes the customer gate before reading obligations or applying ACL. No SQL restore is performed in M3.
- Recovery with Standard plus outstanding obligations may only select Approval through an explicitly approved child step under the parent operation/lock/fence. Its checkpoints never open traffic. Finalization rechecks lifecycle, actual gate/ACL marker, authority and current memberships before the platform CAS.
- `TenantOperations.PurgeAsync`: PendingDeletion only, common lease and previous-worker quiescence, immutable DB identity validation, durable purge intent/checkpoints, target-only deletion and permanent registry tombstone. No global-account deletion.
- `IRecoveryAclApplier.RequiredMemberships`: returns the subjects of the validated full approved ACL; the orchestrator rechecks these platform rows inside its final short transaction. This does not allow Tenancy to parse or own ACL rules.
- M3's production composition is explicitly enabled; existing M1/M2 fixtures retain their narrower endpoints. Tenant `/access` and `/api/v1/access` call the same fresh-authority application service; web mutations require anti-CSRF. A small protected workspace endpoint exercises the same policy on web and API. No owner/tenant/resource authority comes from client input.
- Deployment-owned application profiles record the applied plan/schema manifest in the Tenancy migration stream. Runtime validates, never migrates or repairs. AccessControl/Auditing retain their own schema owners.
- The minimal operator UI is a private PowerShell terminal console over the SQL-authenticated maintenance service API. It lists registry versions and invokes suspend/renew/delete/plan/recovery/purge commands. Privileged credentials remain absent from HTTP dependency injection; no ad-hoc public operator authentication surface is introduced. Tenant role management has both server-rendered web UI and HTTP API.

## Test-first batches

1. Recovery orchestration and guarded purge SQL tests: external authority, rollback, restored rights, child upgrade, unknown reader, lifecycle/expiry races, checkpoint restart and no resurrection after purge.
2. Host integration HTTP tests using the existing native issuer/PKCE fixtures: default deny, deny precedence, self-escalation, tenant routing, current ACL/membership/lifecycle/issuer reads, failure mapping and anti-CSRF. Inherited M2 covers token cryptography and refresh semantics; M3 adds policy endpoints.
3. Private command/console contracts, model consistency, low-privilege runtime, full mandatory inventory and runbooks.

For each batch record RED failure, implementation, GREEN test counts and tested source SHA. Missing prerequisites, skipped or undiscovered mandatory tests stay FAIL/BLOCKED. Use source-bound GitHub Actions because this execution container has no .NET SDK or SQL Server.

## Closure — 2026-11-07

All completion contracts above are implemented. The terminal operator UI and private CLI share the guarded services; tenant UI/API share fresh permission policy. Installation starts empty, recovery requires external approval, unknown business tables keep operations closed, and retirement cannot publish a new enabled client even when deletion happens during native registration.

The full `scripts/check-m3.ps1` run **42344656504** passed on code head `5c5015df76217b0311d17455e4ca4f6b0917d0d0`, tested merge `bdc2a7606c958ef261455b4d09cd69a95eae13bd`: 150 unit, 26 architecture, 48 M1 SQL, 44 M2 and 74 M3 cases passed; no failed/skipped cases. SQL smoke and fixture guards passed, all model checks passed, and seven M3 evidence-validator scenarios passed. All prerequisite reports carry the same SHA.

[Verification details](../../verification/m3.md) and [private operations runbook](../../runbooks/m3-operations.md) replace the historical failure checkpoint as the current implementation record. Owner architecture/security review and merge remain separate; Orders/Approvals and real SQL restore/release rehearsals remain their later milestones.
