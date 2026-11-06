# M0 decision register

Baseline: roadmap revision 1.3, `639a03253bc653758f2a028909cec9b163e0de73`. Owner means the responsible project role, not an invented named assignee. The project owner reviews/merges. **Documented is not runtime verified.** Added defaults remain proposed for review; identity remains Conditional.

| ID | Decision / artifact | Owner | Current status | Runtime gate / missing evidence |
|---|---|---|---|---|
| R01 | Platform-only executable CMS; uploads/rich text closed; ADR 0002 | Security + tenancy | Documented | M1 recipe/direct API/bootstrap restrictions; M3 privilege changes |
| R02 | Central issuer, independent clients; ADR 0001 | Identity | Conditional — pending M2 PoC | Cross-shell issuer-store validation/SSO/revocation on pinned graph |
| R03 | Authoritative per-request checks, generations, strict rotation; ADR 0001–2 | Identity + access-control | Documented | M2/M3 post-commit revoke, source errors, expiry/re-add/restart/concurrent refresh |
| R04 | Closed bootstrap, distinct SQL principals and single schema ownership; ADR 0003 | Tenancy + operator | Documented | M1 real startup DDL rights, readiness and provisioning negative tests |
| R05 | Durable write gate/drain/checkpoints, no direct toggle; ADR 0004 | Tenancy | Documented | M3 fixture races/crashes; M5 real Submit/Approve |
| R06 | Atomic audit, Orders extension/revision/idempotency; ADR 0006 | Audit + Orders | Documented | M3 contract before consumer; M4/M5 rollback and duplicate-command proofs |
| R07 | Exact toolchain, genuine locks, real SQL smoke, non-empty evidence | Build implementer | Implementation under verification | All roadmap §7.1 commands and suite counts on the final source SHA |
| R08 | Host composition; reusable platform/theme independent of business | Platform | Skeleton + negative fixtures | M0 actual graph; M1 feature graph; M7 second product publish graph |
| R2-01 | Restored ACL not current; replace via current recovery authority; ADR 0005 | Security + operator | Documented | M3 fixture; M6 restore and new-login revocation proofs |
| R2-02 | Preserve ApprovalRequired across reject/rework; ADR 0006 | Orders + Approvals | Documented | M3 guard fixture; M4 model; M5 both downgrade orderings |
| R2-03 | Reauthorize replay, do not rerun command-state preconditions; ADR 0006 | Orders + security | Documented | M4/M5 lost response, changed revision and revoked rights |
| R3-01 | Registry/manifest/immutable DB identity triple check; ADR 0003/5 | Tenancy + operator | Documented | M1 identity; M6 wrong source/tampered chain before overwrite and after restore |
| R3-02 | Common RegistryVersion/resource ownership; ADR 0004 | Tenancy + operator | Documented | M3 lifecycle races; M6 real restore/release overlap and purge quiescence |
| R3-03 | Approved child upgrade while restore parent remains closed; ADR 0005 | Recovery + Orders | Documented | M3 fixture, M4 reader, M5 feature-disabled data, M6 SQL recovery scenario |

## Additional implementation selections requiring owner review
Linux x64 (Ubuntu 22.04), GitHub Actions `ubuntu-22.04`, the exact versions and selective Orchard package composition in the compatibility matrix, platform/host/theme boundaries, SQL schema/history ownership names, and ADR 0001's explicit token/session TTLs. These are selected on an isolated implementation branch to make M0 reproducible, not claims of prior product approval. No production host, RTO/RPO, billing, SPA or Kubernetes decision is made.

The isolated implementation and CI experiments do not establish prior owner approval. Owner review/adoption is still outstanding and is a required M0 governance gate before merge or dependent implementation. No agent may close the runtime rows merely because the ADR is committed. Follow-up milestone plans must carry the relevant owner, negative cases, contracts and evidence forward. M2's identity proof is a separate gate even after owner review.
