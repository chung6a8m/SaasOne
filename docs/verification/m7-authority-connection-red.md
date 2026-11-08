# M7 bounded authority connection regression — RED after schema GREEN

Source e3f13d1f9fc18bc866acd41a67189fef2e99d778; PR synthetic merge 307208e2625252e7cd954b07e01056b47ff77298.

M1 run 42536130220 / artifact 13639656777 (outer SHA256 67a09559c9f50f7aeb7e7d6e5577506d5c4888836d7de86b04c5b932a06d98e0) passed all 51 SQL cases. All three new schema tests passed: one physical open on each independent call, unchanged caller-owned connection/transaction on success and mismatch, owned connection closed on mismatch/cancellation. The corrected transaction scalar assertion still requires exactly state 1. 194 non-SQL unit and 27 architecture cases also passed.

M2 run 42536130218 / artifact 13639224108 (outer SHA256 9bdcb9e45c333703adfcccd0c2e7ce1946cc43012d92aa8f5dfe967865dedb1d) passed inherited M0/M1, then discovered/executed 45 M2 cases: 44 passed, exactly one failed, zero skips. Authority_uses_one_owned_platform_connection_per_validation_and_never_reuses_authority failed on its physical-open assertion: expected 3 (2 native reference opens + 1 platform), actual 9 (2 native + 7 platform). This is observed behavioral RED, not a compile failure. The subsequent login-disable/membership assertions have not yet executed because the earlier assertion failed.

Both downloaded outer hashes and TRX counts/outcomes were checked directly. These sources do not prove any later fix. The following change opens the newly owned PlatformDbContext once for ValidateAsync, retains every query/check in the same order, and disposes via the existing await-using lifetime. No pooling, cached authority, transaction or native-store change. Fresh M2 and inherited Full evidence plus a new M7 load comparison are still required; the RED run remains FAIL.
