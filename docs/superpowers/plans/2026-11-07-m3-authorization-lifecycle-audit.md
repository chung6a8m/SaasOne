# M3 Authorization, Lifecycle and Audit Implementation Plan

> For agentic workers: execute sequentially with test-driven changes; the project owner reviews architectural/security selections and merges.

**Goal:** Implement roadmap revision 1.3 M3 without importing Orders/Approvals implementation or claiming an M6 SQL restore rehearsal.

**Architecture:** Extend the existing M1 resource ownership and M2 authoritative identity checks. Tenant ACL and audit have their own owners; platform audit remains in PlatformDbContext. Customer writes hold a transaction-owned SQL gate; operation completion reconciles ownership/version/lifecycle instead of overwriting subscription state.

**Tech stack:** Retain the pinned .NET 10 / Orchard 3.0.1 / EF Core 10 / SQL Server graph and PowerShell gates.

**Spec:** `docs/roadmap/saas-orchard-roadmap-v1.md`, sections 3.3, 4.2–4.4, 5.2–5.6, 6.1–6.3 and M3; ADR 0001–0006.

**Baseline:** `c67eacd25ee0ff82dfb9480f68bb7a75ab3e0c91`, including the merged M2 and native token-index P3 remediation. Work branch: `codex/m3-authorization-lifecycle-audit`.

## Global constraints

- Platform/Abstractions must not reference business modules; the product host composes obligation readers.
- Only the Orchard-routed shell chooses the customer database; current platform sources own identity, memberships and recovery authority.
- No cross-request cached Allow, role-claim authority, automatic migration, EnsureCreated, silent skips or runtime database drops.
- Deny overrides every Allow. Unknown permission, missing resource rule, unavailable required source and historical restored ACL never grant access.
- Preserve exclusive owners: platform operational audit = Tenancy; tenant write gate = Tenancy; ACL = AccessControl; customer audit = Auditing.
- Subscription state and operation gates are distinct. Final/resume operations cannot undo a newer suspend/delete/expiry decision.
- SQL tests use SQL Server. Missing prerequisites, zero discovery, skipped mandatory cases and cleanup failures keep acceptance closed.
- New implementation choices below remain subject to owner review; no ADR is promoted by this work.

## Review focus

1. Concurrent ACL changes must not let two independently authorized requests jointly escalate privilege; serialize administration and reauthorize in its transaction.
2. A preauthorized writer waiting behind a plan change must reject a closed/newer gate, even if its HTTP authorization previously succeeded.
3. A stale worker or restore child must never clear another operation's gate; ownership/version and actual mutual exclusion are separate requirements.
4. A new login during restore must not read restored ACL; generations alone are insufficient.
5. Required-source errors must produce 503 with a correlation identifier, not 403, and must not disclose another tenant's data.

## Task 1 — Establish source-bound verification and contracts

Files: `.github/workflows/m3.yml`, this plan, `src/Platform/SaaS.Abstractions/Authorization/*`, `src/Platform/SaaS.Abstractions/Operations/*`, `tests/*` as selected after inspection.

- [ ] Run inherited M0/M1/M2 baseline and retain exact source SHA and evidence.
- [ ] Freeze typed permission/evaluation, obligation-reader, transaction-gate and recovery-apply contracts before dependent implementation.
- [ ] Write failing tests for the permission truth table, resource restrictions, missing sources and unsafe administration.
- [ ] Run red tests, record expected failures, then implement only the required contracts/policy.

## Task 2 — Tenant authorization and transactional audit

Files: `src/Platform/SaaS.AccessControl/*`, `src/Platform/SaaS.Auditing/*`, host composition and tenant SQL migration/provision scripts, matching unit/SQL tests.

Produces: tenant role/permission/assignment/user-override persistence; per-request evaluator; shared UI/API policy adapter; audited ACL administration; shared-connection transaction coordinator for the first ACL+audit consumer.

Consumes: M2 current credential/account/session/grant/membership authority; M1 routed tenant context/identity/readiness.

- [ ] Add failing tests for all Allow/Deny/Unspecified combinations, user overrides, resource own/other rules, unknown permission, CMS privilege names and cross-tenant input.
- [ ] Add administration tests for self-escalation, granting rights the actor cannot grant, concurrent mutation and atomic rollback when audit fails.
- [ ] Implement independent AccessControl/Auditing contexts and migrations, sharing one SQL transaction; retain platform audit ownership.
- [ ] Integrate fresh evaluation at protected UI/API boundaries; preserve 401/403/503 semantics and correlation IDs.
- [ ] Test post-commit role/Deny/membership changes with unchanged credentials; no cross-request Allow cache.

## Task 3 — Lifecycle, durable write gate and plan changes

Files: `src/Platform/SaaS.Tenancy/Data/*`, `Domain/*`, `Operations/*`, Tenancy migrations/snapshots, operation entry points and SQL fixtures.

Produces: shared operation ownership, transaction-owned compatible writer lock, exclusive close/drain, registry CAS/checkpoints, controlled plan reconciliation.

- [ ] Add failing tests for expiry, suspend/resume, PendingDeletion, cross-tenant isolation and registry conflicts.
- [ ] Add fixture obligation reader covering PendingApproval, Rejected, rework Draft and unknown/error; no entitlement-dependent short circuit.
- [ ] Test drain timeout, preauthorized waiting writer, crash at checkpoints, retry, stale owner/fence and forbidden direct feature changes.
- [ ] Implement plan changes with durable close before reading obligations; migrate before feature enable; fail closed on incomplete reconciliation.
- [ ] Test suspend/delete immediately before completion, expiry mid-operation and resume conflicts; complete only the operation gate.
- [ ] Exercise soft-delete/purge against disposable targets while preserving global accounts/other databases; require actual worker quiescence and a tombstone.

## Task 4 — Security revalidation and nested recovery fixtures

Files: platform recovery authority/checkpoints, tenant ACL generation/apply markers, recovery service and integration fixtures.

Produces: externally authoritative recovery approval, complete ACL replacement with audit/hash/generation, restore-child plan upgrade without opening the parent.

- [ ] Write tests showing restored ACL is never current, including fresh credentials.
- [ ] Verify recovery authority originates in current platform and existed before recovery; tenant roles/audit cannot confer it.
- [ ] Apply an explicit full ACL atomically with audit and a generation marker; apply/audit/source failure keeps the gate closed.
- [ ] Reconcile identity/ownership/checkpoints/current memberships and obligations before final opening.
- [ ] Test approved Standard→Approval child under closed restore parent; refusal/reader failure stays closed and child cannot clear parent gate.

## Task 5 — Minimal operations UI/API and acceptance

Files: host operations endpoints/views, scripts, `.github/workflows/m3.yml`, `docs/runbooks/m3-operations.md`, `docs/verification/m3.md`.

- [ ] Expose minimal authenticated operations UI/API through the same services; verify antiforgery, privilege separation, encoded output and no direct CMS/feature bypass.
- [ ] Extend mandatory-suite detection and migration-model checks for M3; retain inherited suites.
- [ ] Run architecture checks, locked restore/build, complete unit suite, genuine SQL Server migration/rollback/concurrency tests and HTTP UI/API negatives.
- [ ] Record exact tested SHA, environment, commands/exit codes and discovered/executed/passed/failed/skipped counts. Mark any unexecuted gate BLOCKED, not PASS.
- [ ] Self-review the whole diff; open a PR with acceptance evidence and review decisions. Do not merge or claim owner acceptance.

## Verification strategy

The current interactive container has no .NET SDK and cannot clone GitHub directly. Use the connected GitHub API for isolated commits and GitHub Actions for the pinned executable toolchain and SQL Server. Retain a tracked-source archive (no working-tree files, secrets or App_Data) with the source SHA so evidence and inspection are reproducible. Initial workflow execution is baseline-only and does not establish M3 acceptance. The final gate must explicitly execute the M3 suite and reject incomplete discovery.

Inherited command: `pwsh -NoProfile -File scripts/check-m2.ps1`. M3 command: `pwsh -NoProfile -File scripts/check-m3.ps1` after implementation. Exact new context names, test paths and interfaces are recorded with the task implementation before consumers.
