# DMS-1356 — Vendor namespace-prefix updates: persisted client UUIDs, lock participation, and multi-client compensation

## 1. Document control and approval state

| Item | Value |
|---|---|
| Ticket | [DMS-1356](https://edfi.atlassian.net/browse/DMS-1356) — *Vendor namespace-prefix updates strand recreated Keycloak client UUIDs and bypass workflow serialization* |
| Branch | `DMS-1356` (created from `main` at `5a8c5ac82`) |
| Parent design record | `reference/design/configuration-service/DMS-1218-cms-error-response-compliance.md`, §9.1 "Workflow serialization", §9.1 "Provider update atomicity (INV-65)", §12.8 fourth-review finding V-1, §12.8.1 T-1 |
| Revision | R4 — **implemented**. The R2 specification was approved by the architect on 2026-09-17 with two amendments (§5.3 ambiguous current-client outcome; §9.5/§11 E2E scope), both applied then. Q-1..Q-4 are answered in §16. §17 records what landed, §18 the verification evidence, and §19 the limitations found while implementing. R4 (2026-09-18) replaces the R3 fan-out cap with a single-session lock set on a dedicated, bounded lock pool (§17 Step 7.1); the third review's findings and their dispositions are in §20. |
| Author | Samuel Lugo with Claude Code |

Facts are tagged **[JIRA]** (ticket text), **[REPO]** (verified in the repository at `5a8c5ac82`), or **[DESIGN]** (a decision this spec proposes). Every **[DESIGN]** item is open to challenge until approval.

## 2. Objective

Make `PUT /v3/vendors/{id}` a participating, consistency-preserving writer of identity-provider clients: every affected client's stable identity is resolved before mutation, every affected application aggregate lock is held in ascending id order through UUID persistence or compensation, every provider-returned client UUID is persisted with a guarded state transition, and no failure branch can return success while database and provider state disagree. **[JIRA]**

## 3. Defect as verified in the repository **[REPO]**

1. `VendorModule.Update` (`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/VendorModule.cs`) calls `IVendorRepository.UpdateVendor` first, which **commits** the vendor scalars and namespace prefixes and then reads the vendor's `ApiClient.ClientUuid` values **after** the commit, outside any lock. The module then loops those UUIDs through `IIdentityProviderRepository.UpdateClientNamespaceClaimAsync` and discards the UUID in `ClientUpdateResult.Success` (`case ClientUpdateResult.Success: continue;`).
2. `KeycloakClientRepository.UpdateClientNamespaceClaimAsync` deletes the client and recreates it under a **new UUID**. The recreated client also carries **only** the role mapper and the namespace mapper: the `educationOrganizationIds` and `dataStoreIds` claim mappers are dropped, and the service-account realm role mapping that `CreateClientAsync` assigns is never reassigned (the recreate path contains no role lookup or `AddRealmRoleMappingsToUserAsync` call). The delete-and-recreate is therefore lossy beyond the UUID. Whether the copied `DefaultClientScopes` survives the recreate is not established by V-32 (which probed `PUT`, not `POST`) and is not relied on by this spec.
3. `OpenIddictClientRepository.UpdateClientNamespaceClaimAsync` updates the `OpenIddictApplication.ProtocolMappers` JSON in place and returns the stored UUID unchanged.
4. A provider failure at client *k* returns immediately: clients 1..*k*-1 already carry the new claim, the database already holds the new prefixes, and nothing is rolled back.
5. The workflow acquires no `IApplicationLockManager` lock. DMS-1218 §9.1 records it as a non-participating writer.
6. Pre-existing rows already stranded by past vendor updates cannot be repaired by CMS (no `clientId`-based provider re-resolution exists). They are outside this ticket; operators delete and recreate such ApiClients.

## 4. Decisions **[DESIGN]**

### D-1 Keycloak `UpdateClientNamespaceClaimAsync` becomes an identity-preserving in-place update

**Decision.** Replace delete-and-recreate with the INV-65 mechanism: read the stored client (`GetClientAsync`), upsert the `namespacePrefixes` hardcoded-claim mapper in the client's existing `ProtocolMappers` (replace its `claim.value` when present, append the mapper when absent), leave every other mapper untouched, set `Secret = null!` so the secret is never written back, and `UpdateClientAsync(realm, uuid, client)`. Return `Success(storedUuid)`.

**Why in-place.** The V-32 probe against the pinned Keycloak 26.1 image established that a client `PUT` preserves the UUID, fully synchronizes protocol mappers, and preserves the secret when sent as `null`. The same mechanism is already in production for `UpdateClientAsync`. In-place removes the root cause (UUID rotation), the collateral losses in §3.2 (dropped mappers, lost role mapping), and the window in which a client does not exist. No scope convergence is needed because the namespace update does not change the claim-set scope.

**Why the ticket's acceptance criteria still hold.** The workflow is written **provider-indifferent**, exactly as DMS-1218 INV-54 compensation is: after every successful provider call it persists *whatever* UUID `Success` carries through the guarded `SyncApiClientUuid`. A stable UUID resolves as `AlreadyApplied`; a rotated UUID (any future or third-party provider that recreates) is written under the guard. "Every UUID returned by a successful namespace-claim update is persisted to the correct ApiClient row" is therefore satisfied by construction on both providers, and the fixtures in §9 pin it with a rotating fake provider as well as with stable-UUID fakes.

**Phase-aware failure classification (INV-66).** The stored-client lookup is its own phase: a Flurl 404 → `FailureNotFound`; other Flurl → `FailureIdentityProvider`; other exception → `FailureUnknown`. The client update addresses the client directly, so its 404 is likewise `FailureNotFound`; `false` from `UpdateClientAsync` → `FailureUnknown`. A stored identifier that is not a UUID → `FailureUnknown` before any provider call.

**Rejected alternative.** Keeping delete-and-recreate and persisting the new UUID. It satisfies the letter of the ticket but keeps the mapper and role losses of §3.2, keeps a no-client window on every vendor edit, and would need the ApiClient recreate path to reproduce `CreateClientAsync`'s role assignment and scope convergence. DMS-1218's sixth review already rejected recreate for `UpdateClientAsync` on the same grounds.

### D-2 Workflow order: provider first, repository commit last

**Decision.** Mirror `ApplicationModule.Update`: mutate every provider client first, then commit the vendor row with `UpdateVendor`. The database write is the commit point. A failure before it leaves the database untouched; compensation is a single direction (provider rollback to the original prefixes).

**Rejected alternative.** Keep the current database-first order and roll the vendor row back on provider failure. It needs two compensation directions (provider and vendor row), its 404 arrives only after provider mutation, and a failed vendor-row rollback leaves the database, not just the provider, wrong.

**Accepted exclusion (mirrors S-2).** `InsertApplication`/`InsertApiClient` read the vendor's stored prefixes without participating in the locks. A client created while this workflow runs receives the *stored* (old) prefixes and is not in this workflow's snapshot; it is not repaired by this request. Likewise `ApplicationModule.Update` does not re-derive the namespace claim when it changes `VendorId`; that pre-existing gap is out of scope and recorded in §12.

### D-3 Provider re-sync always runs

**Decision.** Every successful `PUT /v3/vendors/{id}` re-syncs every affected client's namespace claim, as today. No "skip when the prefixes are unchanged" optimization. **Reason:** an identical retry after a failed compensation must converge; a skip-when-equal would leave a drifted provider claim in place whenever the caller re-sends the stored value. The cost (locks and one in-place provider `PUT` per client on every vendor edit) is the pre-existing cost minus the delete. **Confirmed by the architect (Q-1, §16).**

### D-4 Ambiguous repository outcome resolution reuses the row-locking state read

`UpdateVendor` returning `FailureUnknown` or throwing is resolved with the new `GetVendorUpdateState` under the still-held locks, classified on the scalars and the normalized prefix set. The reread compares business values only — the commit also writes audit fields the comparison never sees — so **only a state that moved off the original values proves the commit landed**:

| Matches command | Matches original | Outcome |
|---|---|---|
| Yes | No | Committed → **204**, no compensation |
| No | Yes | Provably not committed → provider rollback → **500** |
| Yes | Yes | **No-op request: neither commit nor rollback is established** → provider rollback → **500** |
| No | No | Partial/unclassifiable → **500** with the state logged, no compensation |

The overlapping row is the no-op `PUT`, whose requested values already equal the stored ones. Equal business values cannot prove the failed write committed, and the workflow returns **204** only on a confirmed commit, so it takes the same conservative path as the proven rollback rather than recovering a success. Compensation there restores the vendor's stored prefixes — which a no-op has just asked for anyway — through the same guarded persistence of any returned UUIDs. A `VendorUpdateResult.Success` never reaches this resolution, so an ordinary no-op `PUT` still returns **204**. This is the `ResolveAmbiguousOutcomeAsync` pattern of `ApplicationModule.Update`, narrowed by the `!MatchesOriginal` conjunct on its recovered-success arm.

## 5. Behavioral contract of `PUT /v3/vendors/{id}` (target)

### 5.1 Phases

```
P0  Guards ........ route/body id match; FluentValidation. Nothing consumed on failure.
P1  Pre-read ...... GetVendorUpdateState(id)  → 404 / 500 / snapshot S0 (no lock held)
P2  Locks ......... distinct ApplicationIds of S0.Clients, ascending, as ONE lock set on one
                    database session (IApplicationLockManager.AcquireAllAsync)
P3  Re-read ....... GetVendorUpdateState(id) under locks → S1
                    ApplicationId set(S1) != set(S0) → release all, retry from P1 (max 3) → 409
P4  Provider ...... for each client c in S1.Clients (ordered by ApplicationId, then ApiClient.Id):
                      r = UpdateClientNamespaceClaimAsync(c.ClientUuid, command.NamespacePrefixes)
                      r is Success(u) → SyncApiClientUuid(c.Id, expected: c.ClientUuid, new: u)
                      any failure → §5.3 compensation → error response
P5  Repository .... UpdateVendor(command) → 204 / vanished → §5.3 / duplicate company → §5.3 409 /
                    ambiguous → D-4
P6  Release ....... every held lock disposed on every path (success, failure, throw, cancel)
```

A vendor with **zero** clients skips P2–P4 and P6 (nothing to lock, nothing to mutate) and runs P5 directly. Locks are never acquired before P0 succeeds.

### 5.2 Resolution and drift

* `GetVendorUpdateState` returns the vendor scalars (`Company`, `ContactName`, `ContactEmailAddress`), its normalized prefixes, and every client as `(ApiClientId, ClientId, ClientUuid, ApplicationId)`, tenant-scoped, with the **Vendor row read under a row lock** (`FOR UPDATE OF v` / `WITH (UPDLOCK, HOLDLOCK)`) inside one transaction so it waits out an in-flight vendor-row transaction and the client list is read in the same snapshot.
* The **under-lock** snapshot S1 is authoritative. Its `ClientUuid` values are what P4 targets and what the sync guard expects, so an Application or ApiClient workflow that committed a UUID change before this request acquired its locks is honored, never overwritten.
* **Drift** is a change in the set of owning application ids between S0 and S1 (an application added to or removed from the vendor, or a client moved to another application). The locks are released and P1–P3 retry; the third failure answers the retriable **409 conflict**. A client added to an already-locked application by a non-participating insert is simply included in S1.

### 5.3 Compensation

All compensation runs **under the held locks**. "Roll back" a client means `UpdateClientNamespaceClaimAsync(<the UUID this request last persisted or observed for it>, S1.NamespacePrefixes)` followed by the guarded `SyncApiClientUuid(c.Id, expected: <that UUID>, new: <returned UUID>)`. Rollback continues across every mutated client even after one rollback fails, so mixed state is minimized; the response is then a sanitized **500** with each inconsistency logged.

**The current client *k* on a provider failure is an ambiguous outcome, not a proven no-op.** A returned failure or a thrown exception does not prove the provider left client *k* unchanged (an in-place `PUT` can apply before its response is lost). Client *k* is therefore handled in its own right, distinctly from clients 1..*k*-1, whose mutation is known:

* When the failure is anything other than `FailureNotFound` and *k*'s stored identifier is valid, *k* is **rolled back against the UUID observed under lock (S1)**, exactly like any other rollback, including the guarded sync of whatever UUID the rollback returns. A `Success` proves *k* consistent.
* When the failure is `FailureNotFound`, the stored client does not exist; there is nothing to roll back and nothing is ever recreated (INV-64). *k* is logged as a stored-client disappearance.
* When *k*'s rollback itself fails (`FailureIdentityProvider`, `FailureUnknown`, `FailureNotFound`, or thrown), *k*'s consistency **cannot be proven**. The **possible** inconsistency is logged at Error with the vendor id, ApiClient id, UUID, and both prefix values, and the response keeps the **original failure's sanitized classification** (502 for an identity-provider failure, 500 otherwise): an unreachable provider that refused the update almost certainly refused the rollback too, and the state of *k* is unknown rather than known-wrong. This differs deliberately from clients 1..*k*-1, whose mutation *is* known, so a failed rollback there is a **known** inconsistency and forces **500**.

| Failure point | Compensation | Response when compensation completes | Response when compensation fails |
|---|---|---|---|
| P4 provider returns `FailureIdentityProvider` for client *k* | Roll back *k* against its S1 UUID (ambiguous outcome, see above), then clients 1..*k*-1 | **502** bad-gateway (fixed detail); also **502** when only *k*'s own rollback is unprovable (logged as possible inconsistency) | **500** when any of 1..*k*-1 fails to roll back |
| P4 provider returns `FailureNotFound` for client *k* (stored client missing — INV-45) | No rollback of *k* (nothing exists; never recreated); roll back 1..*k*-1 | **500** | **500** |
| P4 provider returns `FailureUnknown`, an unrecognized variant, or throws for client *k* | Roll back *k* against its S1 UUID, then 1..*k*-1 | **500** (also when only *k*'s rollback is unprovable; logged) | **500** |
| P4 sync for client *k* returns `FailureStaleState` (row now holds a UUID this request never observed — a non-participating writer) | Roll back client *k*'s provider claim (provider only; the row is not ours to write), then 1..*k*-1 | **500** | **500** |
| P4 sync returns `FailureNotExistsSafeToDelete` | If the returned UUID ≠ the expected UUID (this request created the provider client) → delete it, `FailureClientNotFound` counting as success; otherwise roll back the claim only. Then 1..*k*-1 | **500** | **500** |
| P4 sync returns `FailureNotExists` (row missing, UUID referenced elsewhere) | Nothing deleted; roll back the claim of *k*, then 1..*k*-1 | **500** | **500** |
| P4 sync returns `FailureUnknown`, unrecognized, or throws | Treated as stale: roll back the claim of *k*, then 1..*k*-1 | **500** | **500** |
| P5 `UpdateVendor` → `FailureNotExists` (vendor vanished under our locks; cascades delete its applications and rows) | Roll back every client's claim (provider `FailureNotFound` is idempotent success; sync `FailureNotExists*` accepted as the expected row absence; a rotated recreated client that is `SafeToDelete` is deleted) | **404** | **500** |
| P5 `UpdateVendor` → `FailureDuplicateCompanyName` (rename onto a company that already exists in the tenant; the unique violation proves the vendor row did not commit) | Roll back every client's claim; never enters D-4 resolution | **409** `urn:ed-fi:api:conflict:non-unique-identity` | **500** |
| P5 `UpdateVendor` → `FailureUnknown` or throws | D-4 resolution: matches the command **and not** the original → nothing; matches the original (including the no-op overlap, where the commit stays unproven) → roll back every client; partial/unresolvable/vanished → log, no compensation beyond the vanished-vendor rule above | **204** / **500** / **500** | **500** |
| P2 lock timeout (any position in the set) | The lock manager releases the locks the session already took before it reports the timeout | **409** conflict (retry) | — |
| P2 lock infrastructure failure | Same release inside the lock manager | **500** | — |
| P2/P3 throws or is cancelled | Dispose held locks, rethrow | `GlobalExceptionHandler` 500 / client abort | — |
| P3 drift on the third attempt | Locks released | **409** conflict | — |
| P1/P3 `GetVendorUpdateState` → `FailureUnknown` | Locks released | **500** | — |
| P1 `GetVendorUpdateState` → `FailureNotExists` | No locks taken | **404** | — |

No branch returns **204** unless every provider client this request mutated carries the requested prefixes, every UUID it returned is persisted (or proven already stored), and the vendor row commit is confirmed.

### 5.4 Response bodies

All bodies are `application/problem+json` through the existing `FailureResults`/`FailureResponse` factories; no new factory and no provider or exception text in any body.

| Situation | Status | `type` | Detail |
|---|---|---|---|
| Lock timeout; persistent drift | 409 | `urn:ed-fi:api:conflict` | `Unable to process the request due to a concurrent modification. Retry the request.` (identical to Application/ApiClient) |
| Provider `FailureIdentityProvider` after complete rollback | 502 | `urn:ed-fi:api:bad-gateway` | `FailureResults.BadGateway("Identity provider error during client update", trace)`; `errors[0]` is the INV-41 fixed fallback |
| Stored provider client missing (`FailureNotFound`) | 500 | `urn:ed-fi:api:internal-server-error` | `FailureResults.Unknown` |
| Internal consistency failure (stale sync, missing row, partial state, unresolvable outcome) | 500 | same | `FailureResults.Unknown`; the inconsistency is logged at Error with vendor id, ApiClient id, and the UUIDs involved |
| Compensation failure | 500 | same | `FailureResults.Unknown`; each failed rollback logged |
| Vendor not found (pre-read or vanished after full rollback) | 404 | `urn:ed-fi:api:not-found` | existing `Vendor {id} not found. It may have been recently deleted.` |
| Success | 204 | — | empty |

### 5.5 Logging

`ILogger<VendorModule>` (the handler currently injects `ILogger<ApplicationModule>`; corrected as part of the rewrite). Provider messages pass through `LoggingUtility.SanitizeForLog`. Every compensation step logs its outcome. Two distinct wordings are used so an operator can tell a known from an unknown state: a branch that leaves database and provider **known** to disagree logs `stored client state is inconsistent` (the DMS-1218 wording); the current client *k* whose rollback could not be proven logs `stored client state may be inconsistent` with the vendor id, ApiClient id, UUID, original prefixes, and requested prefixes.

## 6. DMS-1218 invariants preserved

| Invariant | How this ticket preserves it |
|---|---|
| Per-application aggregate lock domain (`dmscs:application:{id}`), ascending acquisition, held through compensation | Vendor update acquires the same locks from the same `IApplicationLockManager`, ascending, released only in P6. **[R4]** They are taken as one lock set on one database session (`AcquireAllAsync`), on exactly the same lock keys/resources, so a lock set and the single-application locks of the Application/ApiClient workflows contend with each other and the three workflows still share one domain and one total order. |
| Guarded UUID sync refuses stale state; a recreated client is deleted only when proven unreferenced | Only `SyncApiClientUuid` writes `ClientUuid`; deletion happens only on `FailureNotExistsSafeToDelete` **and** only for a UUID this request itself received from a recreate (returned ≠ expected). |
| Lock timeout → retriable 409; lock infrastructure → sanitized 500 | Same helper shape and same body. |
| INV-45/INV-66: stored provider client disappearance is a sanitized 500, never 404 or 502 | `FailureNotFound` from the namespace update is 500 in both providers; the Keycloak lookup phase is classified separately. |
| INV-65: a provider update never deletes or replaces a client that exists when it begins | Keycloak namespace update is in place (D-1). |
| Provider-first / repository-last with provider rollback on repository failure; ambiguous outcomes resolved by a row-locking state read | D-2 and D-4. |
| Error bodies: `application/problem+json`, fixed details, strict INV-41 provider-error parsing, `correlationId` present | Only existing `FailureResults` factories are used. |
| Application and ApiClient workflows unchanged | No edit to `ApplicationModule`, `ApiClientModule`, `IApplicationRepository`, `IApiClientRepository` implementations, or `UpdateClientAsync`. |

## 7. Files expected to change

| Area | File(s) | Change |
|---|---|---|
| Provider | `backend/EdFi.DmsConfigurationService.Backend.Keycloak/KeycloakClientRepository.cs` | `UpdateClientNamespaceClaimAsync` in place (D-1) |
| Provider tests | `backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/KeycloakClientRepositoryTests.cs`, `OpenIddictClientRepositoryTests.cs` | New fixtures |
| Repository contract | `backend/EdFi.DmsConfigurationService.Backend/Repositories/IVendorRepository.cs` | `GetVendorUpdateState`, `VendorUpdateState`, `VendorApiClient`, `VendorUpdateStateResult` |
| Repositories | `backend/…Postgresql/Repositories/VendorRepository.cs`, `backend/…Mssql/Repositories/VendorRepository.cs` | Implement the read |
| Repository tests | a new `VendorConsistencyOperationTests.cs` in each of `backend/…Postgresql.Tests.Integration/` and `…Mssql.Tests.Integration/` (Q-3) | State-read fixtures |
| Workflow | `frontend/…AspNetCore/Modules/VendorModule.cs` | `Update` rewritten per §5 |
| Workflow tests | `frontend/…AspNetCore.Tests.Unit/Modules/VendorModuleTests.cs` | `Given_…` fixtures per §9.1 |
| Workflow integration | `backend/…Postgresql.Tests.Integration/WorkflowConcurrencyTests.cs`, `…Mssql.Tests.Integration/WorkflowConcurrencyTests.cs` | Vendor fixtures per §9.3 |
| E2E | `tests/EdFi.DmsConfigurationService.Tests.E2E/Features/Vendors.feature`, `StepDefinitions/StepDefinitions.cs` | Scenario and two capture/compare steps per §9.5 |
| Docs | this file; `reference/design/configuration-service/README.md`; `DMS-1218-cms-error-response-compliance.md` §9.1 dated corrections | Record the change |

Not changed: `ApplicationModule.cs`, `ApiClientModule.cs`, ~~lock managers~~, `IApplicationRepository`, `IApiClientRepository`, `OpenIddictClientRepository.cs`, `IIdentityProviderRepository.cs` (no new result variants), database schema. **[Corrected 2026-09-18, R4]** — the lock contract and both lock managers gained the lock-set operation and the dedicated lock pool (§17 Step 7.1); the single-application operation and its callers are unchanged.

## 8. Repository contract **[DESIGN]**

```csharp
// IVendorRepository
/// Reads the complete update-relevant state of a Vendor and every ApiClient it owns inside a
/// transaction that row-locks the Vendor row, so the snapshot reflects any in-flight vendor
/// update's final outcome and the client list belongs to the same snapshot.
Task<VendorUpdateStateResult> GetVendorUpdateState(int vendorId);

public record VendorApiClient(int Id, string ClientId, Guid ClientUuid, int ApplicationId);

public record VendorUpdateState(
    string Company,
    string? ContactName,
    string? ContactEmailAddress,
    string NamespacePrefixes,      // stored prefixes joined with ',' exactly as GetVendor returns them
    VendorApiClient[] Clients      // ordered by ApplicationId, then Id
);

public record VendorUpdateStateResult
{
    public record Success(VendorUpdateState State) : VendorUpdateStateResult();
    public record FailureNotExists() : VendorUpdateStateResult();
    public record FailureUnknown(string FailureMessage) : VendorUpdateStateResult();
}
```

SQL shape (PostgreSQL; MSSQL uses `WITH (UPDLOCK, HOLDLOCK)`):

```sql
SELECT v."Company", v."ContactName", v."ContactEmailAddress"
FROM "dmscs"."Vendor" v
WHERE v."Id" = @Id AND {TenantContext.TenantWhereClause("v")}
FOR UPDATE OF v;

SELECT "NamespacePrefix" FROM "dmscs"."VendorNamespacePrefix" WHERE "VendorId" = @Id;

SELECT ac."Id", ac."ClientId", ac."ClientUuid", ac."ApplicationId"
FROM "dmscs"."ApiClient" ac
JOIN "dmscs"."Application" a ON a."Id" = ac."ApplicationId"
WHERE a."VendorId" = @Id
ORDER BY ac."ApplicationId", ac."Id";
```

Tenant scoping comes from the Vendor row predicate; applications and clients are reached only through that vendor. `UpdateVendor` is unchanged in Phases 1–5. Whether its now-unused `AffectedClientUuids` is removed is **Q-2**.

**Prefix comparison for D-4** normalizes both sides as `Split(',', RemoveEmptyEntries | TrimEntries)` into a set, mirroring the validator and the repository's insert normalization; order is not significant.

## 9. Test strategy

Every guarded branch gets a fixture that is demonstrated to fail under a targeted mutation (the DMS-1218 INV-62 bar). NUnit, FluentAssertions, FakeItEasy; fixtures named `Given_…`, arrange-and-act in `[SetUp]`, one `It_…` per assertion.

### 9.1 Frontend unit — `VendorModuleTests` (fakes + `RecordingLockManager`)

A vendor test base registers fakes for `IVendorRepository`, `IApiClientRepository`, `IIdentityProviderRepository`, and a `RecordingLockManager` (copied from the ApiClient tests: records acquisition order and handle disposal). Default state: vendor 1 with clients across applications `{30: [ac 5], 10: [ac 3, ac 4]}` so ascending order `[10, 30]` differs from discovery order.

| Fixture | Pins | Mutation that must fail it |
|---|---|---|
| `Given_a_vendor_update_with_clients_across_applications` | locks acquired `[10, 30]`; provider called once per client with the **under-lock** UUID; `SyncApiClientUuid(id, expected, returned)` per client; `UpdateVendor` called only after the last provider call; 204; all handles disposed | unordered acquisition; targeting S0 UUIDs; dropping the sync; DB-first order |
| `Given_a_vendor_update_whose_provider_preserves_the_uuid` | sync returns `AlreadyApplied` → 204 | treating `AlreadyApplied` as failure |
| `Given_a_vendor_update_whose_provider_rotates_the_uuid` | each new UUID passed to sync with the correct `expected`; 204 | discarding the returned UUID (the original defect) |
| `Given_a_vendor_update_with_no_clients` | no lock, no provider call, `UpdateVendor` called, 204 | — (regression) |
| `Given_a_vendor_update_for_a_missing_vendor` | 404 before any lock or provider call | — |
| `Given_a_vendor_update_whose_pre_read_fails` | 500, no lock | — |
| `Given_a_vendor_update_with_an_invalid_body` | 400, no lock, no read | acquiring before validation |
| `Given_a_vendor_update_when_the_first_lock_times_out` / `…second_lock_times_out` | 409 contract (DeepEquals), earlier handles disposed, no provider call | — |
| `Given_a_vendor_update_when_a_lock_fails` | 500, handles disposed | — |
| `Given_a_vendor_update_whose_application_set_drifts_once` | re-read differs on attempt 1, matches on attempt 2 → 204; first handles disposed; second acquisition ascending on the new set | no retry |
| `Given_a_vendor_update_whose_application_set_keeps_drifting` | 409 after 3 attempts; every handle disposed; no provider call | unbounded retry |
| `Given_a_vendor_update_whose_under_lock_reread_throws` | exception propagates → 500; handles disposed | leaking locks on throw |
| `Given_a_vendor_update_whose_lock_acquisition_is_cancelled` | `OperationCanceledException` propagates; earlier handle disposed | — |
| `Given_a_provider_failure_on_the_second_client` | client 2 rolled back against its S1 UUID with original prefixes (ambiguous outcome) and its rollback UUID synced; client 1 rolled back targeting its persisted UUID and synced; client 3 never called; `UpdateVendor` never called; 502 with fixed contract; sentinel absent | no rollback of *k*; no rollback of 1..*k*-1; DB commit anyway |
| `Given_a_provider_failure_whose_own_rollback_cannot_be_proven` | client 2's rollback fails at the provider; client 1 still rolled back; **502** retained; `may be inconsistent` logged for client 2 only | downgrading to 500; skipping client 1 |
| `Given_a_missing_stored_client_on_the_second_client` | no provider call targets client 2 again (nothing recreated); client 1 rolled back; 500 | 502 or 404; a recreate |
| `Given_an_unknown_provider_failure_on_the_second_client` / `…a_thrown_provider_failure…` | client 2 rolled back, client 1 rolled back; 500; sentinel absent | — |
| `Given_a_failed_rollback_of_a_known_mutated_client` | rollback of client 1 (known mutated) fails, 500 even though client 2's own rollback succeeded; `is inconsistent` logged | treating a known failure like the ambiguous one |
| `Given_a_stale_uuid_sync_on_the_second_client` | client 2 claim rolled back, no sync write for it, client 1 rolled back; 500; nothing deleted | overwrite on stale |
| `Given_a_safe_to_delete_sync_for_a_rotated_uuid` | `DeleteClientAsync(returnedUuid)` called once; 500 | deleting a stable UUID |
| `Given_a_safe_to_delete_sync_for_a_stable_uuid` | no delete; 500 | — |
| `Given_a_referenced_missing_row_sync` | no delete; 500 | — |
| `Given_an_unknown_sync_failure` | treated as stale; 500 | — |
| `Given_a_vanished_vendor_at_the_repository_update` | every client rolled back; 404 | 404 without rollback |
| `Given_a_vanished_vendor_whose_rollback_fails` | 500 | — |
| `Given_an_unknown_repository_update_that_committed` | resolution matches command → 204, no rollback | — |
| `Given_an_unknown_repository_update_that_did_not_commit` | resolution matches original → all rolled back → 500 | 204 |
| `Given_a_thrown_repository_update` | same resolution path | — |
| `Given_an_unresolvable_repository_update` | resolution `FailureUnknown` → 500, no compensation | — |
| `Given_an_unrecognized_provider_result` | test-defined future `ClientUpdateResult` subtype → rollback → 500 | falling through to 204 |
| Existing fixtures (`SuccessTests`, `FailureNotFoundTests`, `FailureUnknownTests`, `FailureDefaultTests`, validation, id boundary) | remain green; `SuccessTests` and `FailureUnknownTests` need the new repository fake wired (`GetVendorUpdateState`) | — |

### 9.2 Provider unit

**Keycloak** (`KeycloakClientRepositoryTests`, new `NamespaceClaimUpdateTestBase`): stored client with a mapper without `claim.name`, an `educationOrganizationIds` mapper, a `dataStoreIds` mapper, and (per fixture) with or without an existing `namespacePrefixes` mapper.

| Fixture | Pins |
|---|---|
| `Given_a_namespace_claim_update_replacing_an_existing_claim` | `Success(storedUuid)`; one `UpdateClientAsync`; `claim.value` replaced; other mappers preserved by count and name; `Secret` null; **no `DeleteClientAsync`, no `CreateClientAndRetrieveClientIdAsync`** (fails against the current implementation) |
| `Given_a_namespace_claim_update_adding_a_missing_claim` | mapper appended with the full hardcoded-claim config |
| `Given_a_namespace_claim_update_whose_stored_client_lookup_reports_not_found` | Flurl 404 → `FailureNotFound` |
| `…lookup_fails_at_keycloak` | Flurl 500 → `FailureIdentityProvider` |
| `…lookup_throws_an_unexpected_error` | `FailureUnknown` |
| `…client_update_reports_not_found` | `FailureNotFound` |
| `…client_update_reports_no_change` | `false` → `FailureUnknown` |
| `…client_update_fails_at_keycloak` | `FailureIdentityProvider` |
| `…stored_identifier_is_not_a_uuid` | `FailureUnknown`, no facade call |

**OpenIddict** (`OpenIddictClientRepositoryTests`, new `Given_UpdateClientNamespaceClaimAsync`): success returns the same UUID and writes a merged mapper JSON containing exactly one `namespacePrefixes` entry with the new value and the other claims intact; null application → `FailureNotFound` with rollback; zero rows → `FailureNotFound`; thrown → `FailureUnknown`. These are regression fixtures for code that does not change.

### 9.3 Backend integration — PostgreSQL and SQL Server (both, identical fixture sets)

**Repository** (`Given_a_vendor_update_state_read…` in each backend's new `VendorConsistencyOperationTests.cs`):
* returns scalars, prefixes, and clients across two applications with the owning `ApplicationId` and in the documented order;
* missing vendor → `FailureNotExists`; foreign-tenant vendor → `FailureNotExists` (multitenant provider);
* two-connection test: read blocks while another transaction holds an uncommitted vendor-row update and then observes the committed value (mirrors `Given_an_application_update_state_read_during_an_uncommitted_update`).

**Workflow** (extend `WorkflowConcurrencyTestBase` with a vendor state fake served from the shared `ClientState` map and a namespace-claim provider fake that records `(TargetedUuid, IssuedUuid)` and can pause):
* `Given_a_vendor_update_during_a_compensating_application_update` — the Application update is paused inside its rollback holding lock 10; the vendor update (clients on 10 and 30) blocks before its under-lock re-read, then targets the UUID the compensation persisted. **Proves lock participation and under-lock resolution.**
* `Given_an_api_client_update_during_a_vendor_compensation` — the vendor update fails on its second client and is paused inside its rollback; an ApiClient update on the first client blocks until the rollback finishes and then targets the rolled-back UUID. **Proves the locks are held through compensation.**
* `Given_a_vendor_update_and_an_inverse_api_client_move` — gated first acquisition; the vendor's `[71, 72]` and the move's `[71, 72]` contend on the same first lock and both complete. **Proves deterministic ascending order against the other participating multi-lock workflow.**
* `Given_a_vendor_update_whose_repository_commit_blocks_an_application_update` — vendor update paused inside `UpdateVendor`; an Application update on one of its applications blocks and then runs. **Proves the lock spans the repository commit.**

### 9.4 Local execution of the integration lanes

PostgreSQL: trust-auth server on 5432 with database `edfi_configurationservice`; run `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration --filter "FullyQualifiedName~Vendor|FullyQualifiedName~WorkflowConcurrency"`. SQL Server: set `ConnectionStrings__MssqlAdmin` (tests skip silently when it is absent — check the count), then the same filter against the Mssql project. Full project runs before each phase closes.

### 9.5 E2E — `Vendors.feature`, new scenario, tagged `@MssqlRepresentative` so it runs on every CI lane (PostgreSQL and MSSQL × Keycloak and self-contained)

The scenario creates **three affected clients across two applications** and, after the vendor update, exercises **every one of them** with at least one mutating operation that addresses the provider by the stored UUID, plus Application update and delete on **both** affected applications.

```
Scenario: 19 Vendor namespace-prefix update keeps every affected client addressable
  POST vendor "Scenario 19" with prefixes "uri://s19-old.org"             → 201
  POST application A (claimSet "TestClaim01") under the vendor             → 201, credentials captured "a1", id captured "applicationA"
  GET /v3/apiClients/{a1Key}                                               → id captured "a1Id", clientUuid captured "a1UuidBefore"
  POST apiClient A2 under application A                                    → 201, credentials captured "a2", id captured "a2Id"
  GET /v3/apiClients/{a2Key}                                               → clientUuid captured "a2UuidBefore"
  POST application B under the vendor                                      → 201, credentials captured "b1", id captured "applicationB"
  GET /v3/apiClients/{b1Key}                                               → id captured "b1Id", clientUuid captured "b1UuidBefore"

  PUT /v3/vendors/{vendorId} with prefixes "uri://s19-old.org,uri://s19-new.org"  → 204

  -- persisted identity, per client
  GET /v3/apiClients/{a1Key} → clientUuid equals "a1UuidBefore";  same for a2, b1
  -- live provider claim, per client
  token for "a1", "a2", "b1" with scope "TestClaim01"                      → 200 each, each token carries "uri://s19-new.org"
  -- ApiClient update, per client (addresses stored UUID)
  PUT /v3/apiClients/{a1Id} rename → 204;  PUT {a2Id} rename → 204;  PUT {b1Id} rename → 204
  -- credential reset, per client (addresses stored UUID), each followed by a token with the new secret
  PUT /v3/apiClients/{a1Id}/reset-credential → 200, token → 200;  same for a2, b1
  -- Application update, both affected applications (addresses each application's first client by stored UUID)
  PUT /v3/applications/{applicationA} rename → 204, token "a1" → 200
  PUT /v3/applications/{applicationB} rename → 204, token "b1" → 200
  -- ApiClient delete (addresses stored UUID)
  DELETE /v3/apiClients/{a2Id} → 204;  GET {a2Key} → 404
  -- Application delete, both affected applications (deletes every remaining client by stored UUID)
  DELETE /v3/applications/{applicationB} → 204
  DELETE /v3/applications/{applicationA} → 204;  GET {a1Key} → 404
```

Three generic steps are added: `the response body property {string} is captured as {string}`, `the response body property {string} equals the value captured as {string}`, and `the token carries {string} in the namespacePrefixes claim` for a captured-slot token (the existing namespace step only reads the last application's credentials). Under Keycloak each post-update operation addresses the provider by the stored UUID and fails with 500 against the current code at the first ApiClient PUT; under the current recreate the token would also have lost `educationOrganizationIds`. The scenario runs on both providers and, through `@MssqlRepresentative`, on both databases. The existing `Applications.feature` scenario 18 stays as is.

The general "every affected client, whatever the count" guarantee is proven by the unit and integration fixtures (§9.1, §9.3), which iterate arbitrary client sets; the E2E scenario is real-provider coverage of every client it creates.

## 10. Phased implementation plan

Each step is one commit. After each commit: SHA, files, behavior, tests run with results, residual risks, then **stop for approval**. `dotnet csharpier format` runs on the touched files before every commit (the whole-tree check has known unrelated drift; scope it to `src/config`).

### Phase 0 — Approval and baseline

**Step 0.1 — Commit the approved spec.** Purpose: put the design record in the tree the way DMS-1218 did. Scope: this file plus the `README.md` link. Non-goals: code. Tests: none; record the baseline results of `VendorModuleTests`, `KeycloakClientRepositoryTests`, `OpenIddictClientRepositoryTests`, and both backends' `VendorTests`. Checkpoint: architect approval recorded in §1.

### Phase 1 — Provider layer

**Step 1.1 — Keycloak namespace update in place (D-1).**
* Scope: `KeycloakClientRepository.UpdateClientNamespaceClaimAsync` only; add a private `UpsertNamespacePrefixesClaim(List<ClientProtocolMapper>, string)` beside the existing claim helpers.
* Non-goals: `UpdateClientAsync`, scope convergence, role handling.
* Contract: §4 D-1. Failure: phase-aware classification; no partial state is possible because there is a single mutating call.
* Tests: §9.2 Keycloak fixtures; run the whole `Backend.Tests.Unit` project. Evidence: the identity-preserved fixture fails when run against the previous implementation (record the failing assertion).
* Risks: Keycloak.Net `Client.ProtocolMappers` null on a client with none → materialize with `?? []` as `UpdateClientAsync` does. A representation with duplicate `namespacePrefixes` mappers → the first keeps its identity and configuration and takes the new value, the rest are removed.
* Checkpoint: no `DeleteClientAsync`/`CreateClientAndRetrieveClientIdAsync` reachable from the method; all fixtures green; CSharpier clean.

**Step 1.2 — OpenIddict regression fixtures.** Scope: tests only. Checkpoint: fixtures pin the same-UUID return and the merged JSON.

### Phase 2 — Repository layer

**Step 2.1 — `GetVendorUpdateState` contract and PostgreSQL implementation.** Scope: `IVendorRepository.cs`, PG `VendorRepository.cs`, PG integration fixtures (§9.3 repository). Failure: any exception → rollback, `FailureUnknown`. Risks: Dapper multi-mapping of nullable scalars; `FOR UPDATE OF v` with the tenant join — the join is a predicate, not a joined table, so `OF v` is valid. Checkpoint: fixtures green including the two-connection blocking test.

**Step 2.2 — SQL Server implementation.** Scope: MSSQL `VendorRepository.cs` and the mirrored fixtures. Risks: `UPDLOCK, HOLDLOCK` semantics match the existing `GetApplicationUpdateState`. Checkpoint: MSSQL lane green locally with the connection string set (state the skipped count is zero for the new fixtures).

### Phase 3 — Workflow (`VendorModule.Update`)

**Step 3.1 — Resolve before mutate, persist every UUID, commit last.**
* Scope: P0, P1, P4 (without rollback), P5 success/NotExists(404 without rollback)/Unknown(500) — a fail-fast version. Zero-client path. Logger category fix. Existing test fakes updated.
* Non-goals: locks, compensation, resolution.
* Contract: every `Success(u)` is followed by the guarded sync before the next client; any non-success stops and answers 502/500 with no database write.
* Tests: `…across_applications` (without the lock assertions), `…preserves_the_uuid`, `…rotates_the_uuid`, `…no_clients`, `…missing_vendor`, `…pre_read_fails`, `…invalid_body`, classification fixtures without rollback assertions. Evidence: `…rotates_the_uuid` fails on `main`.
* Risk: this intermediate commit knowingly lacks rollback; it is strictly better than `main` (no stranding, no database commit before provider success) and is superseded within the phase.
* Checkpoint: `AffectedClientUuids` no longer read by the module.

**Step 3.2 — Lock participation.**
* Scope: P2, P3, P6; `AcquireVendorLocksAsync` local helper mirroring `AcquireApiClientLocksAsync` (ascending distinct ids, bounded drift retry, dispose-on-throw), `LockFailureResult`, `DisposeLocksAsync` in the module.
* Contract: §5.1–5.2. Failure: §5.3 lock rows.
* Tests: lock-order, timeout, infra failure, drift once, drift persistent, reread throws, cancellation fixtures; extend the success fixture with the order and disposal assertions.
* Risks: the two existing `LockFailureResult` copies in Application/ApiClient modules are not consolidated (P3 follow-up, §12).
* Checkpoint: every fixture asserts all handles disposed.

**Step 3.3 — Compensation for provider and sync failures.**
* Scope: P4 rollback rules of §5.3 (rows 1–7), narrow recreated-client cleanup, aggregate outcome.
* Tests: all `Given_a_…_on_the_second_client`, `…failed_rollback…`, sync-outcome fixtures, unrecognized-result fixture.
* Risks: rollback of a client whose provider now reports `FailureNotFound` → logged as inconsistent (a client that vanished mid-request is not this workflow's to recreate — INV-64 reasoning).
* Checkpoint: no 204 reachable with a mutated-but-unrolled-back client.

**Step 3.4 — Repository outcome handling.**
* Scope: P5 vanished-vendor rollback → 404, D-4 resolution for unknown/thrown.
* Tests: vanished vendor (both), committed/uncommitted/thrown/unresolvable resolution fixtures.
* Checkpoint: §5.3 table fully pinned; `VendorModuleTests` complete.

### Phase 4 — Backend workflow integration

**Step 4.1 — PostgreSQL `WorkflowConcurrencyTests` vendor fixtures (§9.3 workflow).** Risks: barrier timing; reuse the existing `WaitForAsync`/gate patterns. Checkpoint: each fixture demonstrated to fail with a no-op lock manager or reversed acquisition order (record which).

**Step 4.2 — SQL Server mirror.** Checkpoint: same fixtures green with `ConnectionStrings__MssqlAdmin` set.

### Phase 5 — E2E

**Step 5.1 — `Vendors.feature` scenario 19 and the three steps (§9.5).** Run locally with `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak` and again with `self-contained`; run the MSSQL lane with `-EnvironmentFile ./.env.config.mssql.e2e -E2ETestFilter "TestCategory=MssqlRepresentative"` for at least Keycloak. Evidence: the scenario fails against `main` under Keycloak at the first post-update ApiClient PUT (500). Checkpoint: green on both providers.

### Phase 6 — Documentation and cleanup

**Step 6.1 — Design records.** This file gains an "as implemented" section with commit SHAs and the verification table; DMS-1218 §9.1 gets dated corrections to the two sentences that describe Vendor as non-participating and namespace update as delete-and-recreate (the doc's `[Corrected …]` convention). No other DMS-1218 text changes.

**Step 6.2 — (if Q-2 approved) remove `VendorUpdateResult.Success.AffectedClientUuids` and the post-commit UUID query from both `UpdateVendor` implementations**, updating the integration `UpdateTests` fixture and any fake constructions. Small, mechanical, last.

## 11. Acceptance-evidence traceability

| Acceptance evidence [JIRA] | Where proven |
|---|---|
| Every UUID returned by a successful namespace-claim update is persisted to the correct ApiClient row (Keycloak) | General proof for any client set: §9.1 rotating/stable fixtures (per-client `SyncApiClientUuid` with the right expected UUID); §9.2 Keycloak identity fixture. Real-provider evidence: §9.5 per-client UUID capture/compare for the three clients created |
| Update, delete, credential reset, and Application update/delete work for every affected ApiClient afterwards | General proof: §9.1 (every client in S1 targeted and synced; no 204 with an unsynced client). Real-provider evidence: §9.5 scenario 19 exercises ApiClient update and credential reset on **each** of its three clients, ApiClient delete on one, and Application update and delete on **both** affected applications, on both providers and both databases |
| Locks acquired deterministically and held through UUID persistence or compensation | §9.1 order/disposal fixtures; §9.3 compensation-hold and inverse-move fixtures on PG and MSSQL |
| No success with mixed state; every failure branch has a tested consistent outcome or a sanitized logged 500 | §5.3 table, each row pinned in §9.1 |
| PostgreSQL and SQL Server integration coverage of lock ordering, guarded persistence, partial failure | §9.3 both backends |
| OpenIddict in-place behavior regression | §9.2 OpenIddict fixtures; §9.5 self-contained lane |
| Keycloak E2E against the real provider | §9.5 Keycloak lane |

## 12. Deferred, out of scope, and follow-ups

| Item | Disposition |
|---|---|
| Pre-existing stranded `ApiClient.ClientUuid` rows | Not repairable here; operator deletes/recreates the ApiClient. Recorded in the as-implemented section. |
| `InsertApplication`/`InsertApiClient` read vendor prefixes without locks | Accepted exclusion, same class as S-2. |
| `ApplicationModule.Update` changing `VendorId` does not re-derive the namespace claim | Pre-existing gap; candidate ticket. |
| `DeleteVendor` cascades applications and ApiClient rows without deleting provider clients | Pre-existing gap; candidate ticket. |
| Three private copies of `LockFailureResult`/`DisposeLocksAsync` across modules | P3 consolidation after this ticket. |
| `INV-49` (`ApplicationApiClientsResult` has no not-found case) | Unchanged. |

## 13. Risks and mitigations

| Risk | Mitigation |
|---|---|
| A vendor with many applications holds many session locks for the duration of N provider calls | ~~Each lock is one dedicated connection held until the workflow releases it, so `AcquireTimeout` does not bound the cost: the fan-out is explicitly capped instead (25 applications; deterministic 409).~~ **[Superseded 2026-09-18, R4]** The cap never addressed the actual hazard: lock connections were drawn from the repositories' pool, so concurrent workflows could exhaust it well below any per-request cap while each held a lock and waited on a repository call (hold-and-wait). R4 removes the connection growth instead of bounding the fan-out: the vendor update takes its whole application set as **one lock set on one database session** (`IApplicationLockManager.AcquireAllAsync`, deduplicated and ascending, on the same lock keys/resources), and both lock managers draw their sessions from a **dedicated, bounded pool** (`ApplicationLockConnectionPool`: its own application name, `Max Pool Size` 20, `Min Pool Size` 0, built by the manager from the configured database connection so it is the same pool for every acquisition). Lock holders and lock waiters can exhaust only that pool, never the repository pool the same workflow, or any other request, still needs. The cap and its response helper are removed; vendors above 25 applications, including company/contact edits, follow the normal workflow. Ordering is ascending so no cycle with Application/ApiClient workflows is possible. |
| Deadlock with a two-lock ApiClient move | Impossible under a total order; pinned by the inverse-move fixture on both backends. |
| Keycloak representation from `GetClientAsync` carries fields the `PUT` interprets destructively | Same representation round-trip already used by `UpdateClientAsync` and probed in V-32. |
| A non-participating writer changes a row mid-request | Detected by the sync guard (`FailureStaleState`), never overwritten, answered 500 with rollback of this request's own mutations. |
| Drift retry loops under a hot vendor | Bounded to 3 attempts, then 409. |
| Test flakiness in barrier-based integration fixtures | Reuse the proven `TaskCompletionSource` gates and teardown drains of the existing fixtures. |

## 14. Questions raised for the architect in R1 (resolved — see §16)

* **Q-1 (D-3).** "Always re-sync" versus "skip provider calls when the normalized prefix set is unchanged". Recommended always re-sync.
* **Q-2 (§8, Step 6.2).** Remove `VendorUpdateResult.Success.AffectedClientUuids` as the final step, or leave the contract untouched. Recommended remove.
* **Q-3.** Repository state-read fixtures in the existing per-backend `VendorTests.cs` or a new `VendorConsistencyOperationTests.cs`. Recommended new file.
* **Q-4.** Whether the intermediate Step 3.1 (fail-fast without rollback) may be its own commit. Recommended separate commits.

## 15. Self-challenge

* *Does in-place update dodge the ticket?* No. The ticket's required shape (resolve, lock ascending, persist every returned UUID with guards, compensate) is implemented in full and pinned with a rotating fake provider; in-place additionally removes the recreate's collateral losses, which the ticket's acceptance evidence ("credential reset works", "Application update continues to address valid provider clients") would otherwise still be undermined on Keycloak because the recreated client has no service-account role mapping and has lost its education-organization and data-store claims.
* *Is the guarded sync meaningful when the UUID never changes?* Yes: `AlreadyApplied` is the proof that the row still references the client this request mutated; `FailureStaleState`/`FailureNotExists` are the detection of a non-participating writer, which is exactly what allows a sanitized 500 instead of a false 204.
* *Could a 204 be returned with the vendor row committed but a client claim stale?* Only for a client inserted by a non-participating insert during the request (D-2 exclusion) or a client whose row a non-participating writer re-pointed after our sync — both outside the DMS-1218 guarantee and recorded.
* *Does anything here weaken Application/ApiClient?* No file of theirs changes; they gain a participating peer that respects their lock order.

## 16. Decision log (architect, 2026-09-17)

| # | Decision | Effect on the plan |
|---|---|---|
| A-1 | Spec **approved** subject to two amendments, both applied in R2: (a) §5.3/§5.5 state the current client *k*'s provider failure as an ambiguous outcome with its own rollback attempt, a `may be inconsistent` log when unprovable, and the original sanitized classification retained; (b) §9.5/§11 no longer overclaim — the E2E scenario now mutates every client it creates and updates/deletes both affected applications, and the general "every client" proof is attributed to the unit and integration fixtures | New fixtures `Given_a_provider_failure_whose_own_rollback_cannot_be_proven` and `Given_a_failed_rollback_of_a_known_mutated_client`; E2E scenario expanded |
| Q-1 | **Always re-sync.** Retry convergence outweighs skipping unchanged prefixes | D-3 stands |
| Q-2 | **Remove** `VendorUpdateResult.Success.AffectedClientUuids` and the post-commit UUID query as the final cleanup step | Step 6.2 is in scope |
| Q-3 | **New per-backend `VendorConsistencyOperationTests.cs`**, mirroring the DMS-1218 consistency-test layout | §7 and Steps 2.1/2.2 use the new files |
| Q-4 | Step 3.1 **may land as its own local commit**, explicitly an intermediate checkpoint that is neither push-ready nor merge-ready; approval stop still required after it | Step 3.1 carries the caveat in its commit message |

**Guardrails for the implementation (architect):** preserve the DMS-1218 lock, error-response, and compensation invariants; do not broaden into insert workflows, Application vendor moves, `DeleteVendor` provider cleanup, schema/DDL, or `RelationalMappingVersion`; keep every commit focused and local-only until the final gates pass; before push or completion run the targeted unit, PostgreSQL and SQL Server integration, formatting, and Keycloak plus self-contained E2E gates listed here, reporting MSSQL skipped counts explicitly.

---

## 17. As implemented

Every commit below is on branch `DMS-1356`, in plan order. Each landed behind its own review
gate. The identifiers are the ones the branch carries after it was rebased onto `c0da1a75f`.

| Step | Commit | What it changed |
|---|---|---|
| 0.1 | `fe20d3d2d` | This document (R2, approved) and its `README.md` link |
| 1.1 | `92b189a6d` | Keycloak `UpdateClientNamespaceClaimAsync` becomes an identity-preserving in-place update (D-1) |
| 1.2 | `032607869` | OpenIddict regression fixtures for the same operation; no production change |
| 2.1 | `96599ce79` | `IVendorRepository.GetVendorUpdateState` with its records, both relational implementations, PostgreSQL fixtures, and the three R2 spec corrections |
| 2.2 | `529375db9` | The mirrored SQL Server fixtures |
| 3.1 | `2c08cbba1` | Resolve before mutate, persist every reported UUID, commit the vendor row last (no locks, no compensation) |
| 3.2 | `7d45ee95f` | Lock participation: ascending acquisition, authoritative reread, bounded drift retry, release on every path |
| 3.3 | `c1db1e3a1` | Compensation, including the ambiguous current-client rule |
| 3.3a | `8b671de92` | Review finding: a replacement client that cannot be removed fails the restoration |
| 3.4 | `d5cab6a33` | Ambiguous `UpdateVendor` outcome resolved by the authoritative reread |
| 4.1 | `929c2299b` | PostgreSQL workflow-concurrency fixtures against the real lock manager |
| 4.2 | `c864c69b4` | The mirrored SQL Server concurrency fixtures |
| 5.1 | `d0bdc1f51` | E2E scenario and three step definitions, proven against real Keycloak |
| 6.1 | `802b929a7` | This section, §18 and §19, and the two dated corrections to DMS-1218 §9.1 |
| 6.2 | `2aca69dcc` | `VendorUpdateResult.Success.AffectedClientUuids` and the unlocked post-commit client query removed from both repositories (Q-2) |
| R1 | `9263922fb` | First review round: the 25-application fan-out cap (retriable 409), `RollbackSafelyAsync` in both `UpdateVendor` catch blocks, and the provider-target / stored-guard UUID split in `RestoreClientAsync` |
| R2 | `a35a31ce4` | Second review round: the cap refusal became its own deterministic 409 body; the replacement-client delete result flowed into compensation through an (inert, see R4) `currentClientClean` flag |
| 7.2 (R5) | 2026-09-18, fourth review round | Fourth review round (§21): the ApiClient parent-move acquisition became one `AcquireAllAsync` set call per attempt, and both lock managers gave each acquisition attempt one shared contention budget instead of one `AcquireTimeout` window per id |
| 7.1 (R4) | 2026-09-18, third review round | Third review round (§20): `IApplicationLockManager.AcquireAllAsync` — one lock set per vendor workflow on one database session, deduplicated and ascending, on the existing lock keys/resources — implemented by both lock managers, whose sessions now come from the dedicated bounded pool `ApplicationLockConnectionPool`; the fan-out cap and `ApplicationLockCapConflict` removed; the inert `currentClientClean` parameter removed without changing any failure classification |

**Deviations from the plan, and why.**

* **The SQL Server `GetVendorUpdateState` implementation landed in Step 2.1, not 2.2.** Adding the interface member breaks that project's compilation, so a commit without it would not build. Its fixtures stayed in Step 2.2 as planned. Approved in review.
* **Step 3.1 landed as its own commit** carrying an explicit "intermediate checkpoint, not push-ready" caveat, per Q-4.
* **Nothing else departed from the approved phases.** No insert workflow, Application vendor move, `DeleteVendor` provider cleanup, schema, or `RelationalMappingVersion` change was made.

**No implementation steps remain.** Every phase of the approved plan has landed, including the Q-2 cleanup in Step 6.2, and the R4 correction of the review-round cap (Step 7.1).

**How Step 7.1 preserves the approved contract.** The single-application `AcquireAsync` is unchanged for the Application and ApiClient workflows and is now the one-element case of the set operation. Per key, the lock set keeps the deadline re-check and cancellation propagation of the single operation. ~~Per key it also keeps the per-lock `AcquireTimeout` window.~~ **[Corrected 2026-09-18, R5]** Each *acquisition attempt*, not each key, now gets one `AcquireTimeout` contention budget, so a set of N ids waits one timeout rather than N (§21); the one-element case — every `AcquireAsync` caller — is unchanged, because one key spending one budget is exactly the old per-lock window. A timeout, failure, or cancellation part way through the set releases the keys the session already holds through the same release path a handle uses (release each, evict the session from the lock pool if a release fails, then dispose), so a partial acquisition never rides a pooled connection. The vendor workflow keeps the pre-read, the authoritative reread under the set, the bounded drift retry (a new set per attempt), the lock hold through UUID persistence, vendor persistence, and compensation, and D-3 always-re-sync; the only change in `VendorModule` is that one `AcquireAllAsync` call replaces the per-application loop and the cap check.

## 18. Verification table (executable)

| ID | Lane | Command shape | Result |
|---|---|---|---|
| V-1 | CMS backend unit | `dotnet test backend/…Backend.Tests.Unit` | 717 passed, 0 failed, 0 skipped |
| V-2 | CMS frontend unit | `dotnet test frontend/…AspNetCore.Tests.Unit` | 1334 passed, 0 failed, 0 skipped |
| V-3 | PostgreSQL integration | `dotnet test backend/…Postgresql.Tests.Integration` | 454 passed, 0 failed, 0 skipped |
| V-4 | SQL Server integration | same with `ConnectionStrings__MssqlAdmin` exported | 468 passed, 0 failed, **0 skipped** |
| V-5 | E2E, Keycloak, whole suite | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak` | 217 passed, 0 failed, 7 skipped (the self-contained-only scenarios) |
| V-6 | E2E, self-contained, whole suite | same with `-IdentityProvider self-contained` | 220 passed, 0 failed, 4 skipped (the Keycloak-only scenarios) |
| V-7 | E2E, SQL Server | same with `-EnvironmentFile './.env.config.mssql.e2e' -E2ETestFilter 'TestCategory=MssqlRepresentative'` | 23 passed, 0 failed, 0 skipped |
| V-8 | Formatting | `dotnet csharpier check .` from `src/config` | 439 files, clean |
| V-9 | Solution build | `dotnet build EdFi.DmsConfigurationService.sln --no-incremental` | succeeded, no warnings |

The table above is the full gate run before the push, against the branch's original base. The
branch was then rebased onto `c0da1a75f`, whose only change under `src/config` is one line of
prose in a doc comment. The rebase was confirmed with a lighter gate — `git diff --check`, V-8,
V-9, V-1 and V-2 — all of which passed unchanged; the heavier lanes were not rerun because no
CMS behavior differed in the rebased result. **It is historical evidence** for the R3 revision;
the R4 table below is what was run on the final revision.

**R4 (Step 7.1) verification, run on the final revision, 2026-09-18.** The new fixtures are:
`VendorModuleTests` — `Given_a_vendor_update_spanning_many_applications` (40 applications, one
lock set, every client updated and synced, vendor committed) and
`Given_a_company_contact_edit_of_a_vendor_spanning_many_applications` (unchanged prefixes, every
client still re-synchronized, company/contact committed), plus the lock-failure fixtures
restated for one lock set per attempt and the one-set-per-attempt assertions on the drift fixtures;
`ApplicationLockManagerTests` on both backends — `Given_a_lock_set_acquired_on_one_session`,
`Given_a_lock_set_whose_higher_lock_is_held_elsewhere` (partial-acquisition cleanup and ascending
order through the release seam), `Given_a_lock_set_contending_with_a_single_application_lock`,
`Given_a_cancelled_lock_set_acquisition_while_contending`, `Given_the_lock_connection_string`, and
`Given_the_lock_pool_saturated_by_lock_sessions` (repository connection still opens with every lock
session held; every lock held on a session of the dedicated pool; the 21st session refused);
`WorkflowConcurrencyTests` on both backends — `Given_two_vendor_updates_over_the_same_applications`
(deterministic coordination through the paused provider call and the acquisition observer).

| ID | Lane | Command shape | Result |
|---|---|---|---|
| R4-1 | CMS frontend unit (whole project) | `dotnet test frontend/…AspNetCore.Tests.Unit` | 1351 passed, 0 failed, 0 skipped |
| R4-2 | PostgreSQL integration (whole project, incl. lock manager, vendor consistency, workflow concurrency) | `dotnet test backend/…Postgresql.Tests.Integration` | 481 passed, 0 failed, 0 skipped |
| R4-3 | SQL Server integration (whole project) | same with `ConnectionStrings__MssqlAdmin` exported | 495 passed, 0 failed, **0 skipped** |
| R4-4 | E2E, Keycloak, scenario 19 only, image rebuilt | teardown → `Build -Configuration Release` → `E2ETest -Configuration Release -IdentityProvider keycloak -E2ETestFilter 'Name~_19VendorNamespace_PrefixUpdateKeepsEveryAffectedClientAddressable'` | 1 passed, 0 failed, 0 skipped (image rebuilt `--no-cache` from this revision; container env `AppSettings__IdentityProvider=keycloak`, `AppSettings__Datastore=postgresql`) |
| R4-5 | E2E, self-contained (OpenIddict), scenario 19 only | same with `-IdentityProvider self-contained` | 1 passed, 0 failed, 0 skipped (same image, stack restarted with `-SkipDockerBuild`; container env `self-contained` / `postgresql`) |
| R4-6 | E2E, SQL Server × Keycloak, scenario 19 only | same with `-EnvironmentFile './.env.config.mssql.e2e'` | 1 passed, 0 failed, 0 skipped (same image; container env `keycloak` / `mssql`) |
| R4-7 | Formatting | `dotnet csharpier check .` from `src/config` | clean (see §19 for the whole-tree note) |
| R4-8 | Solution build | `dotnet build EdFi.DmsConfigurationService.sln` | succeeded, 0 warnings |

**R4 mutation sensitivity.** Each mutation was applied to the final revision, the named fixtures run, and the source restored (verified by an unchanged diff and a clean CSharpier check). PostgreSQL lock manager acquires the set in the requested order instead of ascending — 2 assertions fail (the higher-first fixture and the one-session fixture). Lock connection string returned unchanged (sessions drawn from the repository pool) — 4 assertions fail (repository connection cannot open with every lock session held; no lock-holding session carries the dedicated pool name; the pool-name and pool-bound assertions of the connection-string fixture). Timeout branch leaks the session instead of releasing the keys already held — 2 assertions fail (the lower lock is not released and the release seam records nothing). `VendorModule` locks only the first application of the set — 3 assertions fail across the across-applications, many-applications, and company/contact fixtures. The R3 mutation table above still applies to every branch this revision did not touch.

The whole E2E suite was deliberately not rerun for R4: the change is confined to lock acquisition
inside the vendor workflow and the lock managers, and scenario 19 is the only E2E scenario that
exercises them across a real provider. The whole-suite E2E rows V-5..V-7 are R3 evidence.

**Mutation sensitivity.** Every guarded branch is pinned by a fixture demonstrated to fail under a targeted mutation, the DMS-1218 INV-62 bar.

| Mutation | Fixtures that fail |
|---|---|
| Keycloak namespace update restored to delete-and-recreate | 29 of the 38 repository fixtures |
| OpenIddict namespace update reports a rotated UUID | the unchanged-identifier fixture |
| Vendor row lock removed from the state read | the two-connection blocking fixture (PostgreSQL; see the SQL Server limitation below) |
| Client ordering reduced to the row id | both ordering fixtures, on both backends |
| Tenant predicate removed | the foreign-tenant fixture, on both backends |
| The workflow discards the reported UUID (the original defect) | 19 of 60 module fixtures |
| Lock acquisition reversed | 3 module fixtures; and on both backends the inverse-move concurrency fixture deadlocks until both requests time out |
| Drift check neutered | 5 module fixtures |
| Locks not released | 3 module fixtures |
| Pre-lock snapshot used instead of the reread | the authoritative-reread fixture |
| Rollback of already-changed clients skipped | 12 module fixtures |
| A known failed rollback no longer downgrades the response | 2 module fixtures |
| The ambiguous client's own restoration skipped | 3 module fixtures |
| Replacement-deletion guard inverted | 3 module fixtures |
| Replacement deletion result discarded (the review finding) | 3 module fixtures |
| Prefix comparison neutered in the command match | the partial-state fixture |
| Compensation skipped on a proven non-commit | 3 module fixtures |
| Compensation performed on a partial match | the no-guessing fixture |
| Lock manager bypassed in the vendor workflow | 6 concurrency assertions, on both backends |
| Container image rebuilt from the pre-fix code | the E2E scenario, at the first ApiClient update after the vendor change, with the sanitized 500 of a stored client that no longer exists |

## 19. Limitations found during implementation

These are recorded because each one bounds what the evidence above actually proves.

* **The SQL Server row-lock hint is not discriminated by its fixture.** Under read committed, SQL Server already blocks a plain select on an uncommitted update, so the blocking fixture passes with or without `UPDLOCK, HOLDLOCK`. The hint is kept because it matches `GetApplicationUpdateState` and holds the row for the whole snapshot rather than one statement. The PostgreSQL fixture does discriminate, because its multi-version reads do not block without the explicit row lock.
* **The E2E stored-identifier assertions prove stability, not liveness.** They pass against the pre-fix code too, because the rows keep their stale identifiers unchanged. What fails without the fix is the operations that follow, which address the provider by that identifier.
* **`-SkipDockerBuild` runs the previously built image.** An E2E run after a code change without rebuilding the image tests the old code and can produce false evidence in either direction. The pre-fix comparison in the mutation table was rerun with the image rebuilt for exactly this reason, after a first attempt silently tested the fixed code.
* **Pre-existing stranded rows are not repaired.** A row already pointing at a client deleted by an earlier vendor update cannot be recovered by CMS, which has no `clientId`-based provider re-resolution. Operators delete and recreate such ApiClients.
* **Non-participating writers remain excluded**, as recorded in §4 D-2: ApiClient and Application inserts read the vendor's stored prefixes without taking the aggregate locks, so a client created during a vendor update receives the stored prefixes and is not repaired by that request. **Deferred, not fixed** (third review, §20).
* **The lock pool bound is a constant, not a setting.** `ApplicationLockConnectionPool.MaxPoolSize` is 20 sessions per CMS process. A burst of more than 20 concurrent lock-holding or lock-waiting workflows makes the 21st acquisition wait for the driver's connect timeout and then fail as `FailureUnknown`, which the modules answer with the sanitized 500 they already use for lock-infrastructure failures; the repository pool is unaffected. The Application workflows still take one single-application lock per acquisition, unchanged. ~~A parent-changing ApiClient move takes two sessions, unchanged and out of this ticket's scope.~~ **[Corrected 2026-09-18, R5]** It took two, and that was this ticket's problem to finish: the move held its first lock session while waiting for a second from the pool this ticket introduced, so 20 concurrent moves over disjoint application pairs could starve it. The move now takes both applications as one set on one session (§21).
* **Ordering is proven through the release seam, not observed live.** The integration fixture that requests a set with the higher id first while the higher lock is held elsewhere proves the lower lock was taken first because the release seam records exactly that key on the timeout; a fixture watching the two acquisitions in flight would need timing assumptions.

## 20. Third review (2026-09-18) — findings and dispositions

The third review was checked against `a35a31ce4`, Jira, the repository, the existing tests, and upstream Keycloak 26.1 source before any code changed. Each finding is recorded here as fixed, rejected with evidence, or deferred, so that a later round does not reopen it without a concrete counterexample.

| Finding | Disposition |
|---|---|
| `currentClientClean` is inert | **Fixed (simplification).** Its only caller already passed `FailureResults.Unknown` as the original failure, so both branches produced the same 500. The parameter is removed; the replacement-client delete attempt, its failure logging, and the rollback of previously changed clients are unchanged. No failure classification changed. |
| The 25-application cap blocks company/contact edits | **Fixed, together with the connection finding.** The cap and `ApplicationLockCapConflict` are removed; vendors above 25 affected applications, including unchanged-prefix company/contact edits, run the normal workflow. No unchanged-prefix shortcut was introduced (D-3 stands): an unchanged request may be recovering provider drift after a failed compensation, and comparing the request with stored prefixes cannot establish that provider state is already correct. |
| Dedicated lock connections can exhaust the repository pool | **Fixed (design defect).** Each application lock retained a connection from the pool the subsequent repository operations needed, so concurrent workflows could exhaust it below any cap. The claim that a workflow holds "two more connections" simultaneously was not established (the inspected repository operations acquire connections sequentially), but the hold-and-wait starvation risk was. Correction: one lock set per vendor workflow on one session (removes the fan-out), and a dedicated bounded lock pool (removes the remaining hold-and-wait on the repository pool). Not done: a pool per request, disabled pooling, or raised pool limits. |
| A stranded `ClientUuid` blocks vendor updates | **Rejected as a repair requirement; behavior preserved.** The workflow logs the affected vendor and client, compensates previous mutations, and returns a sanitized 500. Jira permits this failure outcome and §3.6/§12 exclude repairing pre-existing stranded rows. The missing client is neither skipped, recreated, nor reported as success. |
| Keycloak `PUT` does not delete omitted mappers | **Rejected; contradicted by upstream source.** Keycloak 26.1 `RepresentationToModel.updateClientProtocolMappers` removes existing mappers omitted from the representation, and `ClientResource.update` invokes it; the local stack pins Keycloak 26.1 (V-32 probed the same behavior). No mapper handling changed. |
| Rollback UUID inequality guard (`rollbackSuccess.ClientUuid != providerClientUuid`) | **Kept.** Provider UUID replacement is covered by Jira and exercised by the rotating-provider fixtures; the guard is not dead because the current providers preserve UUIDs. |
| Concurrent Application/ApiClient insertion keeps old prefixes | **Deferred (documented exclusion, D-2 / §12 / §19).** Not expanded into creation-workflow serialization. |

## 21. Fourth review (2026-09-18) — findings and dispositions

Both findings were raised against `aece7f055` by code inspection, and both are consequences of
what R4 introduced rather than new design questions. The architecture R4 settled is kept: one
lock set per workflow on one session, a dedicated bounded lock pool, no cap, no new setting, no
raised pool limit.

| Finding | Disposition |
|---|---|
| The ApiClient parent move still acquires two locks in two calls | **Fixed (integration defect of the R4 pool).** `ApiClientModule.AcquireApiClientLocksAsync` looped over the source and target application ids calling `AcquireAsync` for each and retaining both handles. With the dedicated pool bounded at 20 sessions, 20 concurrent moves over disjoint application pairs can each hold their first lock session and wait for a second from the exhausted pool. This is connection-resource hold-and-wait, not a database deadlock: the locks themselves are taken in ascending id order and never cycle, and the driver's connect timeout eventually breaks the wait, so the symptom is pool starvation answered as failed requests (`FailureUnknown`, sanitized 500), not an indefinite block. Ascending order cannot prevent it, because the contended resource is the pool rather than the locks. Correction: one `AcquireAllAsync(applicationIdsToLock, …)` call per attempt, so a move holds one session. The helper keeps its `List<IAsyncDisposable>` return shape, now carrying the single set handle, and keeps the pre-read, the authoritative reread under the locks, the bounded parent-drift retry with disposal before each retry, cancellation propagation, the existing 409/500 mapping, and lock retention through persistence and compensation. Genuinely single-application callers — the three `ApplicationModule` acquisitions — are untouched; `DeleteApiClient` and `ResetCredential` reach the same helper with a one-element set. §19's "out of this ticket's scope" note on the two-session move is corrected above. |
| Each id of a lock set gets its own `AcquireTimeout` window | **Fixed (small bounded correction, not a required one).** PostgreSQL started a new stopwatch and read the full `AcquireTimeout` inside `TryAcquireWithinTimeoutAsync` for every id; SQL Server passed the full timeout to every `sp_getapplock` call through `@LockTimeout`. Under staggered contention, N locks can therefore consume about N timeout windows while the locks taken earlier stay held. That behavior is confirmed. Its status is worth stating precisely: it is what §17 of this document explicitly described, and Jira nowhere requires a set-wide timeout — the acceptance criteria speak to ordering, retention, and failure outcomes, all of which held. The correction is taken because the set operation is new in R4 and its contention cost should be bounded the same way on both backends, not because an acceptance criterion was unmet. Correction: one monotonic budget per acquisition attempt, started after the session opens and shared by every id. PostgreSQL checks the remaining budget before each attempt and each retry and clips its poll delay to it; SQL Server passes only the remaining milliseconds to `@LockTimeout` and answers an exhausted budget as `FailureTimeout` directly, because a negative `@LockTimeout` means "wait indefinitely" to `sp_getapplock`. Neither backend opens a fresh window for a later id. Partial-acquisition cleanup, release-failure eviction, cancellation semantics, and the existing failure classification are unchanged. |

**What the budget does and does not bound.** It is a contention budget, not a request deadline,
and this ticket did not turn it into one. It starts once the lock session is open, so
establishing the connection — including the driver connect timeout that answers an exhausted
pool — is outside it, and it stops being charged once the whole set is held, so releasing the
locks and everything the workflow does while holding them is outside it too. `AcquireTimeout`
therefore bounds how long one acquisition attempt waits on other lock holders, and nothing else.
`ApplicationLockOptions.AcquireTimeout` carries this statement in its doc comment. The
single-application case is unaffected in every respect: one id spending one budget is the
previous per-lock window.

**Scope held.** No cap was restored, no pool limit raised, no configuration added, no provider,
schema, or `RelationalMappingVersion` change made, and no helper consolidation attempted — the
three private copies of `LockFailureResult`/`DisposeLocksAsync` remain the P3 follow-up recorded
in §12. The deferred items of §19 and §20 are untouched and were not reopened.

### 21.1 R5 verification, run on this revision (2026-09-18)

New and adapted fixtures. `ApiClientModuleTests` — `RecordingLockManager` now records one entry
per acquisition holding that acquisition's ids (`Acquisitions`), because flattening them made one
set of two indistinguishable from two single acquisitions, which is why the previous
higher/lower move assertions passed against the defective loop;
`Given_an_api_client_update_moving_to_a_higher_application_id` and
`..._to_a_lower_application_id` now assert exactly one set acquisition holding both ids in
ascending order and exactly one disposed handle; `Given_an_api_client_update_whose_second_lock_times_out`
and `..._whose_second_lock_acquisition_is_cancelled` — whose "second acquisition" mocks no longer
describe the contract — became `Given_an_api_client_update_whose_move_lock_set_times_out` and
`..._whose_move_lock_set_acquisition_is_cancelled`, asserting the single set request over both
ids, the unchanged 409/500 answers, and that no single-application acquisition happens; the drift
fixture asserts three attempts of one one-element set each. `ApplicationLockManagerTests` on both
backends — `Given_a_lock_set_whose_earlier_lock_consumes_most_of_the_budget`: the lower lock is
held elsewhere for 1.5 s of a 2 s budget and then released, so the set takes it late and contends
for the higher lock, held throughout, on the remainder. A fixture whose second lock is merely held
from the outset cannot distinguish the defect, which is why this one staggers the contention.

| ID | Lane | Command shape | Result |
|---|---|---|---|
| R5-1 | CMS frontend unit (whole project) | `dotnet test frontend/…AspNetCore.Tests.Unit` | 1353 passed, 0 failed, 0 skipped |
| R5-2 | CMS backend unit (whole project) | `dotnet test backend/…Backend.Tests.Unit` | 717 passed, 0 failed, 0 skipped |
| R5-3 | PostgreSQL integration (whole project, incl. lock manager, vendor consistency, workflow concurrency, inverse move) | `dotnet test backend/…Postgresql.Tests.Integration` | 486 passed, 0 failed, 0 skipped |
| R5-4 | SQL Server integration (whole project) | same with `ConnectionStrings__MssqlAdmin` exported | 500 passed, 0 failed, **0 skipped** |
| R5-5 | E2E, Keycloak × PostgreSQL, ApiClients and Vendors features (44 scenarios, incl. scenario 19), image rebuilt from this revision | `./build-config.ps1 Build -Configuration Release`, teardown, then `E2ETest -Configuration Release -IdentityProvider keycloak -E2ETestFilter 'FullyQualifiedName~ApiClientsEndpointsFeature|FullyQualifiedName~VendorsEndpointsFeature'` | 43 passed, 0 failed, 1 skipped of 44 (the skip is the self-contained-only `_21`) |
| R5-6 | E2E, self-contained (OpenIddict) × PostgreSQL, same features | same with `-IdentityProvider self-contained -SkipDockerBuild` (same image) | 44 passed, 0 failed, 0 skipped |
| R5-7 | E2E, Keycloak × SQL Server, representative subset | same with `-EnvironmentFile './.env.config.mssql.e2e' -E2ETestFilter 'TestCategory=MssqlRepresentative'` | 23 passed, 0 failed, 0 skipped |
| R5-8 | Formatting | `dotnet csharpier check .` from `src/config` | 439 files, clean (see §19 for the whole-tree note) |
| R5-9 | Solution build | `dotnet build EdFi.DmsConfigurationService.sln --no-incremental` | succeeded, 0 warnings |

The E2E lanes were scoped to the two features this revision can affect — the vendor workflow
and every ApiClient operation that goes through the changed acquisition helper — plus the
SQL Server representative subset. The remaining E2E scenarios are unchanged behavior and their
whole-suite evidence stays V-5/V-6 (R3 revision). In both PostgreSQL lanes the ApiClients
scenarios `_09` and `_15` print as skipped from their known undefined `Given` bindings and are
not counted in the skipped tally; that is pre-existing and unrelated to this change.

**A note on how the mutation runs were reverted.** The backups restore the original file
timestamps, so an incremental build after a revert can leave mutated assemblies in place and a
`--no-build` test run would then report the mutation's failures as if they were the revision's.
Every number in the table above was taken after `dotnet build … --no-incremental` on the
restored source, not from an incremental rebuild.

**R5 mutation sensitivity.** Each mutation was applied to this revision, the named fixtures run,
and the source restored from a byte-for-byte backup.

| Mutation restored | Assertions that fail |
|---|---|
| `ApiClientModule` loops `AcquireAsync` over the two ids again | 10 of the 12 assertions in the four move fixtures: both `It_acquires_one_lock_set_holding_both_applications_in_ascending_order`, both `It_releases_the_one_set_handle`, both `It_asks_for_both_applications_in_one_acquisition`, both `It_never_acquires_a_single_application_lock`, and the 409 and 500 answers of the timeout and cancellation fixtures. The two `It_returns_no_content` assertions still pass, correctly: a move succeeds either way, which is why the acquisition shape needs its own assertions |
| PostgreSQL starts a fresh stopwatch per key | `It_spends_one_budget_on_the_whole_set`: 3 s 689 ms against a 2 s 800 ms bound |
| SQL Server passes the full timeout to each `sp_getapplock` | `It_spends_one_budget_on_the_whole_set`: 3 s 508 ms against a 2 s 800 ms bound |

The R3 and R4 mutation tables still apply to every branch this revision did not touch.
