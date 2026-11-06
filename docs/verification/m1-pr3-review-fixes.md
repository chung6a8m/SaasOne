# M1 PR #3 — R1–R3 remediation evidence

Baseline: `dc47350631b3131f2da156f6e42323cd8f3bd858`. Original [review 5802373469](https://github.com/chung6a8m/SaasOne/pull/3#pullrequestreview-5802373469) remains unchanged. [Remediation plan](../superpowers/plans/2026-11-06-m1-review-fixes.md).

## Changes and regression contracts

**R1:** Customer registration/secret contract errors are isolated during native inventory loading. Named lookup queries only the requested registration; names do not resolve customer secrets. Nonoperational customers are omitted. Platform reads/bootstrap failures remain outside the customer catch boundary. Six real cold-process cases cover missing/incorrect target/incorrect principal in Active and Suspended states. Further cases cover invalid registration, direct named lookup and corrupt platform baseline. Healthy Beta/Default must remain ready, faulty Alpha must remain closed; a faulty platform closes both.

**R2:** Feature nodes carry their native dependency edges and their owner's original project/package dependency edges. No reverse feature→owner edge or expanded sibling-feature list is introduced. Six direct/transitive negative fixtures cover Platform/Theme→Orders and Orders→Approvals through package dependencies; a valid same-owner feature chain guards against artificial cycles. The compiled production native graph remains mandatory.

**R3:** Retained environment-only delivery, without adding a dynamic secret store. The original hot-registration case now starts a real runtime process with the future tenant's secret already injected, initializes native inventory, and provisions through a separate maintenance process. A negative case allocates the secret only AFTER HTTP startup: the new tenant stays 503 while Beta/platform remain ready, then becomes ready after a controlled restart with the updated runtime-only environment. These are process/credential-boundary tests; separate CI OS accounts and zero-downtime deployment are not claimed. README/runbook/original plan clarify the scope.

No package/lock, EF model/snapshot/migration, production infrastructure or identity-ADR changes are part of these fixes.

## Observed red evidence

| Stage | Source / actual merge-ref | Executed result |
| --- | --- | --- |
| R1 and process regressions before loader fix | `da97d92cb7fc02f853b8655d2ec5cbff34126f09` / `6c77d26faedd912a459d829eb11e8490df1b2f86` | [Run 42244862367](https://github.com/chung6a8m/SaasOne/actions/runs/42244862367): build 0 warnings/errors; 74 unit + 19 architecture passed; SQL 48 executed, 40 passed, 8 failed, 0 skipped. All six cold-process cases, named lookup and invalid-registration isolation failed for the reported R1 behavior. Both R3 process scenarios and platform-negative control passed. |
| R2 regression before mixed-edge fix | `af505b13e0956968587e90e0ab545cb6f853f38d` / `5a251819bc97d01517d5fc58ca3ef462c40fdfe8` | [Run 42245646099](https://github.com/chung6a8m/SaasOne/actions/runs/42245646099): build 0 warnings/errors; unit 74 passed; architecture 26 executed, 20 passed, 6 failed, 0 skipped. Each mixed-path fixture produced no forbidden-dependency diagnostic. SQL steps were not executed because the earlier gate correctly failed. |

R1 artifact: `13518360026`, SHA-256 `af77da3fb90fa130fe7dec6b7f2d69a620028dac0784bc0287153a532ecc60e6`.
R2 artifact: `13516269839`, SHA-256 `8929ccdfe08c838c2f4a2b42cc4b0ed110e2ecea2d952f54347e26da34087c21`.

## Final verification gate

The implementation commit must pass locked restore/Release build, all 74 non-SQL unit cases, all 26 architecture cases, both retained snapshot checks, PowerShell parsing, native-feature graph/hygiene/locks, inherited real SQL smoke, and all 48 SQL/Orchard cases. The SQL evidence gate requires the named regressions and all six parameterized cold-process cases, not only a total count. Fixture cleanup must succeed before PASS.

Final executed results are pending this implementation revision's CI; no future run is claimed as passing by this record. Exact-head and PR merge-ref results must both be recorded before merge. Local dotnet/pwsh/Docker are unavailable in the editing session; execution uses the repository's existing pinned read-only GitHub Actions workflows. No independent subagent review, merge or production deployment is claimed. ADR 0001 remains Conditional pending M2 PoC.
