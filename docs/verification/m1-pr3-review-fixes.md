# M1 PR #3 — R1–R3 remediation evidence

**Implementation verification: PASS at `999a4f4cf9e7f5b8c7f44fade78f95f23b183763`. No merge or production deployment by this record.**

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

## Executed green evidence

| Scope | Run / source / evidence |
| --- | --- |
| Exact implementation head | [42246212780](https://github.com/chung6a8m/SaasOne/actions/runs/42246212780), job `127988983496`, source `999a4f4cf9e7f5b8c7f44fade78f95f23b183763`; all steps SUCCESS |
| PR merge-ref | [42246223331](https://github.com/chung6a8m/SaasOne/actions/runs/42246223331), job `127989023213`, tested source `bfaebd79a274eec2d39172e25cc3ebf61bf58732`; all steps SUCCESS |
| Exact-head artifact | [13517931632](https://github.com/chung6a8m/SaasOne/actions/runs/42246212780/artifacts/13517931632), ZIP SHA-256 `8e2890df59f0a05f3de86462f5089356a0ff7911d33f3b2834f0f4143be5c570` |
| PR artifact | [13519465636](https://github.com/chung6a8m/SaasOne/actions/runs/42246223331/artifacts/13519465636), ZIP SHA-256 `82758d417cdcb3a302d73bfb5dbb7a798592da56a6f4caae5a6dc88cbf5994b7`; downloaded ZIP hash, generated JSON and all TRX counters inspected |

| Gate | Result |
| --- | --- |
| Exact toolchain, locked restore, Release build | PASS; 0 warnings/errors |
| Non-SQL unit suite | 74 discovered/executed/passed; 0 failed/skipped |
| Architecture suite | 26 discovered/executed/passed; 0 failed/skipped |
| Real SQL/Orchard suite | 48 discovered/executed/passed; 0 failed/skipped |
| Combined test suites | **148 passed, 0 failed, 0 skipped** |
| Compiled native/restored graph, hygiene, lock inventory | PASS; 164 combined nodes |
| Both retained migration snapshots and PowerShell parsing | PASS; no pending model changes |
| Inherited real SQL smoke | PASS; wrong-target rejection and low-privilege positive probe |
| SQL test exit, mandatory named cases and cleanup | PASS; test/cleanup/final exit codes all 0 |

The SQL evidence includes all six parameterized cold-process cases and named lookup, registration isolation, platform-negative control, preloaded-secret hot registration and late-secret restart cases. Every one is Passed in the actual TRX. The strict fixture validates named cases and parameterized counts, not only an aggregate total. The negative SQL smoke intentionally exits 1 internally; its wrapper verifies this rejection and finishes successfully.

Environment: Ubuntu 22.04.5 x64, image `20261101.347.1`, SDK 10.0.403, PowerShell 7.6.9, .NET/EF 10.0.14, unchanged Orchard 3.0.1 selection; SQL Server Developer build 16.0.4305.5, digest `sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`.

## Review disposition and later documentation commits

Fix/evidence replies were posted to [R1](https://github.com/chung6a8m/SaasOne/pull/3#discussion_r4494458548), [R2](https://github.com/chung6a8m/SaasOne/pull/3#discussion_r4494461089), and [R3](https://github.com/chung6a8m/SaasOne/pull/3#discussion_r4494463799). Findings are not erased or represented as independent approval. A documentation-only closeout after this implementation SHA must receive its own exact-head/PR CI; record those results in the PR without creating an endless evidence-commit cycle.

Local dotnet/pwsh/Docker are unavailable in the editing session; execution used the repository's existing pinned read-only GitHub Actions workflows. No independent subagent review, merge or production deployment is claimed. ADR 0001 remains Conditional pending M2 PoC. Single operations-host, unidentified-database reconciliation and environment-only secret-delivery constraints remain applicable.
