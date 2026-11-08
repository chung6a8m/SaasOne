# M7 bounded schema connection regression — RED

Source 27ec6d09c33fe72c8fd7d9e168e43cba87cebda7; PR synthetic merge 4fb65bfa41034943b1694e62326027eb3a8b5252; M1 run 42535739066; artifact 13638414753; outer SHA256 176f221c5ace1d0f4a8a277a89fd15f0b33977954f3ab04c9bc97898e6123b27.

Downloaded checksum and TRX were checked. Locked build, 194 unit and 27 architecture tests passed. M1 discovered/executed 51 cases: 49 passed, 2 failed, zero skipped. The new behavioral test Schema_verification_uses_one_physical_open_without_retaining_a_connection failed as intended: expected one open, actual three. Schema_error_and_cancellation_leave_owned_connections_closed passed.

The second failure was a test assertion type mismatch in Schema_verification_preserves_the_callers_connection_and_transaction: SQL XACT_STATE returned numeric 1 with a different boxed numeric type than the literal int 1. The fixture reached the transaction-state assertion, after open-state and zero-new-open assertions passed. The assertion is corrected to Convert.ToInt32 of the scalar; the semantic requirement remains exactly XACT_STATE=1, not a relaxed check. This test-harness failure is not performance RED evidence.

The following narrow implementation holds a connection for the existing schema probes and closes only a connection opened by that call, preserving a caller-owned connection/transaction. No schema result cache, query removal or migration change. A fresh full M1 result is required; the old run remains FAIL. M2 runs inherited M1 first, so the authority regression must be observed separately after this prerequisite is fixed; IdentityAuthority has not yet been changed.
