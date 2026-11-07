# M6 implementation and verification status

Status: **candidate implementation; NOT accepted, NOT production-ready**. Baseline: `a3de8981cd7033b31e3768c199891c4d25ef2fa8`. CI is intentionally unchanged. Source review is not runtime evidence.

## Implemented source

The host composes six release streams, external EF bundles, idempotent SQL, immutable manifests and real Orchard verification. Tenant release/backup/restore reuse the common locks, registry CAS/fences, write gate and current-platform authorization. The platform constraint migration enables the existing Migrating operation kind; no historical migration is rewritten. Physical restore has durable source intent before the security claim, immutable source binding and identity verification before any migration/ACL work. Generic recovery cannot bypass an incomplete physical restore.

Local verification provides a single entry point, isolated evidence directories, exact source/tool versions, inherited M0–M5 gates, mandatory-case validation, release packaging, real SQL fixtures, sanitized reports/TRX and a checksummed ZIP, including failed runs. CI is not enabled by these scripts.

## Execution evidence in the authoring environment

`dotnet`, `pwsh`, Docker and SQL Server are absent. Attempts to execute the test commands cannot start (shell exit 127). Consequently restore/build, PowerShell parsing, component tests, EF model-drift checks, bundle execution, inherited integration gates and all M6 SQL/HTTP tests are **NOT RUN / BLOCKED**, not PASS. No generated release binaries, TRX or acceptance result is asserted or checked in.

The source inventory contains six M6 component facts and thirteen SqlM6 facts; several facts loop through independent failure/lifecycle cases. Exact names are checked into `scripts/m6-policy-cases.json` and `scripts/m6-mandatory-cases.json`. Written tests are not executed evidence. The local full gate must discover each mandatory fact exactly once and pass it without skips.

## Coverage and remaining acceptance work

| Area | Source coverage | Required evidence |
|---|---|---|
| Artifact trust | HMAC/tenant/chain/path/context/hash negative cases; pre-execution host verification | Component and PowerShell self-test results |
| EF release | All six actual bundles, empty databases, previous migration baseline, repeat execution | SQL TRX, manifest, SQL, model-drift results |
| Tenant release | Actual native verification, seven failure checkpoints, independent tenant progress, lifecycle conflicts | Real SQL/HTTP fixture results |
| Physical restore | Real COPY_ONLY backup, cross-tenant/tamper rejection, durable intent and physical checkpoint retries | Real SQL restore results and external audit/state evidence |
| Security recovery | Current full ACL, active membership, removed role assignment/permission or Deny, old access token/cookie rejection and fresh-login denial | HTTP results, current-platform authority review |
| Restored obligation | PendingApproval backup → approve → Standard → restore → explicit child upgrade → normal approval | Actual workflow results |

Additional targeted rehearsal/coverage remains before milestone sign-off: terminate the operating-system process while SQL RESTORE itself is in flight (checkpoint exceptions are not a substitute); restore a genuinely signed artifact whose database identity is missing/wrong and prove no EF/Orchard/ACL action ran; test the full offline platform-release coordinator, request barrier and competing operation paths; explicitly assert the old refresh-token HTTP flow after physical restore; review Windows/Linux protected-store ACLs, process-marker durability and operational key retention. These items must not be inferred from the existing fact count or a component-only PASS. Add regression cases where the first local run or review exposes gaps.

No independent reviewer or runtime host was available in the authoring session. Owner review and runtime verification remain pending. M6 and roadmap risks R2-01/R3-01/R3-02/R3-03 are not closed by this commit.

## Next command

From a clean checkout with the repository-pinned SDK/PowerShell and Docker:

```powershell
pwsh -NoProfile -File scripts/local-verify.ps1 -Scope Full
```

Return `artifacts/local-verify/<run-id>.zip` and its `.sha256`, including failure/blocked results. Review logs before sharing. Use `-Scope Components` only for an earlier build/component diagnosis; it cannot establish runtime acceptance. See the two M6 runbooks for details and operational limits.
