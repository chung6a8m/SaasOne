# M1 — Private tenancy provisioning

## Supported boundary

M1 implements the SQL Server baseline from roadmap revision 1.3. It does not enable identity/SSO, business endpoints, CMS administration, customer-editable recipes or a public provisioning API. The two accepted plan labels (`Standard`, `Approval`) share the foundation profile until their business modules arrive in M4/M5.

The HTTP process and the maintenance process are separate OS identities. Run maintenance only on one trusted operations machine with a private durable local directory. Do not place that directory on a network share, copy its `host-id` to another machine, delete live `.lock` files or run a second independently configured operations host. Cross-host recovery is deliberately blocked pending a reviewed reconciliation procedure; this is not a distributed auto-failover worker.

## Secrets and SQL identities

Inject secrets with the deployment secret manager into the appropriate process environment. Never put connection strings in Git, shell history, command arguments, recipe JSON, logs or evidence. Do not enable EF sensitive-data logging. `SAAS_SQL_SERVER` is the exact configured SQL endpoint; all supplied connections must target that endpoint and the expected database.

| Environment variable | Purpose / required SQL identity |
| --- | --- |
| `SAAS_PROVISIONER_CONNECTION` | Private maintenance only; `master`; dedicated server provisioner with `CREATE ANY DATABASE`, `ALTER ANY LOGIN`, and `VIEW SERVER PERFORMANCE STATE`. It must own/manage the databases it creates. `sa` is used only in the disposable CI fixture. |
| `SAAS_PLATFORM_MIGRATOR_CONNECTION` | Private maintenance only; `saas_platform`, login `saas_platform_migrator`; owner of this database, not of the server or other customer databases. |
| `SAAS_PLATFORM_OPERATOR_CONNECTION` | Private maintenance only; `saas_platform`, login `saas_platform_operator`; `saas_operator` role grants registry/checkpoint writes and append-only operational audit. |
| `SAAS_PLATFORM_RUNTIME_CONNECTION` | HTTP process; `saas_platform`, login `saas_platform_runtime`; SELECT on `platform`, DML only on reviewed Orchard tables; no schema mutation. |
| `SAAS_TENANT_{TENANT_ID_N_UPPER}_MIGRATOR` | Private maintenance only; database `saas_t_{tenantId:N}`, login `saas_mg_{tenantId:N}`. |
| `SAAS_TENANT_{TENANT_ID_N_UPPER}_RUNTIME` | HTTP process; same customer database, login `saas_rt_{tenantId:N}`; read-only `tenancy` schema, DML only on reviewed Orchard tables, no cross-database or DDL privileges. |
| `SAAS_OPERATIONS_HOME` | Private maintenance only; durable local path owned by the operations OS identity. Unix directory permissions are restricted to its owner. |

SQL authentication is the implemented role adapter. The secret manager must persist the same credentials across retry; an existing SQL login is authenticated, never reset/reassigned automatically. Use encrypted SQL transport with certificate validation in production. CI alone uses a trusted ephemeral loopback fixture certificate. The HTTP process must not receive provisioner, operator, migrator or operations-home secrets. The acceptance suite starts separate runtime processes with only runtime connections to enforce this credential boundary; it does not certify separate CI OS accounts.

### Environment-only delivery and newly provisioned tenants

M1 reads the current process environment, not a live secret-manager API. Changing the CLI/parent/service configuration after HTTP startup does not update the running HTTP process. See [.NET process-environment semantics](https://learn.microsoft.com/en-us/dotnet/api/system.environment.getenvironmentvariable?view=net-10.0#remarks).

For no-restart registration, allocate the future TenantId and persist its credentials first, inject that tenant's runtime secret into the HTTP environment BEFORE starting the HTTP process, and then provision using the separate maintenance process. Once provisioning completes, verified shell reload can register the tenant in that already-running HTTP process. The runtime receives only runtime credentials, even for the preallocated tenant.

When a new runtime secret was not present at HTTP startup, provision may complete but that HTTP process must keep the new tenant closed. Update the deployment's runtime-only environment from the secret manager and perform a controlled HTTP process restart/container recreation before readiness can pass. Existing healthy tenants remain available until that planned restart. This also applies to environment-only credential rotation/correction; M1 does not implement dynamic cross-process secret delivery or zero-downtime rollout. Do not give maintenance credentials to HTTP or edit a process's environment through a public endpoint to avoid the restart.

The process acceptance cases launch HTTP first, provision through a separate CLI child, and test both preloaded-secret success and late-secret 503 followed by readiness after restart. An in-process environment mutation is not evidence for deployment secret propagation.

## Build and bootstrap once

Use the exact versions in `global.json`, `Directory.Packages.props`, the tool manifest and the pinned PowerShell toolchain.

```powershell
dotnet tool restore
dotnet restore SaasOne.slnx --locked-mode
dotnet build SaasOne.slnx --configuration Release --no-restore
pwsh -NoProfile -File scripts/check.ps1 -Scope M0
```

Allocate and persist an installation UUID in the private deployment inventory, then provide it explicitly. The example variables below are values retrieved from that inventory, not fresh values generated on each invocation.

```powershell
./scripts/tenant-provision.ps1 -Bootstrap -InstallationId $installationId
```

Bootstrap creates the fixed platform database, applies only the platform-owned EF baseline, records the installation identity, initializes the closed Orchard foundation and validates it using the low-privilege runtime identity. Completion and its audit are committed together. Running bootstrap again after completion returns a nonzero exit code; it does not modify the installation or reopen setup.

## Provision or retry

Allocate and persist one TenantId and one OperationId before starting. Preserve the full request tuple, including the exact expiry, across every retry. Follow the environment-delivery procedure above for the HTTP process separately from maintenance credentials.

```powershell
./scripts/tenant-provision.ps1 `
  -TenantId $tenantId -OperationId $operationId `
  -Slug alpha -Plan Standard -ExpiresAt $expiresAt
```

Repeat for `beta`, `gamma`, `delta`, with distinct TenantId/OperationId values and their respective secrets. Slugs are canonical lowercase ASCII, at most 48 characters, start with a letter, and cannot use reserved system prefixes. Database names are derived from immutable IDs, not slugs or request-provided connection strings.

Checkpoint order is `Registered → DatabaseReady → OrchardReady → RuntimeVerified → Completed`. The registry subscription and expiry are not operation checkpoints. Claim/checkpoint/failure/completion writes use a common registry version and fencing identity; checkpoint and operational audit writes share a platform transaction. Completion also checks current expiry in SQL, so a newer suspension, pending-deletion state, expiry or revalidation gate cannot be overwritten by an old worker.

A per-tenant OS lock remains held for the entire operation, together with a non-expiring SQL session application lock. Native work is awaited and disposed before releasing those locks. A retry with a recorded prior owner must run on the same operations host and prove that the prior owner's tagged SQL sessions have ended before performing resource mutations. Do not treat elapsed time as evidence that an owner has stopped.

A completed retry is read-only verification. A retry with a different payload or OperationId for the existing tenant fails. Partial native warm-up can resume the same operation. SQL/audit/checkpoint failure never publishes success. A failed operation keeps HTTP readiness closed.

## Reconciliation and failure handling

An existing customer database must already contain exactly one matching, nonempty TenantId and the original database nonce. Provisioning never adopts an unidentified database and never overwrites its identity. A database created immediately before a process crash may have no identity yet; automatic retry intentionally fails closed in that narrow creation/identity gap. An operator must investigate provenance and obtain approval to remove only the confirmed empty orphan, or restore a verified owned baseline, before retrying. This runbook does not authorize dropping a database, editing identity, clearing fencing values or changing lifecycle by hand.

The same rule applies to a platform database without its original installation marker. A changed operation host, still-active prior SQL sessions, wrong target/credentials, corrupted schema/history/seed/profile or unknown state requires investigation, not an override flag. There is no public or CLI bypass that marks such a tenant ready.

Maintenance output contains safe error codes, exception type, SQL error number and application method names only. Consult restricted server diagnostics separately; redact credentials before attaching evidence. Fix the failing prerequisite and rerun the exact persisted request only where its checkpoint permits reconciliation.

## HTTP and readiness

`GET /health/live` proves only process liveness. `GET /health/ready` proves the fixed platform baseline. Customer readiness is `GET /{slug}/health/ready`; `GET /{slug}/` is a minimal read-only foundation page with tenant-prefixed links. Operational or baseline failures return a generic 503 without database details. Unknown tenant routes return a controlled error, never customer business through Default.

A customer registration/secret contract error is isolated while loading the native shell inventory: that customer stays closed, while valid customers and Default can initialize. Suspended/expired/incomplete tenants are not loaded. A named shell reload validates only the requested customer, not unrelated customer secrets. Platform connection, bootstrap and platform-query failures are NOT swallowed and keep all readiness closed. Correcting an environment-delivered secret still follows the controlled restart rule above.

The outer boundary checks fresh registry/lifecycle, identity, EF history, seed, native Orchard descriptor/migration and least-privilege SQL health before passing to Orchard. The inner boundary confirms the actual Orchard-selected shell, prefix, TenantContext and customer connection. No header, token, query or body field can select the customer database. All responses are non-cacheable; shared application cache keys use immutable TenantId and length-framed components.

Setup, admin, feature, template, recipe and upload HTTP surfaces are closed even before bootstrap. The reviewed typed recipe `SaaS.Foundation.M1.v1` enables only the application/default feature and `SaaS.Theme.App`; it accepts no scripts or arbitrary feature list. The JSON in `src/SaaS.Web/Recipes` documents this profile, not an executable customer-provided recipe.

## Migration ownership and architecture

Tenancy alone owns `platform` and `tenancy` EF histories and migrations. Orchard owns `dbo.oc_*` and its native migration record. Runtime code never invokes EF migrations. The identity trigger rejects UPDATE/DELETE even through the migrator; runtime SQL permissions also reject INSERT/UPDATE/DELETE on the identity schema.

The architecture gate combines restored project/package references with actual compiled Orchard feature metadata, including BaseTheme and each feature owner's project/package dependencies. Direct, transitive and mixed feature→package paths from platform/theme to business fail; Orders→Approvals is likewise prohibited. Same-owner native feature dependencies must not create artificial ownership cycles. Missing first-party feature references fail. Dormant third-party OpenID feature dependencies absent from the selective M0 package set are recorded as `UnavailableNativeFeature`; none may be reachable from the enabled M1 profile. Enabling identity remains the M2 PoC/ADR gate, not an implicit M1 dependency upgrade.

## Acceptance commands and evidence

```powershell
pwsh -NoProfile -File scripts/check.ps1 -Scope M0
pwsh -NoProfile -File scripts/sql-fixture.ps1
pwsh -NoProfile -File scripts/m1-sql-fixture.ps1
```

The last two commands create isolated Docker SQL Server fixtures and must not be pointed at a deployment. The M1 fixture requires at least 48 executed passing SQL cases, including six cold-process secret/lifecycle combinations and named process/restart regressions. It fails if SQL is unavailable, tests are not discovered/executed, a mandatory case fails/skips, or cleanup fails. It retains source SHA, pinned image/digest, command exit status and TRX counters in `artifacts/verification/m1-sql.json`; CI uploads the full evidence and redacted logs. A build or liveness response alone is not M1 acceptance.

Owner review of security, migration ownership, the single-operations-host constraint and failure reconciliation is required before merge. No production tenant or database is created by opening the PR.
