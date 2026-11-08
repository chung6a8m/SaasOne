# M6 — Controlled release and local verification

Status: **source candidate implemented; runtime verification and owner review pending**. Baseline: `a3de8981cd7033b31e3768c199891c4d25ef2fa8`. Specification: roadmap revision 1.3, M6 and §§5.3–5.6, ADRs 0003–0005. A dedicated M6 CI full-acceptance workflow now runs; do not equate a source commit with acceptance evidence.

## Design

Extend the existing private maintenance CLI, TenantOperations, resource lease, registry CAS, durable write gate and recovery ACL protocol. Keep product composition in the host and no Platform → Orders dependency. Use six existing EF streams and separate real Orchard reconciliation.

The initial plan anticipated no schema migration. Inspection found the existing ResourceOperations constraint excludes Migrating (4), so M6 adds a forward platform migration and frozen target/snapshot widening that constraint. No new table, stream or rewritten historical migration is needed. Platform upgrade runs offline with an external pending audit barrier and exclusive schema lock.

Release packages bind exact source SHA, stream/migration inventory, binaries, SQL and file hashes. Trusted expected manifest digests are supplied independently. Verify the package from the trusted checkout before executing packaged code. External bundle credentials travel only through child environment; durable launch intent and PID/start-time markers prevent unobserved child takeover before SQL connection.

Backup V1 supports exactly one COPY_ONLY full SQL backup with CHECKSUM, no differential/log/striped/mixed chains or relocation. HMAC-authenticated manifests bind source tenant, database, backup-set identity, size and digest. Keys and operation audit live outside customer databases; stores must be private and shared between CLI and SQL service.

Physical restore persists source intent before security-generation claim, blocks generic recovery until physical identity verification, validates provenance/header/layout before overwrite and immutable database identity before migration/native/ACL work. It never opens traffic. Current-platform authority explicitly approves the full current ACL and any necessary Approval child upgrade before existing recovery completion. Suspension/deletion/expiry remain authoritative.

## Source work packages

Release manifest/catalog/package, platform and tenant runner, process fencing, backup store, physical restore barrier, host maintenance composition, six factory connection selectors and platform constraint migration are written. Tests include six component facts and thirteen real SQL facts with checkpoint/lifecycle loops. The local verification entry point, inherited gate integration, negative evidence self-tests, release builder, disposable SQL fixture, exact case inventories and two runbooks are written.

## Verification boundary

The editing environment lacks dotnet, pwsh, Docker and SQL Server. Test-first attempts are BLOCKED, not observed RED/GREEN runs. Build, parser checks, model drift, all integration results and operational acceptance remain NOT RUN. The PR must remain draft/unmerged until reviewed local evidence and outstanding targeted rehearsals are addressed.

The authoritative status and remaining coverage gaps are in `docs/verification/m6-implementation-status.md`. Run `scripts/local-verify.ps1 -Scope Full` from a clean checkout. It collects command logs, exact source SHA, fresh TRX/reports, safe release metadata/SQL, hashes and ZIP even on failure. Missing prerequisites, zero/skipped/duplicate/missing mandatory cases and stale evidence cannot satisfy acceptance. Components-only success is explicitly not M6 acceptance.
