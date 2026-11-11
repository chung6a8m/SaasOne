# Issue #15 — single-maintainer security risk acceptance

**Date:** 2026-11-11  
**Decision ID:** ISSUE15-SOLO-SECURITY-20261111  
**Decision maker:** the project's **sole human owner and PR author** (explicitly approved in the current project conversation).  
**Scope:** PR #16 bounded YesSQL/EF shared physical SQL login and downstream PRs #17/#18, no production capacity approval.

## Decision

The project has no other human contributors or available independent GitHub reviewer. The owner authorizes remediation and explicitly **accepts** the remaining SQL-login-disable race described in PR #16's P1 finding. For this owner-controlled engineering project, the otherwise impossible independent-human-sign-off requirement for this **particular known P1 finding** is replaced by the owner's **documented, explicit risk acceptance plus objective SQL regression evidence**.

This is an exception to the *review process*, not to the security invariants. No reviewer has independently approved the design, and GitHub will not permit self-APPROVE (`422 Review Can not approve your own pull request`). Review comments, owner approval and real SQL tests remain distinct evidence. Do not label any self-review as a separate reviewer's approval.

## Threat and compensating controls

- Threat: SQL Server `ALTER LOGIN [saas_platform_runtime] DISABLE` after the final enabled-login check does not automatically invalidate a previously authenticated SQL session; concurrent in-flight authentication may finish before a new login is required. This is **not atomic immediate revocation**.
- Defense: `DefaultIssuerStore` validates fixed source/catalog, authenticating SQL login, `Pooling=false`, `Enlist=false`, no native transaction; creates a fresh native-owned connection for **each validation**, no cross-validation/tenant sharing.
- Defense: `IdentityAuthority` validates fresh Platform family/session/account/membership/security/client and actual native YesSQL user/grant/token, then fresh tenant state. Checks live enabled-login state immediately before native checks and again just before returning. Orders post-lock ACL/tenant authorization remains independently fresh.
- Defense: unknown/disabled SQL login fails closed; separate full validation after disable requires another fresh SQL login. No global pooling, authorization snapshot cache or time-of-check bypass.
- Verification: [M2 SQL run 42960986881](https://github.com/chung6a8m/SaasOne/actions/runs/42960986881), **53/53 PASS**, including disable at two deterministic in-flight boundaries; [Issue15 SQL/HTTPS run 42960986847](https://github.com/chung6a8m/SaasOne/actions/runs/42960986847), **7/7 M7 mandatory + 1/1 supplemental PASS**, exact 8 physical opens. All M0–M6 inherited gates PASS at PR #16 SHA `75187eac3204a5d7f3bddf1375d5d8331997ebee`.

## Accepted and outstanding

**Accepted by sole owner:** the above bounded residual post-final-check window for the present experimental architecture. Other choices (PR #17 test-only attribution and PR #18 read-only List SQL query) are subject to their own correctness and performance review findings.

**NOT accepted / NOT claimed:** independent human/security review, atomic login revocation, production readiness, frozen M7 performance SLO, valid original M7 benchmark ancestry. Original Candidate remains blocked on missing historical baseline SHA and exploratory p95/p99 targets still fail. **Do not merge** merely because this document exists; PRs and Issue #15 retain their own explicit owner-controlled release actions.

**Migration owner:** none. This is a decision record only, no change to runtime or historical review findings.
