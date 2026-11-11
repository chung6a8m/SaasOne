# M7 native validation scope verification ledger

Plan: docs/plans/m7-native-validation-scope.md, committed at 1fd22167cfdfbb4c9497ef8d185efd97f4cfee90 before runtime implementation. Approved target and workload bytes remain unchanged.

## Preserved setup failure

The first adapter/test commit 2fc4fec2c54743b30470e406269b86de1afef829 did not compile because the new test lacked the SaaS.Tenancy.Data import for IdentityFamily and IdentitySession. M2 run 42544176802 at synthetic merge b36f43aa5eb04340e6accf34b46675797b929f53 remains FAIL; this is not behavioral RED evidence. Commit 46626c16d9f965331d508e902724a93007d75c71 fixes only that import.

## Behavioral RED

M2 PR run 42544445423, artifact 13642743525, head 46626c16d9f965331d508e902724a93007d75c71, tested merge acbd22cf903282d1f3a8e69e9b23fb16f2c5b884. Outer SHA256 0e6511ea488256d6dd49678d7efbc740fd5d0aa959dda3902cb1aaa18d57f13c was checked after download.

All 221 non-SQL cases and 52 SQL M1 cases passed. SQL M2 executed 48, passed 46, failed 2, skipped 0. Both Authority_uses_one_owned_platform_connection_per_validation_and_never_reuses_authority and Combined_native_validation_keeps_all_queries_one_fresh_scope_and_revocation_order failed on expected one native physical open, actual two. The adapter still delegated to the unchanged two native checks, and production ValidateAsync still called those checks separately.

## Implementation

Extract the existing account and authorization bodies into private provider-taking methods. The legacy public methods retain a fresh scope each. The combined method creates one NEW scope per invocation, reads account first then authorization/invalid-access index without removing any query. IdentityAuthority.ValidateAsync calls it after the unchanged platform queries/checks. ValidateAccessAsync is unchanged and still creates a separate fresh scope for token plus authorization recheck; no entity is reused across validations or after a lock boundary.

Fresh runtime verification at the implementation SHA is required; no GREEN, performance acceptance or final sign-off is claimed in this source snapshot. Prior RED/setup failures remain failures. Runtime execution is on pinned CI, not locally.
