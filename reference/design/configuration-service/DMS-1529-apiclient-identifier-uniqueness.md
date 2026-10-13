# DMS-1529 — DB-level uniqueness for `dmscs.ApiClient.ClientId` and `ClientUuid`

**Implementation specification (Configuration Service / CMS)**

---

## 1. Document control

| Field | Value |
|---|---|
| Spec file | `reference/design/configuration-service/DMS-1529-apiclient-identifier-uniqueness.md` |
| Jira ticket | [DMS-1529](https://edfi.atlassian.net/browse/DMS-1529) — *Add DB-level uniqueness for dmscs.ApiClient.ClientId and ClientUuid* (Story, Open, fix version Ed-Fi API v8.1, parent epic DMS-1072) |
| Branch / worktree | `DMS-1529` at `C:\dev\simpat\Data-Management-Service-DMS-1529`, created from `main` at `fd998bec0` |
| Revision | **R3** — R2 plus the six binding corrections from the second architect review (2026-10-13). R2 followed the first review; R1 was the initial specification. |
| Status | **Approved specification (Step 0.1).** No production code, migration, database operation, or test run belongs to this step. Phase 1 is subject to explicit go-ahead after this commit is reviewed. |
| Related designs | [DMS-1343](./DMS-1343-apiclient-get-identifier-semantics.md) §1.2 (key-first GET dispatch, preserved) and §10.5 (the gap this ticket closes); [DMS-1365](./DMS-1365-keycloak-client-provisioning-compensation.md) (compensation boundaries, preserved) |
| Tag legend | **[JIRA]** from the ticket · **[REPO]** verified in the worktree at `fd998bec0` · **[DESIGN]** decision made here · **[ASSUME]** assumption with its verification · **[R2]** / **[R3]** changed in that revision |

**R2 → R3 change record [R3].** (1) The SQL Server script wraps its transaction in `TRY/CATCH` with an explicit rollback of any active transaction followed by a rethrow; automatic `XACT_ABORT` rollback is no longer the only mechanism. (2) OpenIddict rollback is verified per engine through the production caller, `OpenIddictClientRepository.CreateClientAsync`, against the real engine data repository. (3) Step filters select the upgrade fixtures by an explicit NUnit category, and every step reports selected/passed/skipped counts. (4) The E2E filter is written for PowerShell without escaping. (5) The §6 safety statement is bounded exactly as D-6 is. (6) The migration message is a sample of at most 20 ids per column; the remediation procedure enumerates every affected row and its associations before backup and review.

**R1 → R2 change record [R2].** (1) D-7 replaced: the update repositories classify `UX_ApiClient_ClientUuid` violations and the update modules answer with a sanitized 500 without any compensation update, deletion, or UUID synchronization. (2) Remediation rewritten as an operator procedure with ownership review and a stop rule; no survivor heuristic. (3) Script-local atomicity and write exclusion for both engines, with atomicity and pre-existing-constraint fixtures. (4) D-6 handles thrown and unknown reference-check outcomes and claims only bounded safety. (5) Review decisions Q1–Q8 recorded; registration keeps its 400 as an explicit exception; acceptance-criterion labels corrected. (6) Collation, Keycloak, OpenIddict, both-identifier and commit-boundary revisions; exact verification commands.

---

## 2. Problem statement and evidence

`dmscs.ApiClient` has no database-level uniqueness on `ClientId` (the external OAuth client key) or `ClientUuid` (the identity-provider client identifier). `GetApiClientByClientId`, the lookup behind `GET /v3/apiClients/{clientId}` and the DMS application-context lookup, resolves the row with `SingleOrDefault()`, so a duplicate key is a thrown exception, a `FailureUnknown`, and HTTP 500. DMS-1343 deliberately left DDL and repositories unchanged; this ticket owns the schema fix, the deterministic handling of pre-existing duplicates, and the classified failure paths for every writer the new constraints can reject. **[JIRA]**

| Fact | Evidence |
|---|---|
| `ApiClient` constraints today: `PK_ApiClient`, `FK_ApiClient_Application`, `UX_ApiClient_ApplicationId_Name` (0035). Nothing on `ClientId` or `ClientUuid`. | `backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0003_Create_ApiClient_Table.sql`, `0035_Add_DataStore_And_ApiClient_Name_Uniqueness.sql`; MSSQL equivalents **[REPO]** |
| Column types: PostgreSQL `ClientId VARCHAR(36) NOT NULL`, `ClientUuid UUID NOT NULL`; SQL Server `ClientId NVARCHAR(36) NOT NULL` (column collation = database default, no `COLLATE`), `ClientUuid UNIQUEIDENTIFIER NOT NULL`. | 0003 scripts **[REPO]** |
| `GetApiClientByClientId` ends in `.SingleOrDefault()`; the exception is caught as `FailureUnknown`. | PostgreSQL `Repositories/ApiClientRepository.cs:290-313`; SQL Server `:257-310` **[REPO]** |
| Writers of the two columns: `ApiClientRepository.InsertApiClient`; `ApplicationRepository.InsertApplication` (initial client inside the application aggregate transaction); `ApiClientRepository.UpdateApiClient` (`"ClientUuid" = COALESCE(@ClientUuid, "ClientUuid")`); `ApplicationRepository.UpdateApplication` (`SET "ClientUuid" … WHERE "ClientId" = @ClientId AND "ApplicationId" = @ApplicationId`); `SyncApiClientUuid`; `SyncApplicationApiClientUuid`. `OwnershipTokenRepository` writes only `CreatorOwnershipTokenId`. | search of every `INSERT INTO`/`UPDATE` of `dmscs.ApiClient` **[REPO]** |
| The update modules accept a provider-reported replacement UUID and, when the database update fails, compensate by calling `UpdateClientAsync` against **that** UUID. | `ApiClientModule.cs:733-741`, `ApplicationModule.cs:574-582` **[REPO]** |
| CMS issues keys with `Guid.NewGuid().ToString()`; the provider UUID is Keycloak's server-assigned client id or OpenIddict's `Guid.NewGuid()`. | `ApiClientModule.cs:223`, `ApplicationModule.cs:54`, `OpenIddictClientRepository.cs:146` **[REPO]** |
| Provider-side key uniqueness is already global: `UX_OpenIddictApplication_ClientId` (0012, both engines); the `ApiClient` record documents the key as "Must be unique per realm" (one Keycloak realm). | 0012 scripts; `IApplicationRepository.cs` **[REPO]** |
| Keycloak: a create-call 409 is a `FlurlHttpException` mapped by `ExceptionToKeycloakError` to `IdentityProviderError("Unknown")` → `FailureIdentityProvider` → 502. OpenIddict: a duplicate `OpenIddictApplication` insert reaches `catch (Exception)` → `FailureUnknown` → 500. | `KeycloakClientRepository.cs:188-197, 601-611`; `OpenIddictClientRepository.cs:241-245` **[REPO]** |
| `IdentityModule.RegisterClient`: non-atomic `IsUnique` precheck answering a 400 `ClientId` data-validation failure ("Client with the same Client Id already exists. Please provide different Client Id."); the create-result switch ends in `_ => FailureResults.Unknown`. | `IdentityModule.cs:96-141` **[REPO]** |
| Deployment: DbUp with embedded scripts, journal table `dmscs_SchemaVersions`, no `WithTransaction*()` call, so a failed script is not journaled and the whole script re-runs on retry. The CMS runs the deploy at startup when `DeployDatabaseOnStartup` is set; the Installer runs it too. | `*/Deploy/DatabaseDeploy.cs:50-56`; `frontend/.../Program.cs:333-342`; `Backend.Installer/Program.cs:42,49` **[REPO]** |
| SQL Server unique-violation classification uses error numbers 2627/2601 **and** a message substring of the constraint name, so no constraint name may contain another. | `Backend.Mssql/MssqlExceptionExtensions.cs` **[REPO]** |
| Upgrade-test harness: `JobUpgradeTestDatabase` (isolated database, deploy through script N, journal, constraint and index names) and `NameUniquenessUpgradeTests` on both engines; schema-shape tests enumerate `UX_ApiClient_ApplicationId_Name`. | PostgreSQL `DatabaseShapeTests.cs:61-111`; SQL Server `DeployTests.cs:231-239` **[REPO]** |
| Child foreign keys: `FK_ApiClientDataStore_ApiClient` is `ON DELETE CASCADE` (0022, both engines); `FK_ApiClientOwnershipToken_ApiClient` (0028) is confirmed in Step 1.1. The remediation SQL deletes child rows explicitly and does not depend on cascade. | **[REPO]** / **[ASSUME A3]** |
| DMS-1365 contract: the repository compensates post-identifier provisioning failures; the modules delete a provisioned client only after a handled database-insert failure through `CleanUpProvisionedClientAsync`, never changing the intended response. `HasApiClientUuidReference` exists to protect a provider object before a compensation deletion and is deliberately cross-tenant; the update workflows already gate deletion on it (`TryDeleteRecreatedClientCheckedAsync`). | `ApplicationModule.cs:223-245, 687-715`; `ApiClientModule.cs:844-867`; `IApiClientRepository.cs:38-47` **[REPO]** |
| The only production caller of `IOpenIddictDataRepository.InsertApplicationAsync` is `OpenIddictClientRepository.CreateClientAsync`. | `OpenIddictClientRepository.cs:188` **[REPO]** |
| Operator notes for the 0035 blocker live in `eng/azure-vm/provision/UPDATE.md` (Part A, step 2) and `eng/azure-vm/TROUBLESHOOTING.md`; `docs/OPERATIONS.md` has a "Database versions" note but no CMS database-upgrade section. | **[REPO]** |
| One integration fixture per engine, `ApplicationTests+Given_GetApplicationByClientId_WithDisabledApiClient`, deliberately inserted a second `ApiClient` row with the **same `ClientId`** to drive OpenIddict's `BOOL_AND(IsApproved)` to false — the state 0036 forbids. (R3 wrongly stated that no fixture did this; Step 1.1 found it.) Step 1.1 (PostgreSQL) and Step 1.2 (SQL Server) rewrite it to disapprove the auto-created client in place through `UpdateApiClient`, which preserves what the test proves. Every other fixture uses instance-field GUIDs, and Respawner resets tables between tests. | `ApplicationTests.cs:1169-1181` (both engines), `OpenIddictDataRepository.cs:321-326` **[REPO] [Step 1.1]** |

**Intended behavior.** Both DDL sets enforce global uniqueness of `ClientId` and `ClientUuid`. Upgrading a database that holds duplicates stops deterministically with bounded diagnostics and changes nothing. Every writer that can violate either constraint returns a classified result. The two CMS insert endpoints answer identifier conflicts, whether detected by the database or by the identity provider, with a sanitized 409. Registration keeps its existing 400. Update-path UUID collisions are classified and answered with a sanitized 500 without any further provider mutation. GET behavior is unchanged.

---

## 3. Scope, non-goals, assumptions

### 3.1 In scope

* Migration `0036` on both engines, with script-local atomicity and write exclusion; upgrade and schema-shape tests.
* Additive result variants and narrow constraint classification in `ApiClientRepository` and `ApplicationRepository` (insert, update, sync) on both engines.
* Narrow identity-provider conflict classification: `KeycloakClientRepository`, `OpenIddictClientRepository`, and the engine `OpenIddictDataRepository` implementations.
* Module mappings: 409 on the insert endpoints with safe compensation; sanitized 500 on update and sync collisions with no provider mutation; registration's existing 400.
* Operator documentation and this design document.

### 3.2 Non-goals (explicit)

* A GUID-format constraint on `ClientId` (D-8).
* OpenAPI edits: the insert endpoints declare only `Produces(201)`, and ProblemDetails statuses are not enumerated per operation today.
* DMS runtime code and `SchemaHashConstants.RelationalMappingVersion`.
* GET dispatch, tenant filtering, authorization, response DTOs, and the OpenAPI GET contract (DMS-1343 §1.2).
* Automatic merge, winner selection, deletion of credentials, or rekeying of duplicate rows.
* Provider-side orphan reconciliation; it is a separate activity from the migration.
* A global locking subsystem.
* New E2E scenarios; the API generates both identifiers server-side and cannot produce a collision.

### 3.3 Assumptions

* **[A1]** CMS PostgreSQL databases use a deterministic default collation, so `=` on `VARCHAR` is byte-wise. The PostgreSQL collation fixture asserts this as a prerequisite (§8) rather than assuming it; the script makes no claim about it.
* **[A2]** Keycloak answers a duplicate `clientId` on `POST /clients` with HTTP 409 and persists nothing (DMS-1365 §3.3, P5a).
* **[A3]** `FK_ApiClientOwnershipToken_ApiClient` cascades like `FK_ApiClientDataStore_ApiClient`. Verified in Step 1.1; the remediation SQL deletes child rows explicitly either way.

---

## 4. Decisions **[DESIGN]**

### D-1 Global uniqueness for both columns

`ApiClient` has no `TenantId`; tenant ownership is inherited through Application → Vendor. The key is already globally unique at both providers, so a tenant-scoped database rule could admit a key the provider rejects. `ClientUuid` is a provider-level identity that `HasApiClientUuidReference` and the sync methods already treat as global. Constraints:

* `UX_ApiClient_ClientId UNIQUE ("ClientId")`
* `UX_ApiClient_ClientUuid UNIQUE ("ClientUuid")`

Both columns are `NOT NULL`, so no `NULLS NOT DISTINCT`. Neither name contains, or is contained in, `UX_ApiClient_ApplicationId_Name` or `UX_OpenIddictApplication_ClientId` (SQL Server substring rule). This decision satisfies the ticket's "confirm the tenant model" item and, with the migration, AC1.

### D-2 Migration: preflight both columns, stop, never remediate; script-local atomicity; write exclusion **[R2] [R3]**

**Equality.** Duplicate detection uses the constraint's own equality: `other."ClientId" = candidate."ClientId"` and `=` on the UUID column. On PostgreSQL that is the column's collation, deterministic in every supported CMS database, hence byte-wise. On SQL Server it is the column's collation; case sensitivity follows that collation, and trailing spaces are insignificant for `=` regardless of it. The script adds no normalization and changes no collation.

**Message.** Prefix `API client identifier uniqueness upgrade blocked: `, then `{n} ApiClient row(s) share a ClientId with another row, ids: a, b, …[, ... and k more]. ` and the same for `ClientUuid`, then the fixed remediation: `Resolve the duplicates with the operator procedure in docs/OPERATIONS.md ("Configuration Service database upgrades"), then retry the upgrade. Do not use DELETE /v3/apiClients/{id} for these rows.` At most 20 ids per column are listed; the message is a **sample** of the affected rows (the counts are complete, the id lists are not), and the procedure in §6 enumerates every row. The message stays far below THROW's 2047-character limit.

**PostgreSQL.** One `DO $$ … $$` block: `LOCK TABLE "dmscs"."ApiClient" IN ACCESS EXCLUSIVE MODE;`, the preflight, then either `RAISE EXCEPTION` or the two guarded `ALTER TABLE … ADD CONSTRAINT` statements. A `DO` block is one statement and therefore one implicit transaction under DbUp's autocommit: a failure of the second `ALTER` rolls back the first, and the lock is held from the preflight through constraint creation so no insert can slip between them. `COMMENT ON CONSTRAINT` statements follow outside the block (idempotent; unreachable if the block failed).

**SQL Server [R3].** A single batch with no `GO`:

```sql
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    -- preflight reads use WITH (TABLOCKX, HOLDLOCK) on dmscs.ApiClient, so the exclusive
    -- table lock is held until COMMIT
    -- IF duplicates: THROW 50000, @message, 1;
    -- IF NOT EXISTS UX_ApiClient_ClientId:   ALTER TABLE dmscs.ApiClient ADD CONSTRAINT ... UNIQUE (ClientId);
    -- IF NOT EXISTS UX_ApiClient_ClientUuid: ALTER TABLE dmscs.ApiClient ADD CONSTRAINT ... UNIQUE (ClientUuid);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
SET XACT_ABORT OFF;
```

The explicit `CATCH` rolls back any active transaction and rethrows, so the rollback does not depend solely on `XACT_ABORT`'s automatic behavior; `XACT_ABORT ON` remains so that a statement-level error cannot leave a doomed transaction open. A failure of either `ALTER` rolls back both. This is local to 0036; DbUp's shared policy is unchanged.

**Guards.** `IF NOT EXISTS` on each constraint, so a retry converges and a pre-existing valid constraint is left exactly as it is (never dropped or recreated).

**Journal.** Unchanged DbUp policy: a failed script is not journaled and re-runs in full on retry.

**Maintenance requirement.** Run the upgrade from one Configuration Service instance (or the Installer) with no other instance serving writes. The table locks make a concurrent writer wait rather than bypass the preflight; concurrent DbUp runners are not serialized by DbUp and are outside this ticket.

### D-3 Repository classification **[R2]**

Additive variants:

| Union | New variants |
|---|---|
| `ApiClientInsertResult` | `FailureDuplicateClientId()`, `FailureDuplicateClientUuid()` |
| `ApplicationInsertResult` | `FailureDuplicateClientId()`, `FailureDuplicateClientUuid()` |
| `ApiClientUpdateResult` | `FailureDuplicateClientUuid()` |
| `ApplicationUpdateResult` | `FailureDuplicateClientUuid()` |
| `ApiClientUuidSyncResult` | `FailureNewUuidInUse()` |

PostgreSQL classifies on `ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "UX_ApiClient_ClientId"` (or `…ClientUuid`); SQL Server on `ex.IsUniqueViolation("UX_ApiClient_ClientId")` (or `…ClientUuid`). Each catch rolls back (using `RollbackSafelyAsync` where the method already does), logs at Warning with sanitized identifiers only, and is placed after the existing `FailureDuplicateName` catch. Engine exception types never leave the engine projects.

### D-4 Insert endpoints answer 409 for both identifiers (review decision Q1)

`POST /v3/apiClients` and `POST /v3/applications` map `FailureDuplicateClientId`, `FailureDuplicateClientUuid` (repository) and `ClientCreateResult.FailureDuplicateClientId` (provider) to `FailureResponse.ForConflict(detail, traceIdentifier)` with `application/problem+json`:

```json
{
  "detail": "<fixed text>",
  "type": "urn:ed-fi:api:conflict",
  "title": "Conflict",
  "status": 409,
  "correlationId": "<trace>",
  "validationErrors": {},
  "errors": []
}
```

Fixed details — ClientId: `The generated client key is already in use. Retry the request.`; ClientUuid: `The identity provider client is already associated with another API client.` No row identifier, key, or UUID appears in any response. Duplicate names keep their 400 data-validation contract unchanged.

### D-5 Provider conflicts, narrowly (review decision Q2)

New `ClientCreateResult.FailureDuplicateClientId()` (additive).

* **Keycloak.** A dedicated `catch (FlurlHttpException ex) when (ex.StatusCode == 409)` wrapping **only** the `CreateClientAndRetrieveClientIdAsync` call. It logs at Warning with the sanitized `ClientId` and the fixed phrase "no client was created", performs no cleanup, and performs no post-create service-account role assignment. Role and scope preflight 409s, post-identifier 409s, and non-409 creation failures keep their DMS-1365 classification.
* **OpenIddict.** `IOpenIddictDataRepository.InsertApplicationAsync` returns `OpenIddictApplicationInsertOutcome { Inserted, DuplicateClientId }`. The PostgreSQL and SQL Server `OpenIddictDataRepository` implementations classify **only** a unique violation of `UX_OpenIddictApplication_ClientId`; every other exception propagates. `OpenIddictClientRepository` rolls the transaction back (a role inserted earlier in the attempt included) and returns `FailureDuplicateClientId`. Role and scope unique violations still throw and remain `FailureUnknown`. Test fakes arrange `Inserted` explicitly; nothing relies on the enum's default value.
* **Modules.** `ApiClientModule` and `ApplicationModule` answer with the D-4 409 (nothing to delete). **`IdentityModule.RegisterClient` answers with its existing 400 duplicate-client data-validation response** — an explicit, documented compatibility exception to the insert endpoints' 409 contract: it is the same condition the precheck already reports, and it closes the precheck race that today yields 502 "Unknown".

### D-6 Compensation safety on insert conflicts **[R2]**

* `FailureDuplicateClientId`: the module calls `HasApiClientUuidReference(clientUuid)` and deletes the client it provisioned only on `None`. On `Referenced`, `FailureUnknown`, an unrecognized result, or a **thrown** exception (caught), it skips the deletion, logs at Error with the sanitized UUID and client id ("left in place; review manually"), and still answers 409. Cleanup outcomes never replace the 409 (DMS-1365 D-6, helper reused).
* `FailureDuplicateClientUuid`: never delete (the UUID is provably referenced); Error log; 409.
* `ClientCreateResult.FailureDuplicateClientId`: nothing was created; nothing is deleted.
* **Bounded safety claim.** The reference check and the deletion are two operations, not one. Within supported CMS workflows no other request can attach this UUID to a row in the interval: provider UUIDs are generated per creation (Keycloak server-assigned id; OpenIddict `Guid.NewGuid()`), the insert workflows store only the UUID returned for their own creation, and the update and sync paths store only the UUID the provider reports for the client they addressed, which identifies this request's object alone. The boundary excludes manual database writes and a provider that reuses identifiers. No global lock is introduced and no unconditional guarantee is claimed.

### D-7 Update-path UUID collisions: classified, sanitized 500, no provider mutation **[R2]** (review decision Q3)

A violation of `UX_ApiClient_ClientUuid` during `UpdateApiClient` or `UpdateApplication` means the provider reported a UUID different from the stored one that another row already holds: a provider inconsistency. Compensating as today would call `UpdateClientAsync` on that UUID, that is, mutate another client's provider object. The update repositories therefore return `FailureDuplicateClientUuid`, and the update modules add an arm for it in `switch (repositoryResult)` ahead of the `FailureUnknown` arm that logs at Error ("the identity provider reported a client UUID already held by another API client; stored client state is inconsistent; no compensation attempted"), returns `FailureResults.Unknown`, and does **not** call `TryCompensateAsync`, either `TryDeleteRecreatedClient*` helper, or `SyncApiClientUuid`. `FailureUnknown` resolution is unchanged. The sync methods classify the same violation as `ApiClientUuidSyncResult.FailureNewUuidInUse`; the modules log at Error and return `false` on the same path as `FailureNotExists` (no deletion).

### D-8 Format constraint deferred

A GUID-shape constraint on `ClientId` would remove the numeric-key ambiguity DMS-1343 §1.2 documents, but it rejects keys that are legal today and changes the DMS application-context lookup contract. It is a separate compatibility decision and is recorded here as a follow-up, not implemented.

### Alternatives rejected

* Tenant-scoped index — unsatisfiable at the providers.
* An `IdentityProviderError.Conflict` subtype instead of a `ClientCreateResult` variant — routes through `BadGateway` (502) semantics and `RegisterClient`'s detail pass-through.
* An exception type for the OpenIddict data layer — results are the repository convention.
* A guards-only SQL Server script (0035 precedent) — not atomic.
* Relying on the outcome enum's default in fakes — explicit arrangement instead.

### Review decisions recorded

Q1 409 for both identifiers at both insert endpoints · Q2 registration keeps its 400, including the provider-detected race · Q3 update collisions classified, sanitized 500 externally · Q4 `docs/OPERATIONS.md` section with both-engine remediation; Azure deployment docs link to it without duplicating the procedure · Q5 direct SQL remediation with the §6 procedure · Q6 names as listed, plus the two update-result variants; engine classification stays in engine projects · Q7 scoped regression E2E only · Q8 this spec path and the README index entry.

---

## 5. Affected files and contracts

| Area | Files | Change |
|---|---|---|
| Migrations | `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0036_Add_ApiClient_Identifier_Uniqueness.sql`; `src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/Deploy/Scripts/0036_Add_ApiClient_Identifier_Uniqueness.sql` | new; no existing script is edited |
| Result types | `Backend/Repositories/IApiClientRepository.cs`, `IApplicationRepository.cs`, `IIdentityProviderRepository.cs`; `Backend.OpenIddict/Repositories/IOpenIddictDataRepository.cs` | additive variants; `InsertApplicationAsync` returns `OpenIddictApplicationInsertOutcome` |
| Repositories | PostgreSQL and SQL Server `Repositories/ApiClientRepository.cs`, `Repositories/ApplicationRepository.cs`, `OpenIddict/Repositories/OpenIddictDataRepository.cs` | narrow catches; outcome return |
| Providers | `Backend.Keycloak/KeycloakClientRepository.cs`; `Backend.OpenIddict/Repositories/OpenIddictClientRepository.cs` | conflict classification |
| Modules | `Frontend.AspNetCore/Modules/ApiClientModule.cs`, `ApplicationModule.cs`, `IdentityModule.cs` | 409 arms with checked cleanup; update and sync collision arms; registration arm |
| Integration tests (both engines) | new `ApiClientIdentifierUniquenessUpgradeTests.cs`; `Jobs/JobUpgradeTestDatabase.cs` (script 36 constants); `DatabaseShapeTests.cs` / `DeployTests.cs`; `ApiClientTests.cs`; `ApplicationTests.cs`; `ApplicationConsistencyOperationTests.cs`; `OpenIddictDataRepositoryTests.cs`; new OpenIddict provider-path fixture (§8) | |
| Unit tests | `Backend.Tests.Unit/KeycloakClientRepositoryTests.cs`, `OpenIddictClientRepositoryTests.cs`; `Frontend.AspNetCore.Tests.Unit/Modules/ApiClientModuleTests.cs`, `ApplicationModuleTests.cs`, `IdentityModuleTests.cs` | |
| Docs | this spec; `reference/design/configuration-service/README.md`; `docs/OPERATIONS.md` (new section "Configuration Service database upgrades"); `eng/azure-vm/provision/UPDATE.md`; `eng/azure-vm/TROUBLESHOOTING.md` | |

Untouched: the GET handlers and the repositories' GET methods, response DTOs, OpenAPI, DMS, `RelationalMappingVersion`, the lock managers, the delete workflows.

---

## 6. Migration and remediation for existing databases **[R2] [R3]**

The upgrade blocker reports complete counts and a sample of at most 20 ids per column. The operator procedure below is documented in `docs/OPERATIONS.md` for both engines; the Azure deployment documents link to it.

1. **Stop CMS writes.** Stop every Configuration Service instance; the upgrade runs at startup or through the Installer.
2. **Enumerate every affected row.** Run the two duplicate queries (provided for each engine) that return **all** rows whose `ClientId` or `ClientUuid` is shared with another row, together with their `ApiClientDataStore` and `ApiClientOwnershipToken` associations. Do not treat the ids in the startup message as the complete set.
3. **Back up** the enumerated `ApiClient` rows and their associations (or take a full database backup).
4. **Inspect** each duplicate group: both identifiers; `ApplicationId` → Application → Vendor → Tenant ownership; the audit columns; and the identity provider's client for that key and UUID (Keycloak admin console or the `OpenIddictApplication` row), including its configuration (display name, claim-set scope, education-organization and data-store claims).
5. **Establish the intended owner** by operator review of step 4. **Stop if ownership remains ambiguous.** No "keep lowest id" rule is offered, and no provider reconciliation happens automatically.
6. **Delete only explicitly reviewed row ids** inside one transaction: first the `ApiClientOwnershipToken` and `ApiClientDataStore` rows for those ids, then the `ApiClient` rows (engine-specific SQL provided). Deleting a row discards its data-store assignments and ownership links. The provider client is not touched by this step.
7. **Re-check** that both uniqueness queries return zero rows, then retry the upgrade.
8. Any later provider-side orphan cleanup is a separate activity and is not part of the migration.

`DELETE /v3/apiClients/{id}` is prohibited during duplicate resolution: it also deletes the provider client by `ClientUuid`, which for rows sharing a UUID destroys the survivor's provider object.

---

## 7. Transactions, concurrency, rollback, compensation, cross-tenant safety

* **Inserts.** One transaction per writer; the violating `INSERT` raises inside it; the rollback discards `Application`, `ApplicationEducationOrganization`, `ApiClient`, `ApiClientDataStore` and `ApplicationProfile` rows written by the attempt (asserted). **Updates and syncs.** The rollback leaves the row unchanged (asserted).
* **Concurrency.** The database serializes same-key inserts; the loser receives the unique violation after the winner commits and the repository classifies it. The fixture — connection A inserts key K uncommitted, the repository insert of K is started, A commits, the awaited result is `FailureDuplicateClientId` — verifies the losing write's **outcome** regardless of interleaving; it does not claim to prove that blocking occurred.
* **Compensation.** Per D-6 and D-7. Within the supported-workflow boundary stated in D-6, the modules do not delete or mutate a provider object that another row references; outside that boundary (manual database writes, identifier reuse by a provider) the reference check is a check followed by a separate deletion, and no stronger claim is made.
* **Cross-tenant.** The constraints are global. A tenant-B caller colliding with tenant A's key receives the fixed 409 detail and nothing else; the preflight message lists internal row ids only (0035 precedent); the reference check is the existing deliberately cross-tenant one and exposes no tenant data.

---

## 8. Test matrix

**Migration — new `ApiClientIdentifierUniquenessUpgradeTests` on both engines, every fixture tagged `[Category("ApiClientIdentifierUniquenessUpgrade")]`, database deployed and journaled through 0035, then 0036 [R3].**

| Fixture | Expectation |
|---|---|
| Unique identifiers across applications and tenants | upgrade succeeds; journal gains exactly 0036; a repeat deploy adds nothing; rows byte-equal; post-upgrade a repeated `ClientId` and a repeated `ClientUuid` are each rejected naming their constraint; a repeated key under another application is rejected (global) |
| Duplicate `ClientId` (different applications and tenants) | blocked with the exact message; nothing journaled; neither constraint added; rows unchanged |
| Duplicate `ClientUuid` | same |
| Duplicates in both columns | one message listing both |
| 23 duplicates | first 20 ids and "... and 3 more" |
| Pre-existing valid `UX_ApiClient_ClientId` | preserved untouched; `UX_ApiClient_ClientUuid` added; 0036 journaled |
| Atomicity: a non-constraint index named `UX_ApiClient_ClientUuid` pre-created on another column (PostgreSQL: `relation already exists`; SQL Server: error 1913) so the second `ALTER` fails after the first succeeded | `UX_ApiClient_ClientId` absent afterwards; journal and rows unchanged; after the index is dropped, a retry succeeds and journals 0036 with both constraints present |
| Remediation then retry | blocked; the reviewed stale row deleted directly (child rows first); retry succeeds |
| Collation — SQL Server | the fixture reads `sys.columns.collation_name` for `ClientId`; a case-variant pair is blocked if and only if the collation is case-insensitive; a trailing-space pair is blocked (always, by `=` semantics); each pair seeded independently |
| Collation — PostgreSQL | the fixture first asserts the deterministic prerequisites (`'a' = 'A'` is false; `'x'::varchar = 'x '::varchar` is false), then expects both pairs to upgrade successfully, to remain insertable afterwards, and exact repeats to be rejected |

**Schema shape.** PostgreSQL `DatabaseShapeTests` and SQL Server `DeployTests` gain both constraints with their key columns.

**Repository (both engines).**

* `ApiClientTests`: duplicate `ClientId`; duplicate `ClientUuid`; both identifiers duplicated (either classified variant accepted; nothing written, `ApiClientDataStore` included); same key under another application; same key under another tenant (two `Multitenant` contexts); concurrency outcome.
* `ApplicationTests`: the same through `InsertApplication`, plus aggregate rollback (no `Application`, `ApplicationEducationOrganization`, `ApiClient`, mapping rows).
* Updates: `UpdateApiClient` and `UpdateApplication` with another row's UUID → `FailureDuplicateClientUuid`; rows unchanged.
* `ApplicationConsistencyOperationTests`: both sync methods → `FailureNewUuidInUse`; stored UUID unchanged; the existing `AlreadyApplied`, `FailureStaleState`, `FailureNotExists`, `FailureNotExistsSafeToDelete` fixtures untouched.
* `OpenIddictDataRepositoryTests`: duplicate `ClientId` → `DuplicateClientId`; the original `OpenIddictApplication` row byte-equal; a duplicate role name still throws.
* **OpenIddict provider path through the production caller [R3]:** a per-engine integration fixture constructs `OpenIddictClientRepository` with the real engine `OpenIddictDataRepository`, seeds a provider client with key K and a role name the attempt will create, then calls `CreateClientAsync(K, …)`. It asserts `ClientCreateResult.FailureDuplicateClientId`, the original provider client row byte-equal, and no `OpenIddictRole`, `OpenIddictScope`, application-scope or client-role row left by the attempt (the role created earlier in that attempt rolled back).

**Provider unit.**

* Keycloak: create-call 409 → `FailureDuplicateClientId`, no delete, no service-account role assignment, sanitized Warning; `CreateRoleAsync` 409 and `CreateClientScopeAsync` 409 in preflight → `FailureIdentityProvider` with "creation not attempted"; post-identifier 409 (service-account lookup) → DMS-1365 compensation unchanged; non-409 creation failures unchanged.
* OpenIddict: `DuplicateClientId` outcome → rollback, no commit, no scope or client-role inserts, `FailureDuplicateClientId`; every success fixture arranges `Inserted` explicitly; a role or scope insert that throws → `FailureUnknown`.

**Module unit.**

* Inserts (`ApiClientModuleTests`, `ApplicationModuleTests`): `ClientCreateResult.FailureDuplicateClientId` → exact 409 body, no database insert, no delete; repository `FailureDuplicateClientId` with reference `None` → 409 and exactly one deletion; with `Referenced`, `FailureUnknown`, an unrecognized subclass, or a thrown exception → 409, no deletion, Error log; `FailureDuplicateClientUuid` → 409, no deletion; credentials absent; sentinel text never reaches a response; both-identifier arrangement; the existing duplicate-name 400 fixtures untouched.
* Updates: `FailureDuplicateClientUuid` → sanitized 500, no `UpdateClientAsync` rollback call, no deletion, no `SyncApiClientUuid`, lock released.
* Syncs: `FailureNewUuidInUse` → no deletion, Error log, sanitized 500.
* `IdentityModuleTests.RegisterEndpointTests`: `FailureDuplicateClientId` → the existing 400 duplicate-client body.

**E2E.** A scoped regression run of `ApiClients.feature` and `Applications.feature` on the self-contained PostgreSQL lane; no new scenarios. Both features must be confirmed to have executed.

---

## 9. Acceptance criteria and scope items → steps → verification

| Item | Steps | Verification that fails if the feature is reverted or weakened |
|---|---|---|
| **AC1** Both DDL sets enforce uniqueness of `ClientId` and `ClientUuid` | 1.1, 1.2 | shape tests list both constraints with their key columns on both engines; post-upgrade duplicate inserts are rejected naming each constraint |
| **AC2** Migrations succeed on databases with no duplicates and fail deterministically with them | 1.1, 1.2 | clean-upgrade, blocked, atomicity, pre-existing-constraint, remediation-then-retry and collation fixtures (§8) |
| **AC3** Inserting a duplicate key through the repository yields a classified failure, not an unhandled exception | 2.1, 2.2 | both insert writers × both identifiers × both engines return the named variants; cross-application and cross-tenant; both-identifier collision; aggregate rollback; concurrency outcome; update and sync collisions classified with rows unchanged |
| **AC4** No change to `GET /v3/apiClients/{id}` or `GET /v3/apiClients/{clientId}` | — | zero diff in the GET code paths (reported in the final diff review); the existing key-first dispatch unit tests, `ApiClientOpenApiContractTests`, and the integration GET suites run unchanged and green |
| Scope: new numbered scripts, existing scripts untouched | 1.1, 1.2 | the diff adds only `0036_*` under `Deploy/Scripts` |
| Scope: `ClientUuid` decision | D-1 | documented; enforced by AC1 |
| Scope: pre-existing duplicates | 1.x, §6 | AC2 fixtures; documented procedure |
| Scope: tenant model | D-1, 2.x | cross-tenant rejection tests |
| Scope: providers and CMS insert paths answer 409-style, not 500 | 3.1–3.3, 4.1, 4.2 | exact 409 bodies for all three conflict sources on both endpoints; Keycloak catch narrowness; OpenIddict classifies only `UX_OpenIddictApplication_ClientId`, verified through the production caller per engine; registration 400 exception pinned |
| Scope: backend integration tests on both engines | all | SQL Server lane on a real SQL Server 2025; the new fixtures execute with zero skips on both engines, with counts reported |

---

## 10. Rollout risks and compatibility

* Startup stops on a database with identifier duplicates — intended, deterministic, documented with the §6 procedure; requires a maintenance window with no other CMS writer (D-2).
* SQL Server equality follows the column collation; CMS-issued keys are lowercase GUIDs, so only externally seeded data is affected.
* New 409 where callers previously saw 500 or 502; the registration race moves from 502 "Unknown" to the precheck's 400 (explicit exception).
* An update-path collision is now a classified 500 with no provider mutation; previously compensation could mutate another client's provider object.
* Additive result variants; every consumer switch gains an explicit arm; no external implementer of the unions exists in this repository.

---

## 11. Phases and steps

Each step is one local commit. CSharpier runs on every changed C# file. The checks listed run before the commit; selected, passed, failed and skipped counts are reported. Work stops for approval after every step.

### Phase 0 — Specification

* **0.1** Commit this specification and the README index line. No other change.

### Phase 1 — Migrations

* **1.1 PostgreSQL script and tests.** Files: PostgreSQL `0036_Add_ApiClient_Identifier_Uniqueness.sql`; `Jobs/JobUpgradeTestDatabase.cs` (`IdentifierUniquenessScript = 36` and its name); new `ApiClientIdentifierUniquenessUpgradeTests.cs` (category `ApiClientIdentifierUniquenessUpgrade`); `DatabaseShapeTests.cs`. Confirms A3 and records the 0028 FK clause. Risks: exact message text; `DO`-block locking. Checks: the complete PostgreSQL integration project without a filter (`FullyQualifiedName~DatabaseShapeTests` and `~NameUniquenessUpgrade` would match file and abstract-class names, not the concrete fixtures), reporting selected/passed/failed/skipped counts and confirming by concrete name that these groups executed with zero skips: the nine `Category=ApiClientIdentifierUniquenessUpgrade` fixtures; `Given_CMS_PostgreSQL_database_shape`; the existing name-upgrade fixtures (`Given_names_that_repeat_only_across_tenants_and_applications`, `Given_duplicate_data_store_names_within_a_tenant`, `Given_duplicate_data_store_names_without_a_tenant`, `Given_duplicate_api_client_names_within_an_application`, `Given_duplicate_names_in_both_tables`, `Given_more_duplicate_rows_than_the_message_lists`); and the `ApiClientTests` fixtures.
* **1.2 SQL Server script and tests.** Mirrors 1.1 with `DeployTests.cs` and the `TRY/CATCH` transaction. Depends on 1.1 (shared message text). Checks: the complete SQL Server integration project with `--filter "Category=MssqlIntegration"` (§12), confirming the mirrored groups by concrete name with zero skips.

### Phase 2 — Repository classification

* **2.1 Result variants, PostgreSQL repositories, PostgreSQL tests.** Files: `IApiClientRepository.cs`, `IApplicationRepository.cs`; PostgreSQL `ApiClientRepository.cs`, `ApplicationRepository.cs`; PostgreSQL `ApiClientTests.cs`, `ApplicationTests.cs`, `ApplicationConsistencyOperationTests.cs`. Buildable alone (additive variants; every existing consumer switch has a default arm). Checks: PostgreSQL integration filtered to the three fixtures' names; both unit projects build.
* **2.2 SQL Server repositories and tests.** Depends on 2.1. Checks: SQL Server lane with the mirrored filter.

### Phase 3 — Module mapping

* **3.1 `ApiClientModule` insert.** 409 arms; checked cleanup with thrown, unknown and unrecognized reference-check handling; logs; unit tests. Checks: frontend unit tests filtered to `ApiClientModuleTests`.
* **3.2 `ApplicationModule` insert.** Same. Checks: `ApplicationModuleTests`.
* **3.3 Update and sync collision arms** in both modules (`FailureDuplicateClientUuid` → sanitized 500 with no compensation; `FailureNewUuidInUse` → no deletion) and unit tests. Checks: both module fixtures.

### Phase 4 — Provider conflicts

* **4.1 Keycloak conflict emission with all consumer mappings and tests (combined).** `ClientCreateResult.FailureDuplicateClientId`; the narrow Keycloak catch; the arms in `ApiClientModule`, `ApplicationModule` and `IdentityModule`; `KeycloakClientRepositoryTests`, both module test files, `IdentityModuleTests`. Combined so that no commit emits a result that falls through to 500. Checks: backend and frontend unit projects in full.
* **4.2 OpenIddict.** `IOpenIddictDataRepository` outcome; PostgreSQL and SQL Server `OpenIddictDataRepository`; `OpenIddictClientRepository`; `OpenIddictClientRepositoryTests`; both engines' `OpenIddictDataRepositoryTests`; the per-engine production-caller fixture. Must land together (interface with two implementations). Checks: backend unit project; both integration lanes filtered to the OpenIddict fixtures.

### Phase 5 — Documentation

* **5.1** `docs/OPERATIONS.md` section "Configuration Service database upgrades" (procedure of §6 with both engines' enumeration, backup, delete and re-check SQL); `eng/azure-vm/provision/UPDATE.md` and `eng/azure-vm/TROUBLESHOOTING.md` entries that link to it; this spec's "as implemented" section.

### Phase 6 — Verification

* **6.1** The complete §12 matrix, the acceptance-criterion evidence report, and the local commit list. No push and no pull request until explicit approval.

---

## 12. Verification commands

Run from the worktree root. Required results: 0 failed; the new fixtures 0 skipped on both engines; selected counts reported per command.

| Gate | Command | Prerequisite |
|---|---|---|
| Format | `dotnet csharpier check src/config` | — |
| Backend unit | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj` | — |
| Frontend unit | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj` | — |
| PostgreSQL integration | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj` | a local PostgreSQL reachable through the project's `appsettings.json`; the upgrade fixtures create and drop their own databases |
| SQL Server integration | `docker run --name dms-mssql-integration-2025 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='EdFi_Dms1!' -p 1434:1433 -d mcr.microsoft.com/mssql/server:2025-latest` · `$env:ConnectionStrings__MssqlAdmin = "Server=localhost,1434;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true"` · `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj --filter "Category=MssqlIntegration"` | a real SQL Server (collation behavior is under test, so not LocalDB); `RequireConfiguredForCiOrSkipLocally` must not skip |
| E2E, scoped | teardown; then `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -E2ETestFilter "FullyQualifiedName~ApiClients|FullyQualifiedName~Applications"`; then teardown. Confirm from the run output that both features executed. | Docker |

Step-level filters use the explicit category `ApiClientIdentifierUniquenessUpgrade` for the upgrade fixtures and concrete fixture names elsewhere; abstract base-class and file names are not relied on.

---

## 13. Acceptance-criterion evidence (to be completed in Phase 6)

| AC | Evidence |
|---|---|
| 1 | — |
| 2 | — |
| 3 | — |
| 4 | — |

## 14. As implemented (to be completed per step)

| Step | Commit | Files | Checks |
|---|---|---|---|
| 0.1 | `958c98952` | this spec; README index entry | none (specification only) |
| 1.1 | `aa8fea71f` | PostgreSQL `0036_Add_ApiClient_Identifier_Uniqueness.sql`; `Jobs/JobUpgradeTestDatabase.cs`; new `ApiClientIdentifierUniquenessUpgradeTests.cs` (9 fixtures, category `ApiClientIdentifierUniquenessUpgrade`); `DatabaseShapeTests.cs`; `ApplicationTests.cs` (the duplicate-key fixture rewritten, see §2); this spec (§2 evidence row, §11 filters, this table) | complete PostgreSQL integration project against `postgres:18.6-alpine` (trust auth, port 5432): 1219 total, 1157 passed, 0 failed, 62 not executed (pre-existing `OperationalProbe` self-skips); the 9 upgrade fixtures 51/51, shape 10/10, name-upgrade 33/33, `ApiClientTests` 28/28 |
| 1.2 | `8e2a4a649` | SQL Server `0036_Add_ApiClient_Identifier_Uniqueness.sql` (`TRY/CATCH`, `XACT_ABORT`, `TABLOCKX, HOLDLOCK`); `Jobs/JobUpgradeTestDatabase.cs`; new `ApiClientIdentifierUniquenessUpgradeTests.cs` (10 fixtures — the collation cases are seeded independently as case and trailing-space fixtures — tagged `ApiClientIdentifierUniquenessUpgrade` and `MssqlIntegration`); `DeployTests.cs`; `ApplicationTests.cs` (mirrored rewrite); this table | complete `Category=MssqlIntegration` lane. The development host is ARM64 and cannot run the SQL Server container, so the lane ran on a dedicated LocalDB instance `DMS1529` created on the SQL Server **2025** engine (17.0.4025.3): `Server=(localdb)\DMS1529;Integrated Security=true;TrustServerCertificate=true`. Same engine version as the documented container; the container lane is still part of Phase 6 on a host that can run it. Two environment notes: an ARM64 process cannot load LocalDB's x64 native API, so the connection string must name the instance's pipe (`Server=np:\\.\pipe\LOCALDB#<id>\tsql\query`, read from `sqllocaldb info`) instead of `(localdb)\DMS1529`; and the test database was pre-created with its files in the session scratchpad because LocalDB's default data directory already held another instance's `edfi_configurationservice_mssql_integration.mdf`. Result: 1187 total, 1186 passed, 1 failed, 0 skipped; the 10 upgrade fixtures 53/53, `DeployTests` 12/12, name-upgrade 33/33, `ApiClientTests` 30/30, the rewritten disabled-client fixture 1/1. The one failure, `JobScheduleRepositoryTests+Given_a_previous_job_active_across_two_due_intervals("Error")` (DMS-1437, expected `SkippedActiveJob`, got `AlreadyEnqueued`), touches nothing this ticket changes and passed 6/6 when rerun in isolation on the same instance. Recorded as a **suspected timing flake**: the isolated pass does not establish its cause. A full SQL Server lane with zero failures, on the SQL Server 2025 container, remains a Phase 6 requirement |
| 2.1 | — | `IApiClientRepository.cs` (`ApiClientInsertResult.FailureDuplicateClientId`/`FailureDuplicateClientUuid`, `ApiClientUpdateResult.FailureDuplicateClientUuid`, `ApiClientUuidSyncResult.FailureNewUuidInUse`); `IApplicationRepository.cs` (`ApplicationInsertResult.FailureDuplicateClientId`/`FailureDuplicateClientUuid`, `ApplicationUpdateResult.FailureDuplicateClientUuid`); PostgreSQL `ApiClientRepository.cs` and `ApplicationRepository.cs` (narrow `UniqueViolation` + exact constraint-name catches on insert, update and sync, rolling back and logging fixed text only); PostgreSQL `ApiClientTests.cs`, `ApplicationTests.cs`, `ApplicationConsistencyOperationTests.cs` (new fixtures); this table | `csharpier check` on the seven changed files: clean. Backend and frontend unit projects: build 0 errors, 0 warnings. Complete PostgreSQL integration project: 1242 total, 1180 passed, 0 failed, 62 not executed (the 14 pre-existing `OperationalProbe` fixtures). New fixtures: `Given_api_client_inserts_that_repeat_an_identifier` 10/10, `Given_application_inserts_whose_initial_client_repeats_an_identifier` 7/7, `Given_a_sync_to_a_uuid_another_api_client_holds` 3/3, `Given_a_sync_to_a_uuid_held_by_a_foreign_tenant` 3/3. A first run filtered by `FullyQualifiedName~ApiClientTests|~ApplicationTests|~ConsistencyOperationTests` passed 90/90 but did not select the two sync fixtures (their concrete names contain none of those strings), which is why the complete project was run |

Observation recorded in Step 1.1, outside this ticket's scope: `OpenIddictDataRepository.GetApplicationByClientIdAsync` aggregates `BOOL_AND(ac."IsApproved")` over every `ApiClient` row sharing the key. Under `UX_ApiClient_ClientId` at most one row can match, so the aggregate reduces to that row's flag; the query stays correct and is not changed here.
