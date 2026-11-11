# M7 callback connection lifetime review correction

Plan docs/plans/m7-callback-connection-lifetime.md was committed at 57700e2b35a02cf5c9ba8d1176ee41fadb907f3d before implementation. Self-review of the routing optimization found that its longer verification connection lifetime could retain a platform SQL session while a client POST form body is awaited. This is corrected before integration, not dismissed because the main load does not exercise slow form uploads.

## Preserved setup failure

Initial test commit 38e0e26cf2c16c781ca5959164e71220b4eff8e1 failed to initialize the lazy product Default shell before retrieving TestServer. M2 run 42548611817 / artifact 13644652006 at 635bc2039fe4858cc0b43ec7b391f3a16d1a007d remains FAIL; the tenant-name setup exception is NOT behavioral RED. Commit 6a1df2a38dd58d065dbc49ea39c970ef615069ff adds a real /health/ready warm-up only.

## Behavioral RED

M2 PR run 42549235529 / artifact 13644483801 at merge 7db087690fe48fdc0f0d7e7b298a71dfaed447a3, head 6a1df2a38dd58d065dbc49ea39c970ef615069ff. Downloaded outer SHA256 f2e8a26d361030f865f80d6ddf32f2c011699371136612d0a23d54a264c565b1 matches.

TRX: 198 unit +27 architecture Passed; 53/53 SQL M1 Passed; SQL M2 50 executed, 48 Passed, 2 Failed, zero skipped. Both POST /connect/authorize and /connect/logout tests reached their paused ReadFormAsync and observed ONE live runtime SQL session where ZERO was required. Form release and request draining occur in finally. This is the real behavior under review, not a build/setup failure.

## Minimal correction

Close the already-owned platform SqlConnection immediately before the existing callback helper for POST only, after all unchanged bootstrap/principal/schema/native checks. Do not parse form input earlier or move error boundaries. EF reopens the same fixed source for the registry lookup after form parsing and closes it when finished. GET callbacks and normal tenant/readiness/load requests retain bounded connection reuse. No authority result, connection or entity survives into the next request; no change to target, workload, deadline, pooling, SQL permissions, migration/model or lock order.

Fresh implementation-SHA CI must establish the two callback cases GREEN, inherited callback drift/source-failure/revocation tests, M7 Candidate and Full. No final acceptance is claimed by this source snapshot.

## Diagnostic verification update

Blocking probe GREEN at head 6f39a6d1a49703d5c3a99069bce358c77c7ea699: M1 run 42548188408 / artifact 13643944243, merge 93dbcdd447ba7069b9f124d26e219c9a87be9379, outer SHA256 9bb5e6f91f67a93af2cc202721827deac33dd1488d1aec835da4398b604f54da. All 225 non-SQL and 53 SQL M1 cases passed; the corrected test proves an actual blocked reader before the real metadata probe identifies WriteGate, preserves the lock and leaves its borrowed observer connection open.

M7 PR run 42548188358 at that source was cancelled/superseded by the next test push. Its artifact 13644306675 is partial, not a completed load report. The incomplete verifier's sourceCleanAfter=false is not proof of a dirty checkout: the after-run check was never reached. No performance result or acceptance is attributed to the cancelled run. Preserve that state and use the fresh final-source run instead.
