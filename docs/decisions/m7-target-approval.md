# M7 target approval and implementation authority

Status: **Approved for the bounded M7 acceptance experiment; final acceptance pending**.

The project owner explicitly requested in the project conversation: "Phê duyệt các lựa chọn và tiếp tục triển khai các bước còn lại tiến đến nghiệm thu M7". The decision is recorded at https://github.com/chung6a8m/SaasOne/issues/12#issuecomment-6728440377. This is owner direction recorded by the implementation agent, not an independent reviewer approval.

Approve the previously presented independent read-only Portal composition, real SQL/HTTPS four-tenant fixture, Baseline/Candidate/Acceptance separation, and the exact budgets below. The earlier proposal document and failed runs remain historical, unchanged.

- detail/list: p95 <= 1000 ms, p99 <= 2000 ms.
- create/edit/submit/approve: p95 <= 2000 ms, p99 <= 4000 ms.
- Each of 22 tenant/operation cells: >= 1 successful completion/second, >= 100 measured requests; zero unexpected errors across every phase.
- Workload hash remains `694f09001e4ba2b40ba4ed07f93786daa8cb8fc0baaf7eaa9753507415f86adc`: 100 VUs, 30-second ramp, 120-second measurement, 250-ms think time, 120-second observation deadline. The deadline is not an accepted latency.

Authorize profiling and controlled performance work. No weaker authority checks, cross-request cached Allow, runtime migration, unsafe pooling, reduced population or post-run target relaxation. Preserve exclusive migration owners and all inherited failure tests. The bounded experiment is not production capacity or a high-confidence population p99 claim.

This decision is committed before any subsequent Acceptance run. That run must embed fresh inherited Full at its exact source SHA and meet all M7 checks. Final artifact review, independent review and explicit acceptance/merge decision remain outstanding. No conditional ADR is promoted; Issue #12 is not closed by this approval.
