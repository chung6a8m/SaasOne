# M7 implementation plan

Spec: roadmap revision 1.3, M7 / sections 8 and 9.1; issue #12; AGENTS.md.
Base: main `7843cf0c5b38644875529ee5c55f1805d6c2da2a` (M6 closure recorded).
Owner: project owner reviews architectural/security choices and merges. No ADR promotion and no production approval.
Branch: `codex/m7-acceptance-reuse`. Implementation and acceptance are separate gates.

## Scope and sequence

1. Capture a clean, credential-free source archive and inspect existing ADRs, host composition, SQL fixtures and verification entry points. Refine the exact consumed interfaces below before product implementation.
2. Add failing tests for a second host/theme's dependency graph, published artifact and guarded startup. Add the independent product composition without changing platform/core or runtime guards. Build/publish/start on the pinned toolchain.
3. Add four-tenant SQL/HTTP correctness and load fixtures: Alpha/Delta Standard, Beta/Gamma Approval, shared users with different roles, explicit Deny, users without membership and an operator. Retain inherited negative/revocation/replay/restore gates. Keep lifecycle/fault traffic outside the all-Active load baseline.
4. Add a reproducible ramp-up load harness (proposed 25 virtual users per tenant, 100 total), latency distributions, throughput/error accounting, environment and SHA provenance. Baseline first; a separate reviewed target file must be locked before acceptance, never inferred from the acceptance result.
5. Add module template, product creation/runbook and bottleneck reporting. Publish exact-source evidence, mandatory inventory/counts and artifact hashes. Leave issue #12 open until explicit acceptance review.

## Affected files and ownership

- `docs/plans/m7-task-plan.md`: task contracts and acceptance mapping.
- `.github/workflows/m7.yml`: source provenance followed by M7 verification; existing M0-M6 workflows remain gates.
- `samples/` (independent product host/theme), `templates/` (module template), solution/project lock files: composition only; no core modification.
- `tests/Architecture/`, `tests/Unit/SaaS.UnitTests/`, `tests/Load/`: M7 assertions, SQL fixtures and HTTP load workload.
- `scripts/`: dedicated M7 verification/evidence entry point; extend existing inventory only after examining its contract.
- `docs/runbooks/`, `docs/verification/m7.md`: repeatable operations and evidence ledger.

No new database table or migration is planned. Existing Tenancy, Identity, AccessControl, Audit, Orders and Approvals migration streams retain their owners. Fixtures use real SQL Server and existing controlled bootstrap, never runtime EnsureCreated/migrate or a weaker guard.

## Interfaces and failure tests to lock after source inspection

Consume the existing host composition and Orchard-routed tenant resolution, controlled provision/migration/readiness, identity issuance/session, tenant permission and Orders/Approvals HTTP contracts without changing signatures. Produce an independent product entry point plus machine-readable load/evidence reports. Exact signatures, paths and commands will be added before the corresponding implementation task.

Failures to prove: forbidden direct/transitive/package/feature/publish dependency; missing readiness prerequisites; zero discovery or skipped mandatory tests; cross URL/token/body tenant; missing membership/Deny; lost update and duplicate approval; unexpected 5xx; omitted tenant/workload; missing or post-hoc latency target; missing source/environment/count/hash provenance. Expected denied/fault responses must not be counted as successful business work.

## Verification status

NOT RUN. Source-provenance workflow is tooling only and is not an acceptance gate. This planning commit does not claim implementation, SQL correctness, load capacity or M7 PASS. Local environment has no dotnet/pwsh and cannot clone GitHub directly; runtime checks will use the repository's pinned CI toolchain, with results read back before claiming success.
