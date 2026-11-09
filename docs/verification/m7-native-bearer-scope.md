# M7 native bearer validation — source-locked RED/GREEN evidence (2026-11-09)

**Status:** bounded implementation GREEN and current-head M7 Candidate performance FAIL. Neither a production SLO nor an M7 Acceptance. PR #14 remains Draft/unmerged; Issue #12 open. The owner-approved 100-VU workload and 22-cell/88-metric target JSON files are unchanged.

## Scope / security

Plan committed at `40625fca7a37c5efcf17778431ddc86d96120295`: `docs/plans/m7-native-bearer-one-scope.md`. Runtime GREEN at `cfac0cc3548f213e03c5745bd372ee20ccb632df`, only `DefaultIssuerStore.cs`, `IdentityAuthority.cs`, `IdentityProtocol.cs` changed; existing M2 SQL test `Combined_native_validation_keeps_all_queries_one_fresh_scope_and_revocation_order` contains the regression, no new mandatory names.

Only a **single complete bearer validation** shares its *new* Default issuer scope: after verified JWT signature, issuer, audience, lifetime, token type and scope, all fresh platform issuer-family/session/account/tenant/membership/generation/client checks run; within the new native scope, UserManager account/lockout/security-stamp, authorization status/subject/invalid-access-index, **same original principal/family claim binding**, native token validity/type/subject/authorization/expiry/application and final authorization checks execute in the original order. Legacy `ValidateAsync`/web, standalone `ValidateAccessAsync`, refresh/login/logout remain on their former per-call scopes. No cross-call cached native entity, cached Allow, global pool or shared SQL session; the gate and post-lock ACL/Orders/Approvals checks are unchanged.

As with all current-state source reads, there is a concurrent-revocation race after the final check and before response; this change does **not** assert serializability with external issuer revocation occurring during one validation. Same-token successive validation after a revocation is covered. Any review evidence of stale issuer status within one scope is a blocker, not a reason to relax safety.

## RED — real M2 SQL/HTTP evidence

At regression commit `5d2526ca73971adb3dd0cd48b59980ca378d383c`, exact PR synthetic merge `5480fef37c90deeb2d57ec8ac4c60e42d4049ffc`, [M2 run 42576050747](https://github.com/chung6a8m/SaasOne/actions/runs/42576050747), artifact `13654902994`:

- Release build **0 warnings/errors**; architecture 27/27; non-SQL unit 201/201; M1 SQL 53/53; M2 real SQL **49/50 passed**, **one deliberate RED**, zero skipped.
- Existing native bearer validation test expected **one** native physical SQL open per bearer validation, actual **two**; all 32 mandatory M2 cases remained individually PASS. This was the deliberate gap, not a build/setup error.
- Independent outer artifact ZIP SHA256 `4e0e7525f874a9ae1e419352adabd3ad9e556ff39cc028336c435dc3f22597d2`; raw TRX and M2 mandatory inventory counted independently. RED is retained as FAIL.

## GREEN — unchanged authority/permission tests

At GREEN head `cfac0cc3548f213e03c5745bd372ee20ccb632df`, source-matched PR synthetic merge `04fa10a02a338aecc854da382fb664722766a9df`, [M2 run 42576763624](https://github.com/chung6a8m/SaasOne/actions/runs/42576763624), artifact `13656031136`:

- M2 suite **50/50 passed, zero failed/skipped**; all **32/32** mandatory M2 cases PASS. M0 unit 201/201, architecture 27/27, inherited M1 SQL 53/53 PASS inside the same artifact. In particular, the two fresh successive bearer validation calls measure one native open each, and a revoked native authorization denies the next bearer call.
- Independent outer ZIP SHA256 `862613f65196e4c0f26d27482efa85f309477a2444fe7f23dd3a5e5542b7af5e`; exact M2 TRX and mandatory inventory checked.

## M7 Candidate — actual performance boundary

Before optimization: [PR Candidate run 42570281448](https://github.com/chung6a8m/SaasOne/actions/runs/42570281448), artifact `13653231938`, synthetic merge `1c1f0992f9e57cf22220464215f29f7b97bc02f6`; outer ZIP SHA256 `17f9c5b33cf3d8a202a5a4607640058a9c34da188d3d47ee1499d387451c9bbf`.

After optimization: [PR Candidate run 42576763631](https://github.com/chung6a8m/SaasOne/actions/runs/42576763631), artifact `13655418450`, synthetic merge `04fa10a02a338aecc854da382fb664722766a9df`; outer ZIP SHA256 `c5f73f0e7929b34254e858bee52efd93ac2944df0d155386f92218cd05b3c91d`, inner ZIP SHA256 `733105045ed75c9bbb531a27fa5c0579db1feae2d8276bea776674545a3092a0`. ZIP sidecar and all declared inner inventory file lengths/SHA256 passed independent recalculation.

| Metric | Before | After |
| --- | ---: | ---: |
| Successful all-phase HTTP samples | 1,845 | 2,627 |
| Physical SQL opens/request | 25.75610 | **21.39475** |
| Native issuer opens | 16,056 / 8,028 fresh issuer-family validations | **11,424 / 11,424** |
| Application SQL commands/request | 119.9333 | 119.8367 |
| Completed requests in measurement window | 10.3333/sec | 15.5417/sec |
| p95 across 22 cells | 7.805–17.018s | 5.389–13.268s |
| Exact mandatory M7 SQL/HTTPS | 7/7 PASS | 7/7 PASS |
| Failed approved cells / metrics | 22 / 88 | **22 / 88** |

No unexpected HTTP request failures or reported integrity errors; independent M7 TRX counts 7/7 PASS, source/provenance and metrics matched sanitized reports. **Observation not causal SLO claim:** different runner loads may account for parts of the p95/throughput difference; the one-open-per-native-validation mechanism is proven by M2 test and M7 stage ratio itself.

### Follow-on decision

Current validated mechanism saves ~4.361 physical opens/request (~16.9% of this measured cost), but every approved p95/p99/throughput/sample-count target still fails. Current stage profile still shows `acl.authorize` ~6.70 physical opens/request and Orders resource/page lock waits, which need separate safety review and test-first plans. Neither enabling global pooling, cross-request authorization caching, shortening resource locks nor changing targets is approved.

Complete inherited M0–M6 Full green at the **final** source SHA, a separate actual `scripts/check-m7.ps1 -Mode Acceptance` on locked target, independent review and explicit owner signoff are still prerequisites. Do **not** merge PR #14 or close Issue #12 merely because the M2 and M7 correctness suites passed.
