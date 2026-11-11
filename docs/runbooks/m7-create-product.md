# Create a product from the M7 composition example

Begin on an isolated branch with a product task plan. `samples/Portal` is the smallest read-only
platform composition. It is not a tenant SSO/business-app template; those capabilities require an
explicit integration design using the existing host contracts and their negative tests.

For another platform-only product, copy the **sample host and its own theme only** into a separate
product directory. Rename both project/assembly names, the theme namespace, manifest identity,
page branding and host-to-theme reference. Keep the host `ApplicationName` tied to its own assembly.
Do not copy `SaaS.Web`, Orders, OrderApprovals, platform source or migration files.

Select the platform projects and own theme explicitly in the new host project. Maintain fixed
server-owned connection selection and the existing runtime-principal, schema, bootstrap and
platform-release readiness checks. Keep public setup and unimplemented tenant routes absent.
Supply runtime secrets through deployment configuration, not source, files in the sample or CLI
arguments. Use a loopback-only binding for local smoke tests; deployment TLS is a separate concern.

Delete only the copied sample lock files, run a genuine restore on the pinned toolchain, review
all resolved dependencies and commit the newly generated lock files. Subsequent restore/publish
must use `--locked-mode` / `--no-restore`. Never hand-edit package lock content hashes.

Add the new product to the source-closure architecture assertion and give it a dedicated gate
following `scripts/check-m7-product.ps1`: inspect resolved packages, published `.deps.json`, all
published files and the native Orchard feature/theme dependency graph. Forbid the original product
and any unselected business implementation across **all** these graphs. A disabled feature does
not count as an absent dependency. Record assembly and artifact hashes.

Extend the published-process SQL smoke test for the new entry point and its unique own-theme
marker. It must start using runtime-only credentials, return ready/render when valid, fail closed
for missing/wrong/overprivileged source and an incomplete platform release, and keep setup and
unimplemented business routes unavailable. Re-run inherited gates before integration.

Generate product-specific business modules with `templates/module` only after declaring their
permissions, resources, audit/concurrency/idempotency and exclusive migration ownership. Shared
platform modules and shared themes must never reference the new product's business implementation.
See `m7-product-and-module-reuse.md` for module generation and current sample commands.
