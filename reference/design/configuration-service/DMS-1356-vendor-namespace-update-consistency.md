# DMS-1356 — Vendor namespace-prefix updates: persisted client UUIDs, lock participation, and multi-client compensation

## 1. Document control and approval state

| Item | Value |
|---|---|
| Ticket | [DMS-1356](https://edfi.atlassian.net/browse/DMS-1356) — *Vendor namespace-prefix updates strand recreated Keycloak client UUIDs and bypass workflow serialization* |
| Branch | `DMS-1356` (created from `main` at `5a8c5ac82`) |
| Parent design record | `reference/design/configuration-service/DMS-1218-cms-error-response-compliance.md`, §9.1 "Workflow serialization", §9.1 "Provider update atomicity (INV-65)", §12.8 fourth-review finding V-1, §12.8.1 T-1 |
| Revision | R2 — implementation spec **approved by the architect on 2026-09-17** with two amendments (§5.3 ambiguous current-client outcome; §9.5/§11 E2E scope), both applied in this revision. Q-1..Q-4 answered in §16. No code changed before Step 0.1. |
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

`UpdateVendor` returning `FailureUnknown` or throwing is resolved with the new `GetVendorUpdateState` under the still-held locks: a complete match with the command (scalars and normalized prefix set) means the transaction committed → **204**; a match with the original snapshot means it did not → provider rollback → **500**; anything else → **500** with the state logged. This is the `ResolveAmbiguousOutcomeAsync` pattern of `ApplicationModule.Update`.

## 5. Behavioral contract of `PUT /v3/vendors/{id}` (target)

### 5.1 Phases

```
P0  Guards ........ route/body id match; FluentValidation. Nothing consumed on failure.
P1  Pre-read ...... GetVendorUpdateState(id)  → 404 / 500 / snapshot S0 (no lock held)
P2  Locks ......... distinct ApplicationIds of S0.Clients, ascending; acquire one by one
P3  Re-read ....... GetVendorUpdateState(id) under locks → S1
                    ApplicationId set(S1) != set(S0) → release all, retry from P1 (max 3) → 409
P4  Provider ...... for each client c in S1.Clients (ordered by ApplicationId, then ApiClient.Id):
                      r = UpdateClientNamespaceClaimAsync(c.ClientUuid, command.NamespacePrefixes)
                      r is Success(u) → SyncApiClientUuid(c.Id, expected: c.ClientUuid, new: u)
                      any failure → §5.3 compensation → error response
P5  Repository .... UpdateVendor(command) → 204 / vanished → §5.3 / ambiguous → D-4
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
| P5 `UpdateVendor` → `FailureUnknown` or throws | D-4 resolution: matches command → nothing; matches original → roll back every client; partial/unresolvable/vanished → log, no compensation beyond the vanished-vendor rule above | **204** / **500** / **500** | **500** |
| P2 lock timeout (any position) | Dispose the locks already held | **409** conflict (retry) | — |
| P2 lock infrastructure failure | Dispose held locks | **500** | — |
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
| Per-application aggregate lock domain (`dmscs:application:{id}`), ascending acquisition, held through compensation | Vendor update acquires the same locks from the same `IApplicationLockManager`, ascending, released only in P6. No new lock resource is introduced, so Application/ApiClient/Vendor workflows share one domain. |
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

Not changed: `ApplicationModule.cs`, `ApiClientModule.cs`, lock managers, `IApplicationRepository`, `IApiClientRepository`, `OpenIddictClientRepository.cs`, `IIdentityProviderRepository.cs` (no new result variants), database schema.

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
| A vendor with many applications holds many session locks for the duration of N provider calls | Bounded by the same `AcquireTimeout` contract; each lock is one pooled-free dedicated connection as today for two-lock moves. Ordering is ascending so no cycle with Application/ApiClient workflows is possible. Document the connection cost in the as-implemented section. |
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
