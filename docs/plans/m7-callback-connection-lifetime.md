# M7 callback form/connection lifetime correction

Review of the routing optimization found that SelectAsync retains its owned platform SQL connection while DemandRegisteredCallbackAsync awaits a POST form body. Previously the per-query context had released its connection before this network await. Keep the routing improvement for steady-state API/readiness, but do not introduce a SQL-session retention window controlled by a slow form upload.

This is a correction within docs/plans/m7-routing-connections.md, not a new product/target decision. User continuation is recorded in Issue #12 comment 6729373692. No model/migration/permission/workload/target changes.

Affected files: tests/Unit/SaaS.UnitTests/IdentityPoc/IdentityCallbackConnectionTests.cs; src/SaaS.Web/Orchard/IdentityRuntimeHost.cs; verification ledger.

RED: actual product host and real SQL/native fixture, TestServer.SendAsync with an IFormFeature that pauses exactly at ReadFormAsync. At the pause, query only a count of active saas_platform_runtime SQL sessions through the private fixture administrator and require zero. Exercise POST /connect/authorize and /connect/logout. Release the body in finally and require existing invalid-callback HTTP 400. No fake authentication or authority source; only the form-read wait is controlled. Expected existing failure: one retained runtime session, not zero.

GREEN: after all unchanged platform/bootstrap/schema/native checks, close the owned platform transport BEFORE invoking the POST callback helper. The existing platform DbContext then performs the callback registry query by opening/closing the same fixed SQL source as needed after form parsing. GET callbacks retain their bounded transport; invalid content type and callback rules/error order remain unchanged. Never carry an open connection into downstream Orchard/business processing. This does not increase a timeout or cache any ready/Allow decision.

Run M0/M2 and fresh M7 Candidate plus inherited Full at the new SHA. Verify same-request invalid callback, repeated readiness connection counts, disabled source and callback drift regressions. Preserve all failed snapshots; no Acceptance/merge on a performance or inherited failure. Runtime execution is pinned CI, not local.
