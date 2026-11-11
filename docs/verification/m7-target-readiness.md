# M7 target-readiness verification

This is an additive ledger. Historical baselines/failures and PR review 5818648969 remain unchanged. No M7 acceptance, owner approval, optimization or production readiness is claimed.

## Preserved baseline

Branch source `443e9ac2f0a51022de550437957f94a1e7dc6c23`, run `42496484263`, artifact `13622894577`. Original outer ZIP SHA256 `d48578491ca71c979d79efbd33d8674500b9cd7b1adfe67574186e1711bf5308`; inner ZIP `20155dcb7de1832a806afc9a205a0b10cac39bdbebc16b16c4c69384a5fe6b41`; exact retained load JSON `97e9ae342ef8d2ac3d01c3b2cc7a508c1bcc35788c6fd57bdddb42187360b431`.

Outer/inner/19 file hashes were reverified; all 22 cells were recomputed from 696 sanitized samples. Every cell fails all four proposed metrics (88 metric failures in total). This is a historical gap calculation, not a new load run. The unchanged workload SHA256 is `694f09001e4ba2b40ba4ed07f93786daa8cb8fc0baaf7eaa9753507415f86adc`.

## Behavioral RED

Test-first source `c5a63791841d7e1c032f571359b0745fb65872bb`; PR synthetic merge `4c029b9ae6de7f12248143bc7b29d744c70c35bd`; run `42501913132`; policy job `128796296218`.

On the pinned toolchain, the existing 21 evidence-policy negatives and literal-path package regression passed. The new regression then failed with exit 1: `Target policy accepted: Proposed target with a nonempty reviewer is not approved`. This proves the old policy checked only a nonempty reviewer, not an explicit approval state. The dependent load job did not run after the failed preflight. RED is preserved, not relabeled.

## Implementation and verification boundary

The implementation separates target definition/evaluation from approval, adds a per-cell sample floor and full failure reporting, and adds Candidate mode while preserving Acceptance's same-source inherited Full requirement. The new real-entry-point test uses a disposable clean Git fixture; it never fabricates a real owner approval or executes SQL.

At this document's commit, GREEN and the new Candidate runtime result have not yet been observed. Check the subsequent exact-head PR/Issue evidence record. Prior-source 501/501 inherited Full and seven SQL passes are not substituted for this changed source.

Review environment has no dotnet/pwsh/Docker; local checks cover JSON, exact bytes/hashes, independent raw statistics and diff structure. Runtime validation executes on pinned GitHub CI. Final owner review and actual approved-target Acceptance remain outstanding regardless of Candidate results.
