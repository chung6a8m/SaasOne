# M4 Orders operations and API

M4 adds Orders pages and API to the native tenant identity host. It does not implement production Approvals, backup/restore, notifications, cancellation or order deletion. The field model, composition and replay-retention choices are proposed in [ADR 0007](../adr/0007-orders-command-contract.md); passing CI is not owner approval.

## Private installation and rollout

Use the pinned toolchain and M1–M3 deployment prerequisites. The HTTP process receives only runtime connections, identity configuration and its persistent Data Protection ring, never operator/migrator/provisioner credentials.

M4 advances `tenancy.ApplicationProfile` to version 2. Older profiles are deliberately not runtime-ready under this binary. During a controlled rollout, privately invoke the existing command per customer with the current registry version and a persisted operation UUID:

```powershell
dotnet <published/SaaS.Web.dll> access-install <tenant-id> <operation-id> <expected-registry-version>
```

Follow the [M3 operations protocol](m3-operations.md). Installation closes/drains the durable write gate, verifies identity/ownership, applies each owner's migrations, preserves the ACL, grants narrow runtime DML, writes the profile, verifies readiness and reopens through the shared completion protocol. Retry/resume uses the existing operation tuple; never reset an operation by editing registry rows.

A verified M3 profile can upgrade. Unknown future profiles, missing schema on installed M4 customers, unknown table inventory or unreadable obligations block reconciliation and leave the gate closed. HTTP never creates, migrates or repairs schema/profile. Resolve failures through trusted maintenance; never clear a marker, rewrite history or force-open the gate to bypass them.

Enable `SAAS_IDENTITY_ENABLED=true` and `SAAS_ACCESS_CONTROL_ENABLED=true`. There is no public feature-toggle/Orders-install endpoint. Standard includes Core and Orders; Approval includes Core, Orders and Approval. Entitlement alone grants no user permission. New Orders permissions are default-denied until explicitly assigned.

An existing `Access.Manage` holder does not automatically gain or become able to delegate Orders capabilities. Bootstrap initial capabilities through the reviewed complete-ACL recovery flow in M3. Subsequent role/override changes use its safe Access UI/API and cannot delegate a capability the actor lacks. Current membership, generation, entitlement, ownership and Deny are evaluated for every business request and replay.

## Ownership, fields and state

Orders owns `orders.Orders`, `orders.Revisions`, `orders.Receipts` and its EF history. Auditing owns the nullable resource/correlation/change-detail extension in `audit.Events`. Tenancy/Access models are unchanged. Runtime can insert/update current orders and append history/receipts/audit, but cannot delete orders, rewrite/delete retained history or perform DDL. There is no runtime receipt-cleanup endpoint.

| Field | Validation |
|---|---|
| `number` | 1–64 normalized uppercase ASCII letters/digits/dot/dash/underscore; unique within the customer database. |
| `customerName` | 1–160 UTF-16 code units after NFC normalization and trimming; plain text. |
| `amount` | Nonnegative decimal(18,2), maximum 9999999999999999.99; no excess fractional precision. |
| `currency` | Three uppercase ASCII letters after normalization; shape validation, not an authoritative ISO catalog. |
| `notes` | Required field, may be empty; maximum 2000 UTF-16 code units, normalized line endings, plain text. |

Invalid Unicode/control characters fail validation; newline/tab are allowed in multiline text. Bodies are bounded to 32 KiB before parsing, including chunked requests and percent-encoded web forms. Pages never interpret stored HTML. No uploads, rich text or executable templates are provided. Line items, tax, payment, cancellation and deletion are outside this proposed minimum field model.

`id`, `createdBy`, `status`, `approvalRequired`, timestamps and concurrency values are server-owned. Create cannot supply them. Existing-order writes must include the expected revision and rowversion from a prior read. The rowversion is an opaque base64 eight-byte SQL value, not a client timestamp/counter.

Wire statuses are Draft=1, PendingApproval=2, Rejected=3 and Confirmed=4. Create starts revision 1. Each accepted edit/transition increments revision once and appends an immutable snapshot. Edit permits Draft/Rejected and produces Draft. PendingApproval/Confirmed are immutable. There is no delete/cancel operation.

## Permissions and approval obligation

| Action | Current requirements |
|---|---|
| Create | `Orders.Create` and `Orders.ViewOwn`. |
| Read/list | `Orders.ViewOwn` or `Orders.ViewOthers`, selected by persisted creator; filtering occurs before pagination. |
| Edit | Selected `Orders.EditOwn`/`Orders.EditOthers` and corresponding read permission. |
| Submit | Selected `Orders.SubmitOwn`/`Orders.SubmitOthers` and corresponding read permission. |
| Future decision | `OrderApprovals.Decide`, corresponding Orders read access, current Approval entitlement, actor different from creator. |

A Deny on the selected permission cannot fall back to its alternate. Body/query/header hints cannot change owner, tenant, client or connection.

Standard Submit confirms only without an existing approval obligation. Current Approval entitlement or `ApprovalRequired=true` requires current Approval entitlement and a ready handler. Missing handler/schema/entitlement never falls back to Confirmed. M4 registers no production handler, so Approval-required Submit returns 503 and leaves the draft unchanged. No public Approve/Reject route is exposed until M5.

A future Reject requires a reason and matching pending revision/token and preserves the marker. Editing Rejected does not erase it. The independent reader counts PendingApproval, Rejected and rework Draft as outstanding even without current entitlement/handler. Confirmed retains marker/history but is no longer outstanding. Downgrade remains on Approval while obligations exist; unknown/missing data fails closed.

## API and screens

Paths below are under the actual tenant prefix, for example `/alpha`. API calls require that tenant's native bearer token; cookies are not API credentials. Unknown tenant/resource IDs cannot select another database.

| Method and path | Contract |
|---|---|
| `GET /api/v1/orders?page=0` | At most 50 authorized `items` and `hasMore`; page index 0–1000; no inaccessible-resource total. |
| `GET /api/v1/orders/{id}` | Current `OrderView`, including `revision` and `version`. |
| `POST /api/v1/orders` | Strict `OrderDraft` JSON; 201 `OrderCommandResult`. |
| `POST /api/v1/orders/{id}/edit` | `expectedRevision`, `expectedVersion`, `draft`; 200 result. |
| `POST /api/v1/orders/{id}/submit` | `expectedRevision`, `expectedVersion`; 200 result. |

Every POST requires one case-sensitive `Idempotency-Key`: 8–128 ASCII letters/digits/dot/dash/underscore/colon. JSON uses exact case-sensitive camelCase fields. Missing/unknown/duplicate properties, duplicate keys, wrong shapes and oversized bodies return 400. Empty `notes` is valid; omission is not.

```json
{
  "number": "SO-001",
  "customerName": "Example customer",
  "amount": 125.50,
  "currency": "USD",
  "notes": "Plain text"
}
```

For Edit, wrap that draft in `draft` and add the exact expected revision/token from GET. Submit sends only those expected values. Results contain `order`, `replayed` and `replayUntil`; Location identifies the resource. Replayed Create retains 201; replayed Edit/Submit retain 200, with `replayed=true`.

Web paths are `/{slug}/orders`, `/orders/new`, `/orders/{id}` and its edit/submit POST actions. Encoded, script-free forms carry antiforgery, a unique command key and expected concurrency values. Success redirects with 303. These are product pages, not native CMS content; service authorization is enforced regardless of visible buttons.

## Lost responses, races and failures

After a connection loss or ambiguous result, retry the **same key and original normalized command**, including the original expected revision/token. Do not substitute newly read concurrency values. A committed Submit changing state/revision does not invalidate replay of the original command.

Partitioning is tenant + authenticated actor + authenticated client + action + key. Order, revision, enlisted approval hook writes, audit and receipt commit in one customer transaction. Concurrent duplicates serialize and only one executes. Different keys on one revision have one winner. EF contexts never retry independently.

Before returning a stored result, the service checks current identity/membership/entitlement/ACL, current resource owner, the same action permission and the required read permission. Revocation/source failure denies replay even when the command originally committed. Different payload conflicts are disclosed only after that authorization; denied callers receive neither the result nor fingerprint conflict.

Replay lasts seven days from the completion timestamp recorded inside the committed transaction. Expired retained keys return authorized `idempotency_expired` (409), not a new execution. There is no automatic receipt deletion or silent key reuse. Plan storage and customer-data retention accordingly; privileged compaction needs a separate reviewed design. Audit stores before/after business fields and rejection reasons, so protect it like the Orders data.

Outcomes include 400 invalid input/CSRF, 401 credentials, 403 current permission/entitlement/membership denial, 404 unavailable route/resource, 409 state/revision/idempotency conflict and 503 unavailable source/handler/schema/audit. Sanitized errors include a correlation ID. Audit/handler failure rolls back all enlisted work. Retrying after recovery cannot duplicate a committed effect under the same key.

## M5 extension and verification

`IOrderApprovalHandler` receives the caller-owned `TenantTransaction` and revision-bound submission/decision records. Every context must enlist on that exact connection/transaction and all work must be awaited. The hook cannot commit, open an independent write connection, retry its own context or perform irreversible external effects. Future notifications require an enlisted outbox. M4 fixtures prove the seam, not production Approvals or M5 acceptance.

```powershell
pwsh -NoProfile -File scripts/check-m4.ps1
```

The full gate runs M0–M3, all 24 mandatory M4 policy/model cases, the Orders snapshot check and all 44 SQL/native-HTTP cases. Missing/duplicate/skipped/unexpected cases fail. Every prerequisite JSON must be PASS for the same SHA. Do not combine success from different commits. The readonly M4 workflow retains source and JSON/TRX; focused/development runs are not full acceptance. See [verification](../verification/m4.md) and the source-bound completion record on PR #6. Tests use disposable databases, not production. Restore/release and distributed load remain M6/M7.
