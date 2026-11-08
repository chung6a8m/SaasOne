# SaaS.Module.Template

This deliberately inert module starts with only the shared Abstractions dependency.
It has no public endpoint, Orchard feature, persistence, authorization bypass or runtime migration.
Generate it under `src/Modules/<name>` in a repository using the SaasOne pinned build/central package files.
A product host explicitly selects the module; platform and shared themes never reference it.

Before adding a real operation, add its permission/resource policy, routed tenant context,
current entitlement check, transactional audit, revision/idempotency contract and negative tests.
Before adding persistence, name its exclusive schema, history table and DbContext/migration owner;
use controlled migrations and real SQL Server tests. Never use runtime EnsureCreated/Migrate.
If adding an Orchard module/feature, declare only necessary dependencies, then run the native
feature and published-artifact graph checks. Disabling a feature is not dependency isolation.

Do not copy Orders/Approvals or the original host to obtain platform functionality.
Review the generated project and regenerate/commit genuine NuGet locks before integration.
