# SaasOne agent contract

## Authority and workflow
The normative specification is `docs/roadmap/saas-orchard-roadmap-v1.md` revision 1.3. Do not edit historical reviews to hide findings. Implement one milestone at a time from a task plan with affected files, consumed/provided interfaces, failure tests, commands, migration owner and acceptance gate. The project owner reviews architectural/security decisions and merges; an agent must not promote a conditional ADR to Accepted.

Work on an isolated branch. Read `docs/adr/` and `docs/decisions/m0-register.md` first. New defaults are proposals until owner review; never describe them as prior user requirements. No concurrent writers to one DbContext model, snapshot or migration stream. Do not introduce M1–M7 product behavior into the M0 skeleton.

## Non-negotiable boundaries
- Platform, Abstractions and shared themes never reference Orders/OrderApprovals directly or transitively through projects, packages or Orchard features. A product host is the composition root. Orders does not depend on Approvals implementation. Run architecture checks for every dependency change.
- The Orchard-routed shell selects the customer connection. Tokens, request bodies and headers never select a database. Platform access uses its fixed, separate connection.
- One table and migration history have one owner. Orchard/YesSql and EF do not migrate each other's tables. No runtime `EnsureCreated`, automatic EF migration or database drop.
- No customer Orchard Administrator, executable templates/scripts or arbitrary inline uploads. No public setup. M0 returns 503 for everything except `/health/live`.
- Authorization defaults closed, Deny overrides Allow, dynamic authorization is not cached across requests. Restore ACL is historical, not current authority. Operation completion cannot overwrite a newer suspend/delete decision.
- Never commit secrets, tokens, certificates, connection strings with credentials or local App_Data. Use secret references/environment variables; never print their values or pass them on command lines.

## Verification and evidence
Write a failing behavioral test before implementation. Run the full suite after fixes. SQL evidence must use real SQL Server, not SQLite or InMemory. An unavailable prerequisite is BLOCKED/FAIL, not a successful skip. Zero discovered tests, missing mandatory suites and skipped mandatory cases fail the gate.

Use `pwsh -NoProfile -File scripts/check.ps1 -Scope M0` and the separate `SqlSmoke` gate. Record the exact tested commit, environment, command exit codes and discovered/executed/passed/failed/skipped counts. A workflow starting, restore succeeding or HTTP 200 is not milestone acceptance. Keep identity Conditional until the first M2 cross-shell PoC passes. Report any missing runtime evidence explicitly.
