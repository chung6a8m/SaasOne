# Confirmed blocked-reader negative control

This supersedes the evidentiary interpretation, not the historical outcomes, of the first SELECT-XLOCK fixture described in m7-blocking-resource-diagnostic.md. See m7-blocking-fixture-correction.md.

At head c7dd53818f294dd5217626c95e96aad5f90722fa, tested PR merge 1eed54f665eeba350139e735fc5da1d19a165c39, M1 run 42547735112 / artifact 13644570352, the fixture first proved via sys.dm_exec_requests that its exact reader was waiting on its exact blocker with an LCK wait, and that the read Task was incomplete. Only after that successful assertion did the empty negative-control probe fail the collection assertion.

Downloaded outer SHA256 13b13c453a55e2dc3d505e2a471f169a122c2f0ea25b72bdf55760f9944bbe96 matches. TRX: 198 unit +27 architecture Passed; SQL M1 53 executed, 52 passed, 1 failed, zero skipped. The only failure is Blocking_probe_observes_a_real_wait_without_exporting_identifiers_or_closing_its_connection, Assert.Contains on an empty collection, NOT the new blocked-reader setup assertion. The transaction is rolled back in finally and the waiter completes against the original gate state.

The next implementation restores exactly the compiled metadata probe blob 7de8f9d045a89fac69d58426c79526b5e2219f93 from cdc68d1, retaining the corrected fixture. No query-filter relaxation, target/workload/runtime change or changed lock semantics is used to obtain GREEN. Fresh exact-source CI is required.

The intermediate cdc68d1 Candidate artifact 13644042502 contains 163 successful sampling queries and observations in app.order-resource and orders.Orders buckets; it remains a failed Candidate and its inherited M1 fixture was FAIL. These observations are diagnostic, not accepted evidence for a green final source. The fixture validation and final source-matched load/Full runs must be completed before conclusions about the remaining bottleneck or integration.
