# M6 — Controlled release, backup/restore and local verification

Status: implementation in progress; NOT accepted. Baseline: `a3de8981cd7033b31e3768c199891c4d25ef2fa8`. Specification: roadmap revision 1.3, M6 and §§5.3–5.6; ADRs 0003–0005. CI is temporarily disabled by the owner. Do not enable workflows or equate a source commit with runtime evidence.

## Design and decisions for owner review

Extend the existing private maintenance CLI, `TenantOperations` partial class, `OperationLease`, registry CAS, durable write gate and recovery ACL protocol. Do not introduce a second coordinator or a Platform → Orders dependency. The product host composes the six EF streams and Orchard reconciliation. No new EF tables/migration stream is planned: append-only platform operational audit records bind release/backup digests and physical-restore checkpoints.

Release packages contain the published host, per-context EF bundles, reviewable idempotent SQL, context/migration inventory and SHA-256 file inventory. The operator supplies the expected manifest digest through trusted deployment configuration, independently of the package. Bundle connections travel through child-process environment only, never arguments/logs. A durable child-process sentinel supplements the existing same-host/SQL-session fences: a possibly orphaned bundle must block takeover even before it opens SQL. Unknown launch state requires explicit operator reconciliation, not an elapsed-time assumption.

Backup V1 deliberately supports exactly one COPY_ONLY full SQL Server backup set with CHECKSUM. Differential/log/striped/mixed chains are rejected, not partially restored. A versioned HMAC-authenticated manifest binds TenantId, source database, backup-set identity, restore point and artifact hash/length. The signing key and access-controlled operations store remain outside customer databases. SQL and the CLI must see the same protected backup files; a read handle and protected directory prevent accidental replacement during validation/use. This is not protection from an all-powerful DBA. Restore is restricted to the current registered database; relocation/tenant cloning and platform recovery are outside this implementation.

A physical restore first binds its source at platform and advances the existing security generation/revocation gate. Before SQL overwrite, verify registry, manifest provenance, artifact digest and SQL backup header. After SQL restore, verify immutable database TenantId before migration, ACL replacement or Orchard warm-up. Persist an external physical-restore checkpoint. Generic recovery resume/restart must not bypass an incomplete physical restore. Existing explicit current-platform ACL approval and optional Approval child upgrade remain mandatory; neither restore nor release changes suspension, deletion or expiry.

## Work packages

- [ ] Tests first: release/backup manifest mutation, traversal/duplicate inventory, wrong tenant/source, unsupported chains, child-process ambiguity, missing evidence and skipped/zero tests. SQL tests use real SQL Server only. Record RED execution as BLOCKED if the pinned toolchain is unavailable; do not invent a failing run.
- [ ] Release packaging: `scripts/build-release.ps1`, a host-owned context catalog and bundle execution connection configuration. Six owners: PlatformDbContext/platform, TenantDbContext/tenancy, AccessDbContext/access, AuditDbContext/audit, OrdersDbContext/orders, ApprovalsDbContext/approvals. Verify actual type names against existing contexts before implementation. Orchard migrations remain separate from EF bundles.
- [ ] Release runner: `TenantReleaseOperations`, manifest binding, close/drain, bundle execution, Orchard reconciliation, schema/feature verification, audited checkpoints, per-tenant report and explicit resume. Every resource step checks current ownership/version. No automatic reload-and-retry after lifecycle conflict.
- [ ] Backup/restore: authenticated manifest/store and SQL backup/header/restore adapter, source/destination guards, physical checkpoint integration with existing recovery. Restore never opens traffic; explicit current ACL approval/resume does that after all existing checks.
- [ ] Acceptance tooling: mandatory case inventories, disposable SQL fixture and `check-m6.ps1`; inherit M0–M5 gates without weakening them. Add SqlM6 exclusion to component-only execution.
- [ ] Local verification: one entry point, isolated run ID, exact Git SHA and dirty-state inventory, prerequisite versions, command exit codes/durations, TRX counts/case inventory, safe logs, checksums and ZIP. Preserve evidence on failure; never export credentials, raw database backups, local App_Data, private keys, arbitrary environment variables or MSBuild binary logs. Components-only evidence cannot satisfy full M6 acceptance.
- [ ] Runbooks and verification report; source/diff/hygiene review, then owner review and local runtime evidence. Keep the branch/PR unmerged while acceptance evidence is missing.

## Required failure evidence

Release: empty schema; upgrade from previous baseline; idempotent rerun; wrong/missing context/artifact; failed tenant while another proceeds; retry after each checkpoint; previous child still alive; schema not ready means no feature enable; actual Orchard migration/profile checks, not only HTTP 200; Suspend/PendingDeletion/expiry before completion; shared-operation overlap.

Restore: valid source; Alpha backup offered to Delta; forged manifest, modified artifact and unsupported/mixed chain before overwrite; missing/wrong restored TenantId before any seed/migration/ACL; crash before/after SQL and identity checkpoint; old credentials rejected; fresh login cannot regain revoked role assignment, role permission or override/Deny while membership stays active; recovery authority comes from current platform; other tenants remain intact. Exercise backup PendingApproval → Approve → Standard → restore → explicit approved child upgrade → normal approval of restored revision.

## Verification and acceptance

Commands are documented in the runbook and emitted by the local runner. The full gate must include locked restore/build, architecture/hygiene/lock checks, snapshot drift checks, mandatory policy cases, inherited real SQL/HTTP gates, M6 release/restore SQL tests and artifact validation. Missing prerequisites = BLOCKED; failed commands, missing/duplicate mandatory tests, zero discovered tests or skipped mandatory cases = nonzero exit. Packages and test reports must identify the same clean source revision. Evidence is collected in a finally path even after failure.

The current editing environment has no `dotnet`, `pwsh` or SQL Server and cannot clone the private repository directly. Source is read/written through the GitHub connector. Runtime acceptance therefore remains NOT RUN/BLOCKED until actual local results are supplied. Static checks alone do not close M6, R2-01, R3-01, R3-02 or R3-03.
