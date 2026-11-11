# M7 routing-connection regression ledger

This extends the measured bounded-lifetime work; it is not an Acceptance PASS.
Plan: docs/plans/m7-routing-connections.md, committed at c512efeb9087b6e4b016f8cc8e2bc557601171cd before implementation.

## Behavioral RED

Test-only head f7ece00974d32c1c8f706655b41bee9d33edac21; tested PR merge 25674471db52b40f44b1840cd734ac924c91c153. M2 run 42540621948, artifact 13641206804; outer SHA256 4515939358e31f2c72c30bdbbcb3c531fc12692a04eb1f6a1ee67226aacb2ca2.

The downloaded artifact was hash-checked. TRX: 27 architecture and 194 non-SQL unit cases passed; 51/51 SQL M1 passed. SQL M2 discovered/executed 47, passed 46, failed 1, skipped 0. The exact failure was Product_routing_readiness_uses_one_fresh_connection_per_database_per_request: expected one physical open, observed seven for warmed platform readiness. This is a behavioral failure, not compilation. The source-failure test disabling/re-enabling customer and platform runtime logins passed.

## Implementation boundary

Preserve all source queries and their ordering. Borrow one owned platform and one owned customer connection only inside selection verification, dispose before Orchard/downstream execution, and open new unpooled connections on every request. Existing string helper APIs remain owned wrappers; SQL-connection overloads borrow. No cached authority, global pooling, target/workload change, post-lock check removal, transaction/lock-order or migration/model change.

Runtime verification of this implementation is pending a fresh CI run at its exact SHA. The RED remains FAIL regardless of subsequent results. Editing environment has no dotnet/pwsh/Docker; no local runtime test is claimed.
