# M7 performance investigation ledger

Owner target approval: Issue #12 comment 6728440377, committed at a4b72c6911cb5db5efa25682de0482b701f5f201. Budgets and workload numbers are unchanged.

Collector RED: de54434159acdc25e4bddce4cde061ab4b304211, PR synthetic merge c6e96c6e8402328f323f7e11426047f348a1cfaf, run 42533871427/job 128893837788. Build had zero warnings/errors; 27 architecture and 192 existing non-SQL unit cases passed. Exactly the two new collector behavioral tests failed (expected pending=2 / completed open count=1, actual=0). This is the intended missing-implementation failure, not a compile or dependency failure. The historical run stays FAIL.

The following implementation adds only test-side SQL profiling and evidence export. It has not yet produced a measured runtime result. Before any performance change, capture a fresh Candidate run of the unchanged runtime. Client timing sums overlap across requests; SQL wait totals also include background waits. Do not divide their sums by wall time to claim CPU utilization or infer a bottleneck from counters alone.
