# M3 private operations and application authorization

Implementation verification, owner architecture/security acceptance and merge are separate. See [M3 evidence](../verification/m3.md) and PR #5 for source-bound results. This is not a SQL restore runbook; SQL backup/restore rehearsal remains M6. Orders and OrderApprovals remain M4/M5.

## Separation of privileges

The HTTP host receives runtime connections only. Do not deploy provisioner, migrator or operator secrets to the HTTP process. `SAAS_ACCESS_CONTROL_ENABLED=true` opts the identity-enabled host into the M3 application routes. It does not enable customer CMS administration, arbitrary Orchard features, SQL DDL or business modules.

Run maintenance in a separate, trusted operator process with the existing M1 secret references and SQL roles. The CLI authenticates the actual SQL login in the fixed platform database; a command-line actor name is not authority. Resource operations also require the server provisioner and the registered customer migrator. Never pass passwords, connection strings, token material or certificates in argv. The console must not be exposed as an HTTP endpoint.

Use `pwsh -NoProfile -File scripts/operations-console.ps1 -AssemblyPath <published/SaaS.Web.dll>` for the minimal terminal UI. Its actions invoke the same maintenance commands below. It does not retry conflicts automatically or change a target silently. Cancellation/failure leaves durable state for inspection and an explicitly authorized resume.

## Commands

Invoke `dotnet <published/SaaS.Web.dll> --maintenance <command> ...`. UUIDs use the canonical `D` form. Versions are positive decimal registry versions. Read `ops-list` again after every operation: intermediate checkpoints, lifecycle changes and retries advance the registry version.

| Command | Arguments | Purpose |
|---|---|---|
| `ops-list` | none | Authenticated, non-secret tenant status, expiry, plan, version, operation and recovery state. |
| `access-install` | tenant, operation, expectedVersion | Install/upgrade owned schemas and the deployment manifest under the durable gate; initially empty ACL. |
| `ops-lifecycle` | tenant, expectedVersion, `active`/`suspended`/`delete`, expiry | Change lifecycle with compare-and-swap; expiry uses round-trip `O` format. `delete` means PendingDeletion, not DROP. |
| `ops-plan` | tenant, operation, expectedVersion, `Standard`/`Approval` | Change plan through the guarded protocol. |
| `ops-resume` | tenant, operation, expectedVersion | Resume the recorded plan operation with its original target, after prior-owner quiescence checks. |
| `ops-recovery-begin` | tenant, operation, expectedVersion | Close access, advance authorization generation and invalidate prior sessions/grants before ACL reconciliation. |
| `ops-recovery-approve` | tenant, operation, expectedVersion, ACL-file, `keep-plan`/`allow-approval-upgrade` | Explicitly approve the complete replacement ACL using pre-registered external recovery authority. |
| `ops-recovery-resume` | tenant, operation, expectedVersion | Revalidate membership/authority, replace ACL and append audit atomically, verify, then open only if all fences remain current. |
| `ops-purge` | tenant, operation, expectedVersion, repeated-tenant-UUID | Irreversible target-only purge after PendingDeletion. The repeated UUID is an explicit confirmation, not a second target selector. |

The expiry changes and suspension do not wait for a long resource operation. They preserve the operation owner/fence and invalidate its stale registry version. A completed resource step must not overwrite a newer suspension, deletion or expiry decision. PendingDeletion is not reversed by the lifecycle command.

## Initial ACL and recovery

`access-install` grants no administrator implicitly. Initial state is empty and deny-by-default. To establish initial application administrators, use the same explicit external recovery approval flow: begin, review a full ACL document against current memberships, approve, resume. This avoids treating a role name, restored tenant ACL or application request as recovery authority.

A platform administrator must separately register the actual SQL operator in `RecoveryAuthorities` before the recovery operation starts. An ordinary operator cannot self-register through these commands. That authority is outside the customer database. Plan-upgrade approval additionally requires the registered `CanChangePlan` authority. Revocation or unknown authority during apply keeps access closed.

The ACL file is bounded to 1 MiB and must contain the complete document (roles, assignments and user overrides), not a patch. Set the non-secret review explanation in `SAAS_RECOVERY_REASON` (maximum 1000 characters). Do not put credentials or personal details in the reason. Approval records the document hash, decision, actor and reason. A decision cannot be silently replaced after application may have started. Keep the approved file in an operator-controlled location and delete local transient copies according to your operating policy.

Example shape (replace the role/user UUIDs with reviewed IDs; numeric effects are Unspecified=0, Allow=1, Deny=2):

```json
{
  "Roles": [
    {"Id":"11111111-1111-1111-1111-111111111111", "Name":"Application administrator", "Permissions":[
      {"Permission":"Access.Manage", "Effect":1},
      {"Permission":"Workspace.View", "Effect":1},
      {"Permission":"Workspace.Approval", "Effect":1},
      {"Permission":"Profile.ReadOwn", "Effect":1}
    ]}
  ],
  "Assignments":[{"UserId":"22222222-2222-2222-2222-222222222222", "RoleId":"11111111-1111-1111-1111-111111111111"}],
  "Overrides":[]
}
```

Check `PermissionEffect` before adapting integrations. `Access.Manage` is nondelegable through customer role editing; external review is required. An operator approval of an empty ACL is valid and leaves all application permissions denied.

## Tenant UI and API

`/{tenant}/access` is the minimal role editor (full ACL JSON, current revision and write-gate version). It requires a current tenant cookie, current `Access.Manage`, and antiforgery on POST. Names and JSON are HTML encoded. `/{tenant}/api/v1/access` exposes the same service using a native bearer token; cookie fallback is not supported. Send an `AclChange` with operationId, expectedRevision, gateVersion and document. Duplicate operation/payload returns the recorded revision only after current authorization; a changed payload or stale revision is a conflict.

`workspace`, `workspace/approval` and `profile/{userId}` have corresponding `/api/v1/` paths and exercise permission, entitlement and owner-resource boundaries without pretending to implement Orders. The routed Orchard shell selects the tenant connection. Query parameters, headers, claims and body values never choose a database or the owner of a stored resource.

Every request checks current identity/account/membership/lifecycle/generation, ACL, deployment manifest and schema histories. Deny dominates all allows. A prior allow is not reused after dependency failure. The API and UI return a correlated closed error rather than an authentication redirect on source failure. ACL writes repeat authorization after their administration lock. Roles, their archive and the audit event commit on one customer connection/transaction; audit failure rolls back the complete mutation.

## Plan and deletion safety

M3's foundation deployment has no business obligation tables. Its independent reader recognizes only its exact owned/foundation tables and returns Unknown for unfamiliar tables. It must be replaced/extended by explicit persisted Orders/Approvals readers in M4/M5; do not broaden it to accept all `dbo` or `oc_*` tables. Contract fixtures already exercise approval obligations even while the entitlement is disabled and the approved recovery child-upgrade path. No command directly toggles a feature to bypass that protocol.

Purge requires PendingDeletion, the common exclusive resource lease, prior worker quiescence and a matching immutable database identity. It retains the platform tombstone/audit/global accounts and unrelated tenant memberships/databases. A retry reconciles durable identity-verified deletion intent; a missing database without that intent is not success. Do not manually clear operation IDs, fences, write gates or security-revalidation flags to bypass a failed operation. Use the operation ID and latest registry version to investigate and resume through the private command.

After a completed purge, remove that retired tenant's runtime/migrator/client secret references from the external deployment configuration and recreate affected processes. The CLI revokes/drops the SQL principals and disables platform client authority; it cannot mutate a parent process environment or an external secret store. Do not remove shared issuer certificates, Data Protection keys or global accounts as tenant cleanup. Any native client left by an interrupted registration has no authority without an enabled current platform registration.

## Verification

Run `pwsh -NoProfile -File scripts/check-m3.ps1` on the pinned toolchain with Docker. SQL tests create disposable loopback instances; never point the fixture at a deployment. The full gate includes M0/M1/M2, model snapshots and all mandatory M3 cases with source SHA, image digest and TRX counts. A component-only success is not full milestone acceptance. Architecture/security choices and merge remain owner decisions.
