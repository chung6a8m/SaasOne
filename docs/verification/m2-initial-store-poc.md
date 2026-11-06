# M2 verification — partial native issuer-store PoC

## Milestone decision

**M2: FAIL / incomplete. Do not merge PR #4 as a completed milestone.** ADR 0001 remains Conditional. This delivery implements a test-only issuer-store bridge, not central login, SSO, a JWT authenticator or the complete M2 identity subsystem.

Baseline: `2e1dd5568c204e3422e8c9222771e7796cf0eb25` (merged M1).
Branch: `feat/m2-identity-poc`.
Plan and adapter contract: `docs/tasks/m2-identity.md`.

## Source-bound execution evidence

Implementation SHA: `977e23d74f6d975c6333afe5a507cd017e4fa74a`.
GitHub Actions push run: `42259545489`; job: `128035554871`.
Run URL: https://github.com/chung6a8m/SaasOne/actions/runs/42259545489
Completed on 2026-11-06 at approximately 17:17 UTC. The run is intentionally unsuccessful overall because the real PKCE entrypoint is still absent. This is a development result, not architecture acceptance.

| Check | Discovered / executed | Passed | Failed | Skipped | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| Locked restore and Release build | — | — | — | — | Exit 0; 0 warnings, 0 errors |
| M0 unit tests | 74 / 74 | 74 | 0 | 0 | PASS |
| Architecture tests | 26 / 26 | 26 | 0 | 0 | PASS |
| Architecture / tracked hygiene / lock inventory | — | — | — | — | PASS; 164 restored project/package nodes |
| Platform and tenant EF snapshot consistency | 2 checks | 2 | 0 | 0 | PASS; no pending model changes |
| Inherited restricted-user SQL smoke | 1 / 1 | 1 | 0 | 0 | PASS |
| Inherited M1 SQL / Orchard tests | 48 / 48 | 48 | 0 | 0 | PASS |
| M2 native issuer-store subtests | 9 / 9 | 9 | 0 | 0 | PASS at store layer only |
| M2 HTTP PKCE entrypoint | 1 / 1 | 0 | 1 | 0 | FAIL: expected 302; actual 404 |
| Other mandatory end-to-end cases | 17 missing | — | — | — | NOT IMPLEMENTED; never counted as skipped or passed |

The complete `Category=SqlM2` run discovered/executed 10 cases: 9 passed, 1 failed, 0 skipped. Test exit code was 1; disposable-container cleanup exit code was 0. There are no passing mandatory end-to-end identity cases yet. The SQL smoke harness deliberately includes a negative privileged-connection probe before its successful restricted-user probe; that expected rejection must not be confused with the final smoke result.

The raw-log development artifact for this historical run was removed for log hygiene, as recorded below. The GitHub job log retains the test counts and complete machine-readable inventory. The current workflow retains only JSON reports and TRX files; subsequent run results must be associated with their own source SHA rather than relabeled as this run.

## What the nine native cases actually prove

The fixture uses real SQL Server, real Orchard 3.0.1 shell containers and real OpenIddict/YesSql managers. Each case provisions isolated native table prefixes through the private maintenance path. Each runtime identity receives DML only on its own explicitly selected tables; there is no runtime DDL or `db_owner` grant.

The passing cases demonstrate: Default/customer native-store separation; committed token/authorization fields and linkage; Default-store lookup from Alpha and Beta service scopes; rejection of missing, expired, wrong-subject, wrong-authorization, customer-only and refresh-token records; committed token and authorization revocation visible to the next fresh scope; survival of an independent grant across that revocation; preservation of revocation after rebuilding the runtime shell host; unavailable rather than cached-allow on a real issuer-store SELECT denial; refusal of an entity-cache-enabled configuration; and request-cancellation propagation.

**These are not authenticated HTTP requests.** No JWT is issued or validated by this fixture, no browser session is created, and the independent grants do not prove the same-user multi-tenant refresh-family policy. `IssuerEntryDecision.Valid` means only that the native store checks passed. It must never be used as a standalone authentication decision.

## Findings fixed during implementation

1. M1 runtime permissions are an explicit table allowlist. New isolated native PoC tables initially failed with SQL permission errors. Setup now grants only those individual tables; this fixture failure was not counted as behavioral TDD evidence.
2. The pinned OpenIddict 7.5 manager returns the canonical token-type URN. Comparing a stored access-token type with `TokenTypeHints.AccessToken` incorrectly rejected valid entries. The adapter and committed-record regression test now use `TokenTypeIdentifiers.AccessToken`.
3. Orchard tokens and authorizations use the `OpenId` YesSql collection. The dependency-failure test now denies SELECT on that collection's document table, not the unrelated default document table. Recovery is checked after restoring the grant.
4. Capturing raw workflow stdout also captured `add-mask` directives, including the randomly generated credentials of disposable SQL fixtures. Raw stdout capture/upload was removed in `8fb3862ff0d2faa7f66981aaccb658369bf9012e`; masking directives now reach the runner directly. One-time cleanup run `42260503251`, job `128038877352`, removed artifacts `13524830359`, `13523643134`, `13524077644` and `13523683817` from the four explicitly selected M2 development runs and verified zero matching artifacts remained. These credentials belonged to disposable SQL instances, not a deployment. The temporary cleanup workflow is removed from the final diff; the retained verifier has read-only repository permissions.

## TDD and regression history

- `aef2c251e714fb643b46b51b859ad3ee53474a6a`, run `42254300573`: build and 100 M0 tests passed; first real HTTP PKCE test failed 404 versus 302.
- `8f5e5ce78dc0b66696542473e874634e60f5a2fc`, run `42257349180`: native store-separation positive control passed; bridge cases failed against the invalid-only stub. Six SQL cases executed, one passed and five failed including PKCE.
- `b6560b5cbefd2003bdc8431abd402ab50e240839`, run `42258863312`: full inherited gates passed; the committed-record diagnostic identified the token-type hint/URN mismatch. Ten M2 cases executed, one passed and nine failed.
- `977e23d74f6d975c6333afe5a507cd017e4fa74a`, run `42259545489`: nine native cases passed after the fixes; the real PKCE route remained failing. Full baseline results are tabulated above.

## Reproduction and gate behavior

Pinned environment: .NET SDK 10.0.403, PowerShell 7.6.9, Orchard 3.0.1, YesSql 5.4.7, OpenIddict 7.5.0, SQL Server 16.0.4305.5. SQL image: `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04`; observed digest: `sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`.

Run from the repository root with the pinned tools and Docker available:

```powershell
pwsh -NoProfile -File scripts/check-m2.ps1
```

The script runs M0, both EF snapshot checks, inherited SQL smoke, complete M1 SQL tests and then M2. The M2 command is:

```powershell
dotnet test tests/Unit/SaaS.UnitTests/SaaS.UnitTests.csproj --configuration Release --no-build --filter 'Category=SqlM2' --logger trx --results-directory artifacts/test-results/m2-poc
```

Reports: `artifacts/verification/m2-poc.json` and `artifacts/test-results/m2-poc/*.trx`. The gate rejects zero discovery, any failing/skipped test, duplicate/missing mandatory cases or incomplete cleanup. Missing mandatory cases are listed even when another test fails. A native-store subtest cannot substitute for a similarly themed end-to-end case.

The local editing runtime was unavailable; all build/SQL results above came from actual GitHub Actions runs, not local test claims. No packages, lock files, production EF models, migrations, snapshots, host routes or M1 feature descriptors were changed by the PoC.

## Remaining work before M2 can be accepted

Build an isolated HTTP PoC profile with native Orchard Users and OpenID Server, plus native tenant clients; move the existing PKCE acceptance test to that actual PoC host rather than adding a fake redirect to the M1 host. Review any required dependency/lock changes. Complete the central login and SSO flow with real code redemption, separate tenant cookies, PKCE, callbacks/logout allowlists, antiforgery and persistent keys.

Integrate cryptographic JWT validation and Default issuer-store checks into protected API and web requests. Prove tenant/audience/scope binding and same-user Alpha/Beta isolation. Implement immutable issuer+subject identity mapping, memberships and account/tenant/membership generations, authoritative fresh-state reads and 401/403/503 behavior.

Implement persistent refresh-family/session state and strict serialized rotation, reuse/concurrency/lost-response handling, tenant versus global logout, non-resurrection after lifecycle changes and restart. Only after all 18 mandatory cases listed in `scripts/m2-sql-fixture.ps1` pass may the PoC be presented for owner acceptance and dependent production integration continue. ADR status and merge authority remain with the project owner.
