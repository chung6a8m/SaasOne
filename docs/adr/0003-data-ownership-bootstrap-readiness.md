# ADR 0003 — Data ownership, SQL principals and bootstrap

Status: Proposed for owner review; runtime privilege exceptions require M1 evidence. Owners: tenancy/data implementer and operator. Scope: R04, R08, R3-01; roadmap §3, §5.1, §9.1.

One ASP.NET Core host; Default uses `saas_platform`; customer shells use separate SQL Server databases. Platform access uses a fixed connection through a dedicated service. Business DbContexts use only the validated Orchard-routed shell connection. No database selection from token/body/header. No migrations, actual tenant provisioning or production DB changes in M0.

## Ownership allocation (physical names are M0 choices)
| Location / schema / history | Exclusive migration owner |
|---|---|
| Platform and customer Orchard/YesSql/OpenID tables (`oc_` table prefix in `dbo`; Orchard's own migration records) | Corresponding Orchard modules/YesSql; never EF |
| Platform `platform`: registry, plans, memberships, operation/checkpoints and operational audit; `platform.__EFMigrationsHistory` | Tenancy / PlatformDbContext from M1 |
| Customer `tenancy`: immutable singleton TenantDatabaseIdentity (M1), durable write gate (M3); `tenancy.__EFMigrationsHistory` | Tenancy |
| Customer `access`: roles, role permissions, user-role and user overrides; `access.__EFMigrationsHistory` | AccessControl from M3 |
| Customer `audit`: business/security audit; `audit.__EFMigrationsHistory` | Auditing from M3 |
| Customer `orders` and `approvals`, separate history in each schema | Orders M4 and OrderApprovals M5 respectively |

These are ownership reservations, not claims that contexts/tables already exist. A task plan may choose fewer contexts without changing exclusive ownership; review that choice before consumers. Platform audit is not re-created by a competing M3 migration stream. PostgreSQL gets a separate provider migration graph later.

## SQL rights and secrets
| Identity | Intended rights and limits |
|---|---|
| Customer runtime | CONNECT only to its customer DB; narrowly granted SELECT/INSERT/UPDATE/DELETE on required application tables; SELECT-only immutable identity; no ALTER/DROP/CREATE DATABASE, no cross-DB user, no server roles |
| Platform runtime | CONNECT + required DML only in platform; not a customer DB principal |
| Migrator | DDL/DML only in an explicitly verified target DB/schema and bounded maintenance session; no server-wide database provisioning |
| Provisioner/purge operator | Separate privileged operation channel for verified DB/principal creation/deletion; not used for HTTP business traffic |
| M0 SQL smoke user | CONNECT to the disposable `saas_m0_smoke` database and permission to read engine metadata; no business schema or migration rights |

Runtime DDL-free Orchard startup is a **hypothesis to prove in M1**, not an M0 success claim. Orchard automatic migrations must run under controlled maintenance/warm-up identity before traffic. Any necessary serving-time DDL exception is tenant/schema-bounded, documented and reviewed before readiness. Never share the provisioning principal across runtime tenants.

Secret references: `SAAS_SQL_SMOKE_CONNECTION` for the M0 test connection; local ASP.NET user-secrets ID `SaasOne.Web.Development`; future runtime/migrator/provisioner references supplied by the operator's secret store. No values or connection strings in Git, logs or command-line arguments. Persistent keys are outside Git; production secret-store vendor is outside M0.

## Bootstrap and readiness
M1 Default bootstrap is closed to public traffic via authenticated private CLI/channel or a one-use secret-store credential, disabled after setup. Every provision/migrate/feature/purge command authenticates the actor and checks stable TenantId/target, operation ID, common ownership lock, fencing/checkpoints and operational audit. Retry must reconcile, not duplicate DB/admin/identity. Purge rejects the platform DB and arbitrary caller-supplied connections.

Customer DB identity is created once with its platform TenantId, never reassigned on retry or seeded over a mismatch. Runtime cannot modify/delete it. Readiness checks current registry/lifecycle + operation gates, identity, expected Orchard and EF schema, seed, feature/profile and required health. Unknown/failed/incomplete means closed. Warm-up/repair channels are separately protected. Audit/checkpoint failure cannot report success or open traffic. M0 liveness is explicitly not readiness.
