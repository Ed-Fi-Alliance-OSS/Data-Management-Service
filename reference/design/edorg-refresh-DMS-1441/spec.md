# DMS-1441 — CMS education-organization refresh, snapshots, and tenant aggregate

Specification **v4** (approved by the architect on 2026-11-20, with the step 1.2 reclaim and staging checkpoint clarification, the step 1.3 SQL Server staging change in §6.2 step 7, the step 2.2 and 2.4 review corrections in §6.5 and §6.6, and the reduced review plan in §7.0).

- Written against `main` at `df7b857fc`. Every code citation (file:line) refers to that commit.
- Three earlier review rounds (v1 to v3) are summarized in §0. Their full text is not committed.
- Step 0.1 committed this document. Implementation follows the steps in §7, one approved local commit at a time.

Path abbreviations:

| Abbreviation | Path |
| --- | --- |
| `CFG` | `src/config` |
| `BE` | `CFG/backend/EdFi.DmsConfigurationService.Backend` |
| `PG` | `…Backend.Postgresql` |
| `MS` | `…Backend.Mssql` |
| `FE` | `CFG/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore` |
| `EOP` | `BE/EducationOrganizationProjection` |

---

## 0. Revision history

### 0.0 v3 → v4 (architect review of v3)

| # | v3 finding | v4 resolution | Where |
| --- | --- | --- | --- |
| 1 | The reclaim predicate `NOT IN (ActiveGeneration, g)` keeps abandoned rows while `ActiveGeneration` is null | The predicate is now null-safe: `Generation <> @g AND (ActiveGeneration IS NULL OR Generation <> ActiveGeneration)`. Tests assert the exact **retained generation IDs**, both with and without an active generation. | §6.2 step 3; steps 1.2/1.3 |
| 2 | A strict renewal could judge expiry before waiting for the row lock | **Lock first, then validate.** Every operation that depends on ownership first locks the target row in its own statement. It then validates owner, generation and expiry with a **fresh statement clock** in a separate statement, and only then writes, all in one bounded transaction. This mirrors `PostgresqlJobFenceFactory.cs` L39-41, and applies to both providers (SQL Server evaluates `SYSUTCDATETIME()` once per statement). New tests cover a lease that expires *during the lock wait*. | §6.2; steps 1.2/1.3 |
| 3 | Changing the compose worker default could silently stop workers for managed deployments | **Compose defaults are unchanged** (`WorkerEnabled` `:-true`). Bootstrap mode is an **explicit setting**: only the fresh-install `.env.example` copies and the CMS-E2E environment set `CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED=false`. An existing `.env` without the variable keeps workers on, and so fails loudly when reader settings are missing. A configuration-matrix test proves it. | §2 E2 rule 7; step 5.1 |
| 4 | The suspension limitation was described too narrowly and its timing too strongly | The limitation is restated: delays in execution or scheduling (a continuation descheduled, process suspension, or late timer or cancellation delivery), with timing bounds that hold only under normal scheduling. The guarantee that can be tested is unchanged: once another owner takes over, the previous owner can never stage or publish. A new test pauses the old owner after admission. | §6.2 guarantees; steps 2.3/2.5 |
| 5 | The operational test did not prove that recurring scheduled runs happen | Step 5.2 now asserts completed jobs whose `SourceScheduleId` is that tenant's refresh schedule, then changes source data and checks that a **later occurrence** publishes the change, with no manual trigger. | step 5.2 |

Architect positions on v3:
- D2 sequencing agreed; D3 concrete contract approved; D4 approved.
- D9 architecture accepted, pending finding 2 (now applied).
- D1, D5–D8 and D10 remain agreed.

### 0.1 v2 → v3 (architect review of v2)

| # | v2 finding | v3 resolution | Where |
| --- | --- | --- | --- |
| 1 | An expired target lease did not stop its previous owner from entering the reader | **The target lease is renewed and admission-checked.** <br>• It reuses the validated `JobSettings` `LeaseDuration` and `RenewalInterval`. <br>• A strict renewal (owner, generation and *unexpired*, by statement clock) is the **last operation before `ReadAllAsync`**, with nothing awaited in between. <br>• A renewal loop runs during the read; a lost or uncertain renewal cancels the read. <br>• Reclaiming abandoned rows moved out of acquisition into its own owner-checked transaction, which runs before admission. <br>• The guarantee under process suspension is stated exactly. | §6.2; D9; steps 1.2/1.3, 2.3, 2.5 |
| 2 | The composer could exceed 1000 characters, and it trusted caller-supplied messages | **Composite codes are registered with an allowlist of category codes.** Startup proves the worst case fits in 1000 characters. Composition resolves every text from the registry by code and never uses a caller's `.Message`. Any violation fails closed to `HandlerFailed`. Files are listed. | §6.6; step 2.2 |
| 3 | Truncation could hide problem categories (AC8) | **Every distinct category code is always listed.** Explanatory prose is added only while it fits. Tests cover the complete supported category set. | §6.6 |
| 4 | The freshness rule contradicted its own test | **The freshness skip is removed.** Every attempt re-reads every target under target coordination (at-least-once). The rule, the column and the test now agree. | §6.2, §6.5; step 2.5 |
| 5 | Deployment repair came after the startup break | **The startup requirement and every shipped-configuration repair land in one commit** (step 5.1). <br>• Bootstrap and operational configurations are explicit. <br>• Migration guidance covers existing worker deployments. <br>• An operational Instance E2E proves credential provisioning, worker enablement and a real *scheduled* execution (step 5.2). | §2 E2; steps 5.1, 5.2 |
| 6 | SQL Server acquisition could not tell "busy" from "absent" | **One locked decision on both providers.** <br>• PostgreSQL: `FOR KEY SHARE` on the store, then `FOR UPDATE` on the target row. <br>• SQL Server: `HOLDLOCK` on the store, then `UPDLOCK, HOLDLOCK` on the target row (key-range on absence). <br>• Busy returns without inserting. | §6.2 step 2 |
| 7 | Locking claims went beyond what the mechanism proves | **Lock-order table** with bounded waits. Deadlocks and timeouts are classified rather than ruled out. The blocking point under SQL Server lock compatibility is corrected. Ordinary HTTP writers fail with existing responses and roll back; nothing retries them automatically. Tests cover blocked HTTP writers. | §6.12; steps 1.5, 3.3 |

Positions adopted from the v2 review:
- D1, D5, D6, D7, D8 and D10 agreed.
- D2 is **Option A**.
- D3 is approved in principle.
- Q8: workers off is an explicit bootstrap configuration, E2E runs fully operational, and existing operational deployments are never silently stopped.
- Q9: no data store IDs in public errors.

### 0.2 v1 → v2 (summary)

- Target lease introduced.
- Static validation always on.
- Composite error.
- Bound of 2 staged generations per target.
- Coherent aggregate read boundary.
- Per-schedule tenant recheck.
- Concrete live harness.
- Prune is never fatal.
- Complete step fields.

The full v1 review table is not committed.

---

## 1. Sources and authority

| Source | Use |
| --- | --- |
| Jira DMS-1441 (In Progress), all fields + 1 comment (2026-09-28 DMS-1437 decision) | **Authoritative ACs (22).** The spike copy matches them one to one. |
| ADMINAPI-1496 (Done, "v3 only"), ADMINAPI-1488 (Done) | Contract inputs. |
| `reference/design/jobs-DMS-1437/admin-api-v3-ed115fd8.yaml` + `admin-api-v3-provenance.md` | Pinned OpenAPI (ODS-Admin-API `ed115fd8`). |
| Upstream source at `ed115fd8` and `main` `a8fca91e` (2026-11-19), read with `gh api` | Refresh, ReadTenants and TenantService behavior; the manage query; the delete job; default settings. |
| `reference/design/jobs-DMS-1437/spec.md`, `docs/CONFIGURATION-SERVICE-BACKGROUND-JOBS.md` | Jobs contract. |
| `reference/design/edorg-projection-DMS-1440/spec.md`, `docs/EDUCATION-ORGANIZATION-PROJECTION.md`, `docs/CONFIGURATION.md` | Reader contract. |
| `reference/design/managed-data-stores-DMS-1439/contract-provenance.md`, `docs/CMS-MANAGED-DATA-STORES.md` | Managed lifecycle. |
| Code at `df7b857fc` | Cited in §4. |

## 2. Evidence questions

### E1. The "201 contract blocker" vs the pinned 202 — resolved

The pinned yaml's hash equals its provenance record. The yaml declares 202 and a relative `/v3/jobs/{jobId}` `Location` with `jobQueuedResult` for both refresh routes, plus 404 for the per-store route. The pinned source returns `Results.Accepted` with fixed, distinct messages. ADMINAPI-1496 was tested before the pin. The "201" claims exist only in the pre-1496 `data-store-lifecycle-findings.md`, a historical document that stays unedited.

For the tenant GET, the pinned yaml declares 200/401/403/500. The pinned source already returns 400 and 404, and upstream `main` declares both, so CMS declares them too.

Documented differences:
- (a) GUID job IDs (DMS-1437 D-14).
- (b) No Swagger-referer bypass.
- (c) `urn:ed-fi:management-api:` problem types.
- (d) CMS generator schema and tag names.
- (e) Canonical tenant name in `id`/`name`.

### E2. Reader allowance vs AC6 — Option A (selected)

**Facts:**
- The validator's early return skips every rule for a blank `DmsBaseUrl` (`DmsEducationOrganizationProjectionSettingsValidator.cs` L30-33).
- The shipped section is empty (`FE/appsettings.json` L44).
- Compose runs workers by default and sets no reader settings (`eng/docker-compose/local-config.compose.yml` L57, `published-config.compose.yml` L61).
- The reader credential is a CMS application created through CMS itself (`docs/CONFIGURATION.md` L475).

**Rules:**

1. **Always validated**, whatever `DmsBaseUrl` holds. The early return is removed.
   - Every numeric bound.
   - `MaxResponseBodyBytes ≥ PageSize×2048+1024`.
   - A half-filled shared pair.
   - Every `TenantCredentials` entry complete.
   - `ContractVersions` distinct and supported.
   - The `DmsBaseUrl` format, when set.
   - A shared pair with both values empty still counts as absent.
2. **Required on every replica with `JobSettings:WorkerEnabled=true`:** `DmsBaseUrl` and at least one complete credential pair. Only workers call DMS.
3. **Always validated:** `AppSettings:EdOrgsRefreshIntervalInMins`, in the range 1–527040.
4. **Dynamic, classified inside the job:**
   - Discovery and token failures.
   - A tenant with no applicable credential: tenants are rows, and the reader returns `NotConfigured`, which is permanent.
5. **Configurations:**
   - **Bootstrap** (fresh install only): `WorkerEnabled=false`. The service starts without reader settings. Refresh POSTs and schedules enqueue `Pending` jobs that nothing executes. At startup the service logs `JobWorkersDisabled` (Warning): "Job workers are disabled on this instance; queued jobs run only on instances with JobSettings:WorkerEnabled=true." This state is documented as **not operational**.
   - **Operational:** `WorkerEnabled=true` with reader settings. An operator moves from bootstrap to operational by creating the projection claim set and application, setting the reader settings, enabling workers and restarting.
6. **Migration of existing deployments that run workers**, including managed-lifecycle deployments: before upgrading, create the projection claim set and application on the current release (the claim exists since migration `0039`), then add the reader settings. An upgrade that skips this step **fails at startup**, naming the missing settings. The failure is loud; workers are never silently disabled.
7. **Shipped configurations are repaired in the same commit that introduces rule 2** (step 5.1). **No compose default changes.**
   - The `JobSettings__WorkerEnabled: ${CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED:-true}` line stays as it is (`local-config.compose.yml` L57, `published-config.compose.yml` L61, and the azure-vm equivalent).
   - New reader variables (`CONFIGURATION_SERVICE_DMS_EDORG_*`) map onto `DmsEducationOrganizationProjectionSettings__*` with empty defaults.
   - **Explicit bootstrap only where a fresh install is created:**
     - The fresh-install templates (`eng/docker-compose/.env.example`, `eng/azure-vm/compose/.env.example`) gain an explicit `CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED=false`, with a comment: "bootstrap: no job runs here until you configure the DMS reader and set this to true".
     - The CMS-E2E environment (no DMS) sets it explicitly too.
   - An existing deployment's own `.env` is untouched, and an unset variable still means workers on.
   - So an existing deployment, with or without `CONFIGURATION_SERVICE_DATA_STORE_MANAGEMENT_ENABLED=true`, either starts operationally when reader settings are supplied, or **fails at startup**, naming the missing settings. It never becomes workers-off by accident.
   - Every start script and CI path that composes CMS is inventoried in step 5.1. Each one passes bootstrap explicitly or is wired as operational.
   - **Instance E2E:** fully operational (step 5.2).
   - **CMS E2E:** explicit bootstrap.

---

## 3. Scope, non-goals, boundaries

**In scope:**
- Snapshot persistence on both providers.
- Two refresh POSTs.
- One job type with its handler.
- Per-target lease coordination.
- Per-tenant schedule reconciliation.
- The tenant aggregate GET.
- The excluded-GET 404 guard.
- Deletion cleanup.
- Startup validation and deployment wiring (E2).
- An additive composite-error extension to DMS-1437 jobs (D3).
- Documentation and tests.

**Non-goals:**
- Writing to DMS.
- Validating applications against snapshots.
- CDC or a broker.
- Successful excluded GETs.
- Changes to reader internals.
- DMS-1439 locking changes.
- Parallel target processing.
- Any `src/dms`, mapping, DDL or `RelationalMappingVersion` change.
- Existing test-project DMS references.

**Ownership:** CMS owns orchestration, jobs, schedules, coordination, snapshots, aggregate responses, retries and diagnostics. DMS owns the projection, consumed only through `IEducationOrganizationProjectionReader.ReadAllAsync`.

## 4. Verified dependency facts

| # | Fact | Evidence |
| --- | --- | --- |
| F1 | `ReadAllAsync(EducationOrganizationProjectionReadRequest(string? TenantName, int DataStoreId, IReadOnlyDictionary<string,string> DataStoreContexts), ct)` returns `Success(Items, ContractVersion, PageCount, Restarts)` or `Failure(Detail)`. A failure carries no items. Only cancellation throws. A read is bounded by `TotalReadTimeoutSeconds`. | `EOP/IEducationOrganizationProjectionReader.cs` L11-64; `BoundedProjectionRequest.cs` L28-84 |
| F2 | Each item is `(long, string, string?, string, long?)`, ascending by ID, with names of at most 75 scalar values. | same, L58-64; Reader L75-83 |
| F3 | Failures are Transient or Permanent, with 22 codes. `ToJobErrorCode()` returns null for transient failures, and one of 13 registered `EdOrgProjection*` codes otherwise. Codes match `^[A-Za-z][A-Za-z0-9]{0,63}`; the 13 total 384 characters. | `EducationOrganizationProjectionFailure.cs` L9-151; `…FailureExtensions.cs` L24-56; `…JobErrorCodes.cs` L16-87; `BE/Jobs/JobErrorCodeRegistry.cs` L64 |
| F4 | The reader is registered unconditionally, with `ValidateOnStart`. The validator's early return is at L30-33. | `EOP/ServiceCollectionExtensions.cs` L23-72; `FE/…/WebApplicationBuilderExtensions.cs` L124-126 |
| F5 | `IJobEnqueuer.EnqueueAsync(…, null, ct)` autocommits before returning. Job IDs are `Guid "N"`. The tenant comes from the scoped provider. There is no dedup key. | `BE/Jobs/IJobEnqueuer.cs` L14-25; `PG/Repositories/JobRepository.cs` L33-101 |
| F6 | `IJobHandler<T>.ExecuteAsync(JobExecutionContext(JobId, TenantContext, Attempt, MaxAttempts, Fence), T, ct)`. The tenant is installed first. `ct` is cancelled on host stop and on job-lease uncertainty. A `JobPermanentException(JobErrorCode)` is terminal; anything else is transient. | `BE/Jobs/JobExecutionContext.cs` L14-20; `JobExecutor.cs` L92-268 |
| F7 | The fence locks the job row and validates the lease. Its deadline is `min(FenceTimeout, lease − 1 s)`, and each statement has a 5 s lock timeout. | `BE/Jobs/IJobFence.cs` L22; `PostgresqlJobFenceFactory.cs` |
| F8 | Error-code registration requires a message of 1–1000 characters with no control characters and no braces (`JobErrorCodeRegistrations.Add` L25-61). `IJobErrorCodeRegistry` exposes `TryGet` only (L78-79). Terminal failures write the **registry's** message for the code, never the caller's text (`JobExecutor.cs` L248-253). Exhaustion writes `AttemptsExhausted` without running the handler in the cases documented at docs L74-86. | — |
| F9 | Schedule upsert is keyed `(TenantId, ScheduleType)` in the scope's tenant. `Disable`, `DisableById` and `ListByType` exist, and the skip-while-active rule applies. `FK_JobSchedule_Tenant` is RESTRICT. No schedule exists in code. | `BE/Jobs/IJobScheduleService.cs`; `IJobScheduleRepository.cs` L20-25; PG `0033` L46, L64, L68-69 |
| F10 | `JobSettings`: `LeaseDuration` (default 5 min, 30 s–1 h); `RenewalInterval` (default 1 min, 12 s – lease/3); renewal timeout = interval/2; a startup inequality guarantees room for a late renewal. | `BE/Jobs/JobOptions.cs` L14-61; `JobOptionsValidator.cs` L27-129 |
| F11 | `DataStore.Id` is `INT`. Both delete paths use `DeleteDataStoreRows`, which deletes the row; children go by FK cascade. | PG/MS `DataStoreRepository.cs` L200-218 / L199-216 |
| F12 | The tenant catalog gate (PG `pg_advisory_xact_lock`; MS `sp_getapplock` Exclusive, Transaction owner; ≤5 s) is taken by: <br>• ordinary insert/update reservations (`DataStoreCatalog` L63/L89/L122); <br>• ordinary lock and delete (PG `DataStoreRepository.cs` L207, L481); <br>• managed create, publication, deletion, delete execution and retry outcome; <br>• managed acceptance (`DataStoreManageRepository.cs` L306). <br>Documented order: "job fence, tenant gate, sorted target reservations, managed aggregate, ordinary row". | `DataStoreCatalog.cs` L19-23 |
| F13 | The managed link is one-to-one and never dangling (FK NO ACTION plus a filtered unique index). Deletion unlinks first, so an orphaned management record has `DataStoreId IS NULL`. | `0041` PG L80, L119 / MS L69; `DataStoreManageDeletion.cs` L48-225 |
| F14 | `DataStoreContext` is unique on `(DataStoreId, ContextKey)`. | `0022_Create_DataStoreContext_Table.sql` PG L40-43, MS L21-22 |
| F15 | The FK-set test pins the FKs to `DataStore`. Respawn lists must include new tables. | `DataStoreManageDeletionTests.cs` L159-181; `DatabaseTestBase.cs` |
| F16 | The existing repositories decrypt connection strings, and the manage list is paged. Neither is reused. | PG `DataStoreRepository.cs` L289-401; `DataStoreManageQuery.cs` L14, L44 |
| F17 | Tenant middleware bypasses `/v3/tenants*`, with the missing-header text shown below. `GetTenantByName` is not tenant-scoped and is case-insensitive. The tenant provider is scoped and mutable. | `TenantResolutionMiddleware.cs` L28-129; `TenantRepository.cs` L132-136; `ITenantContext.cs` L12-28 |
| F18 | `TenantModule` is skipped in single-tenant mode. The EAT test L167-182 asserts that no `/v3/tenants*` route exists in that mode. | `WebApplicationExtensions.cs` L39-43 |
| F19 | An authorized `GET /v3/dataStores/edOrgs` returns 400 today, because `{id}` has no `:int` constraint. `/{id}/edOrgs` is unmapped. | `DataStoreModule.cs` L61, L112-116 |
| F20 | `MapSecuredPost` = Service + Admin; `MapSecuredGet` = Service + ReadOnlyOrAdmin. `JobQueuedResponse` exists. The EAT covers both IdPs and both tenancy modes. | `EndpointBuilderExtensions.cs`; `JobQueuedResponse.cs` |
| F21 | Compose: workers default to on, no reader settings, management off. Test hosts: all job services off. | compose L57; `appsettings.Test.json` L38-42; `JobClaimPolicy.cs` L22-23 |
| F22 | Live harness: `Tenant_255901` has 255901/2024 and 255901/2025, the latter seeded with 7 organizations. Per-tenant projection credentials are provisioned. `FaultInjectingHandler`/`PageFault` can replace or stall any page. The database server and engine come from environment variables. | `build-dms.ps1` L1960-1962; `Live/ProjectionE2EFixture.cs`; `Live/ReaderHost.cs` L34-158; `ProjectionE2EEnvironment.cs` L77-108 |
| F23 | The upstream aggregate is unfiltered by status (Deleted included). Unlinked or dangling management records are appended. | upstream `TenantService.cs`, `GetDataStoreManagesQuery.cs`, `DeleteInstanceJob.cs` |

Missing-header 400 text (F17): ``The 'Tenant' header is required when multi-tenancy is enabled``.

## 5. AC matrix

Status: **P** = provided by a dependency (verified and re-tested), **Part** = partially provided, **N** = new.

| AC | Status | Steps | Verification that establishes it |
| --- | --- | --- | --- |
| 1 | N | 1.1–1.5 | Schema tests on both providers. A duplicate `(store, generation, org)` is rejected. An active-generation read never contains duplicates. Snapshot = the active generation (D1). |
| 2 | N | 1.4/1.5, 2.4, 2.5 | The reader double records exactly the tenant's ordinary store IDs, in ID order, with an unmanaged store, a linked store and a pending unlinked manage record present. |
| 3 | N | 3.1, 3.3 | Unknown store or another tenant's store → 404, and the `dmscs.Job` count is unchanged (both providers). |
| 4 | Part | 3.1, 3.3 | 202. The exact Location `{PathBase}/v3/jobs/{jobId}` resolves under PathBase. The DB row is `Pending` with the same ID. EAT: read-only → 403, anonymous → 401 (both IdPs). The two messages are distinct and non-empty. |
| 5 | P | 3.1, 3.3 | Immediate GET → 200 `Pending` (both providers). |
| 6 | Part | 2.1, 5.1 | Startup refuses, naming the setting, for each of: <br>• a blank base URL with partial credentials; <br>• a blank base URL with an invalid bound; <br>• an unsupported version; <br>• a worker replica without a base URL or credential; <br>• an invalid interval. <br>A worker-off replica starts and logs `JobWorkersDisabled`. A valid static configuration followed by a dynamic Discovery or token failure gives a classified job outcome. |
| 7 | Part | 2.1, 2.3–2.5 | Sentinel scan after a refresh. Sentinels: client secret, token, connection string, cursor, page body. Scanned: every column of `dmscs.Job`, `…SnapshotTarget`, `…Snapshot`, and captured logs. |
| 8 | Part | 2.2–2.5, 5.4 | Refresh-one with `TargetSchemaIncompatible` → snapshot kept, `errorMessage` = that registered text. Refresh-all with **all 13 permanent codes plus both refresh categories** → snapshots kept, every category code present, length ≤ 1000. Also two-code and permanent+transient mixes. |
| 9 | Part | 1.2/1.3, 2.3, 2.5, 5.4 | {A,B,C}→{B,D}. Empty success → `[]`. Exact retained generation IDs after abandonment, with and without a published generation. Other tenants' and stores' rows identical before and after. Live: 7 organizations over 4 pages. |
| 10 | Part | 2.3, 2.5, 5.4 | Classification table. Real executor: <br>• signalled cancellation and target-lease loss during a read; <br>• refused fence after staging; <br>• prune failure after activation (published, job not failed). <br>Live: timeout, unavailable DMS, later-page failure. |
| 11 | N | 2.2, 2.4, 2.5, 5.4 | Stores 1 OK / 2 fail / 3 OK → 1 and 3 published, 2 kept, terminal `Error` with a composite; store 3 is attempted after store 2 fails. Live mixed case. **Stated limitation:** exhaustion without a handler run gives `AttemptsExhausted` (F8), tested. |
| 12 | N | 1.2/1.3, 2.3, 2.5 | Publication order equals lease order. A stale owner cannot stage or activate. A recovered execution has a new token and cannot read while the old lease is live. |
| 13 | Part | 2.1, 2.3, 2.5, 4.1, 4.2, 5.1, 5.2 | **Overlap tests** (signalled): <br>• A paused after acquisition, expired, B enters, A resumed → A never enters the reader. <br>• A paused after admission → A's read cancelled at its next renewal, and its stage and activation refused. <br>• Lease taken during a read → cancelled. <br>• Lease expires during a lock wait → not revived. <br>**Reconciliation:** race, failure and mode-switch tests. <br>**Execution conditions:** management disabled; no `HttpContext`. <br>**Operational:** jobs linked by `SourceScheduleId`, and a *later* occurrence publishes changed source data. <br>**Configuration preservation:** an existing worker configuration is never rendered workers-off. |
| 14 | N | 3.2, 3.3 | Authorized excluded GETs → 404. Anonymous → 401. Multi-tenant without a header → 400. `GET /v3/dataStores/1` unchanged. |
| 15 | N | 3.2, 3.3 | EAT (both IdPs). No lookup on a missing or mismatched header. A resolution probe sees `Multitenant` and never runs on 400 or 404. The request scope stays `NotMultitenant`. |
| 16 | N | 3.2, 3.3 | `default` in any case → 200 without a header. Other names → 404. A supplied header is ignored. |
| 17 | N | 1.4/1.5, 3.2, 3.3 | Out-of-order store IDs. Org IDs 2, 9, 10, 2³¹+1. Explicit nulls. Full-body comparison. |
| 18 | N | 1.4/1.5, 3.3 | Linked/pending/Deleted ordering. FK-proven non-dangling links. Real read/write interleavings. |
| 19 | Part | 2.2, 2.3, 3.2, 3.3 | Composite text resolved from the registry (forged-message test). Full HTTP sentinel scan. Cross-tenant isolation test. |
| 20 | Part | 1.1–1.5, 3.3 | `9007199254740993` and `long.MaxValue − 1` round-trip exactly (raw JSON text). |
| 21 | N | 1.1, 1.4/1.5, 3.3 | Ordinary and managed deletion cascade, plus rollback. Delete-first and refresh-first orderings at acquisition and at staging. The aggregate shows the state before or after a delete, never a mix. |
| 22 | P (production) | all | `BackendProjectBoundaryTests` stays green. No new DMS references. |

## 6. Architecture and behavior

### 6.1 Persistence (migration `0042_Create_EducationOrganizationSnapshot_Tables.sql`, both providers)

**`dmscs.EducationOrganizationSnapshotTarget`** (one row per ordinary store that has ever been refreshed):

| Column | PG | MS | Notes |
| --- | --- | --- | --- |
| `DataStoreId` | `INT` PK | `INT` PK | FK → `DataStore(Id)` **ON DELETE CASCADE** |
| `TenantId` | `BIGINT NULL` | `BIGINT NULL` | FK → `Tenant` NO ACTION; `IX_…_TenantId` |
| `LastGeneration` | `BIGINT NOT NULL` | `BIGINT NOT NULL` | Incremented by each acquisition |
| `ActiveGeneration` | `BIGINT NULL` | `BIGINT NULL` | The published snapshot; CK `<= LastGeneration` |
| `RefreshedAt` | `TIMESTAMP NULL` | `DATETIME2 NULL` | Statement clock at activation |
| `SourceContractVersion` | `VARCHAR(100) NULL` | `NVARCHAR(100) NULL` | — |
| `LeaseOwner` | `VARCHAR(32) NULL` | `NVARCHAR(32) NULL` BIN2 | Execution token (`Guid N`) |
| `LeaseGeneration` | `BIGINT NULL` | `BIGINT NULL` | — |
| `LeaseExpiresAt` | `TIMESTAMP NULL` | `DATETIME2 NULL` | CK: all three lease columns null, or all set |
| `CreatedAt`, `LastModifiedAt` | per convention | per convention | — |

**`dmscs.EducationOrganizationSnapshot`:**

| Column | PG | MS |
| --- | --- | --- |
| `DataStoreId` | `INT` | `INT` |
| `Generation` | `BIGINT` | `BIGINT` |
| `TenantId` | `BIGINT NULL` | `BIGINT NULL` |
| `EducationOrganizationId` | `BIGINT` | `BIGINT` |
| `NameOfInstitution` | `VARCHAR(256)` | `NVARCHAR(256)` |
| `ShortNameOfInstitution` | `VARCHAR(256) NULL` | `NVARCHAR(256) NULL` |
| `Discriminator` | `VARCHAR(128)` | `NVARCHAR(128)` |
| `ParentId` | `BIGINT NULL` | `BIGINT NULL` |
| `RefreshedAt` | `TIMESTAMP` | `DATETIME2` |
| `SourceContractVersion` | `VARCHAR(100)` | `NVARCHAR(100)` |

- PK `(DataStoreId, Generation, EducationOrganizationId)`.
- FK → `Target(DataStoreId)` **ON DELETE CASCADE**, so there is one cascade path: DataStore → Target → rows.
- `TenantId` follows CMS convention (it is copied from the target row).
- `DeleteDataStoreRows` is unchanged.

### 6.2 Target coordination protocol (AC9, 10, 12, 13)

**Definitions:**
- **Clock.** Every lease comparison and assignment uses the **statement clock**: PG `clock_timestamp()` (not the transaction-start `now()`), MS `SYSUTCDATETIME()`. No application-server time is used.
- **Execution token.** Each handler execution generates one `token = Guid.NewGuid():N`. A reclaimed job attempt gets a new token.
- **Lease timing** reuses the validated `JobSettings` values (F10). Lease duration `D` = `LeaseDuration`; renewal interval `R` = `RenewalInterval`; renewal timeout = `R/2`.
- **Lock timeout.** Every refresh transaction sets a lock timeout: PG `lock_timeout`, MS `LOCK_TIMEOUT`. It is `R/2` for renewals and 5 s for everything else.
- **Lock, then validate (LTV).** Every operation whose decision depends on lease ownership or expiry runs in one bounded transaction, in three parts:
  1. **Lock.** Lock the target row in its own statement, with no time predicate: PG `SELECT … FOR UPDATE` / `FOR SHARE`; MS `SELECT … WITH (UPDLOCK | HOLDLOCK, ROWLOCK)`.
  2. **Validate.** Once the lock is held, read `clock_timestamp()` / `SYSUTCDATETIME()` together with the locked row's lease columns in a **separate statement**, and decide.
  3. **Write.** Perform the write, using that fresh clock value.
  
  Why a separate statement:
  - PostgreSQL evaluates the `WHERE` clause and select list of a `SELECT … FOR UPDATE` before waiting for a row lock whose holder releases it unchanged (`PostgresqlJobFenceFactory.cs` L39-41).
  - SQL Server evaluates `SYSUTCDATETIME()` once per statement, at the start of that statement.
  
  So a single conditional `UPDATE … WHERE LeaseExpiresAt > clock` can carry an expiry verdict made before the wait. LTV prevents this. The steps that use LTV are acquisition (2), reclaim (3), admission and every renewal (4, 5, 6), stage (7) and activation (8).

**Sequence for one target:**

1. **Resolve contexts** for the target (§6.7), inside the job scope.
2. **Acquire.** One transaction, with one locked decision.
   - PostgreSQL:
     ```sql
     SELECT 1 FROM dmscs."DataStore" WHERE "Id"=@Id AND {TenantWhereClause} FOR KEY SHARE;
     -- no row → DataStoreNotFound
     INSERT INTO …Target ("DataStoreId","TenantId","LastGeneration") VALUES (@Id,@TenantId,0) ON CONFLICT DO NOTHING;
     SELECT … FROM …Target WHERE "DataStoreId"=@Id FOR UPDATE;
     ```
   - SQL Server:
     ```sql
     SELECT 1 FROM dmscs.DataStore WITH (HOLDLOCK, ROWLOCK) WHERE Id=@Id AND {TenantWhereClause};
     -- no row → DataStoreNotFound
     SELECT … FROM …Target WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE DataStoreId=@Id;
     -- absent → INSERT the row with the lease (the key-range lock from HOLDLOCK serializes concurrent inserts)
     ```
   - Decision on the locked row (LTV: fresh clock read **after** the lock statement):
     - `LeaseOwner IS NOT NULL AND LeaseExpiresAt > clock` → **`Busy`**. No update and no insert attempt; commit, which releases the locks.
     - Otherwise: `LastGeneration += 1`, `LeaseOwner = token`, `LeaseGeneration = LastGeneration`, `LeaseExpiresAt = clock + D` → **`Acquired(g)`**.
   - On SQL Server, when the target row is absent, the decision is to insert. The key-range lock that `HOLDLOCK` took on the missing key makes a concurrent acquirer wait. That acquirer then locks the inserted row and sees a live lease, giving `Busy`.
   - Lock order: `DataStore` (key-share / S) → target (update / X).
3. **Reclaim.** A separate transaction, using LTV:
   - Lock the target row (`FOR UPDATE` / `UPDLOCK, ROWLOCK`).
   - Validate with a fresh clock: `LeaseOwner = token AND LeaseGeneration = g AND LeaseExpiresAt > clock`; otherwise **`LeaseLost`**.
   - `DELETE` the snapshot rows with the null-safe predicate:
     ```sql
     DataStoreId = @Id
     AND Generation <> @g
     AND (ActiveGeneration IS NULL OR Generation <> ActiveGeneration)  -- ActiveGeneration read from the locked row
     ```
   - On SQL Server the `DELETE` runs `WITH (ROWLOCK)` in statements of at most 4,000 rows, all in this transaction (see step 7). Each statement chooses its keys with a `FORCESEEK` subquery inside the data store's key range and reaches the target rows by nested loops on the full primary key (`OPTION (LOOP JOIN)`). A plain batched delete got a clustered index scan plan, which waited on another data store's locked rows, and SQL Server refuses `FORCESEEK` on a delete's target. The statements that lock or read a target or data store row also use `FORCESEEK`.
   - No other execution can publish those generations: every stage and activation requires the live lease.
4. **Admission.** A strict renewal using LTV, in one transaction with a lock timeout of `R/2`:
   - Lock the row.
   - Validate `LeaseOwner = token AND LeaseGeneration = g AND LeaseExpiresAt > clock`, with the clock read after the lock is held.
   - If valid, `UPDATE … SET LeaseExpiresAt = clock + D`, then commit. The result is admitted.
   - An invalid lease → `LeaseLost`.
   - An exception, lock timeout or unknown commit outcome → `LeaseLost` (uncertain).
   - **This is the last operation before `ReadAllAsync`. Nothing is awaited between its completion and the reader call.**
   - An expired or taken-over lease is never revived. That includes a lease that expired while this renewal was waiting for the row lock.
5. **Read with renewal.**
   - Create `targetCts = CancellationTokenSource.CreateLinkedTokenSource(ct)` and start a renewal loop on a `TimeProvider` timer every `R`. Each renewal is the step 4 transaction, with the `R/2` timeout.
   - A renewal that returns 0 rows, throws or times out cancels `targetCts`.
   - Call `ReadAllAsync(request, targetCts.Token)`.
   - Read budget: the reader's own `TotalReadTimeoutSeconds` bounds the read. The lease stays alive while the owner is healthy, and losing the lease ends the read. The budget does not depend on when the lease was acquired.
   - Classification of an `OperationCanceledException`:
     - when `ct` is cancelled → propagate (DMS-1437 semantics);
     - when only `targetCts` is cancelled → **`LeaseLost`**.
   - When the read returns, the loop is stopped and awaited before the next step, so no renewal overlaps staging. This orderly shutdown is tested.
6. **Pre-stage renewal.** A strict renewal as in step 4 (LTV); failure → `LeaseLost`.
7. **Stage.** One transaction, using LTV:
   - Share-lock the target row (PG `FOR SHARE`, MS `HOLDLOCK, ROWLOCK`).
   - Then require the live lease, as in step 3, with a fresh clock.
   - Insert the items: PG `unnest` typed arrays; MS `INSERT … WITH (ROWLOCK) SELECT … FROM OPENJSON(@Items)` statements of at most 4,000 rows, all in the staging transaction. JSON identifiers are read as `BIGINT` and text as `nvarchar(max)`, so an over-long value fails the insert instead of being truncated. Foreign key and check constraints are enforced as for any insert and stay trusted.
   - **SQL Server staging change (approved during step 1.3).** `SqlBulkCopy` was replaced. Without a table lock it loads by page: a paused 50,000-row bulk copy held about 3,200 exclusive page locks, which blocked another data store's reclaim until its lock timeout (`1222`). `ROWLOCK` sets only the initial lock granularity; SQL Server can still escalate to a table lock, for example under lock-memory pressure. Statements of at most 4,000 rows stay below the per-statement escalation threshold, which reduces that risk without ruling it out. Contention ends in the bounded lock waits and failure handling of §6.12.
   - The share lock blocks any takeover until commit, because takeover needs the update/X lock, so staging cannot interleave with another owner's reclaim. If the lease passes expiry during staging, the next owner takes over after the commit and reclaims these rows, and this execution's activation is refused.
   - An empty set stages nothing.
8. **Activate.** Inside `context.Fence.ExecuteAsync`, O(1), using LTV:
   - Lock the target row (PG `FOR UPDATE`, MS `UPDLOCK, ROWLOCK`).
   - Missing row → `DataStoreRemoved`.
   - Then, with a fresh clock in a separate statement: lease mismatch or expiry → `LeaseLost`.
   - Otherwise `ActiveGeneration = g`, `RefreshedAt = clock`, `SourceContractVersion`, and the lease is cleared.
9. **Prune.** Own transaction, best effort: delete rows with `Generation < ActiveGeneration`. A failure is logged and never changes the outcome. On SQL Server it deletes like reclaim (step 3), in statements of at most 4,000 rows, all in this transaction.
10. **Release.** On every exit after `Acquired` without activation; best effort, with `CancellationToken.None` and a 5 s timeout. Clears the lease where `LeaseOwner = token`.

**Guarantees:**

- **Publication** is exclusive and ordered. Only the live lease holder can stage or activate. A new owner reclaims every unpublished generation. Publication order equals acquisition order, which equals read order. A stale completion can never overwrite a newer snapshot.
- **Publication after takeover (unconditional and tested).** Once another execution has acquired the target, the previous owner can never stage, activate or reclaim. Every such step validates ownership with a fresh clock, after taking the row lock, in the same transaction as its write. Activation additionally runs inside the job fence.
- **Reader admission (an operational guarantee, under a stated failure model).**
  - An execution calls `ReadAllAsync` only directly after a successful strict renewal. At that moment the lease is unexpired, with `D` remaining.
  - Another execution can acquire the target only after that lease expires by statement clock, or after it is released.
  - Overlapping reads therefore need **an execution or scheduling delay longer than `D`** (default 5 min) between the admission renewal and the reader's requests. Examples: a continuation or thread descheduled, a process suspension, or late delivery of a timer or a cancellation. No await sits between admission and the call, but that does not make the interval atomic.
  - **Under normal scheduling,** the delayed execution's next renewal (scheduled every `R`, timeout `R/2`) fails and cancels its read. Under starvation or suspension, timers and cancellation delivery have no unconditional wall-clock bound, so no hard upper bound on the overlap is claimed.
  - Any such overlap is read-only: DMS reads have no side effects, and the delayed execution cannot publish (guarantee above).
- **Cancellation.**
  - Host stop or job-lease loss cancels `ct`, so the read stops and the lease is released (or expires).
  - Target-lease loss cancels only that target's read, and the outcome is `LeaseLost`.
- **Storage.** At most **2 generations per target**: the active one, plus the current owner's in-progress generation. A failed prune leaves the previous generation until the next reclaim, which removes it before any new staging.

### 6.3 Job type and payload

- **Job type** `EducationOrganization.Refresh`, payload v1: `sealed record EducationOrganizationRefreshPayload(EducationOrganizationRefreshScope Scope, int DataStoreId)`, with scope `{All, One}`. Validator: `All` ⇒ `DataStoreId` is `0`; `One` ⇒ `DataStoreId > 0`.
- **Schedule type** `EducationOrganization.ScheduledRefresh`, payload `{All, 0}`.
- **Registered codes** (fixed text, ≤1000 characters, no braces; wording is not an acceptance criterion):
  - `EdOrgRefreshDataStoreNotFound` — "The data store no longer exists; its education organizations were not refreshed."
  - `EdOrgRefreshTemporarilyUnavailable` — "DMS, the data store, or the Configuration Service database was temporarily unavailable."
  - `EdOrgRefreshTargetBusy` — "The data store was being refreshed by another job."
  - **Composite** `EdOrgRefreshIncomplete` — "Education organizations could not be refreshed for one or more data stores, whose previously refreshed education organizations were kept." Its allowed categories are the 13 `EdOrgProjection*` codes, `EdOrgRefreshTemporarilyUnavailable` and `EdOrgRefreshTargetBusy`.

### 6.4 Refresh routes (new `EducationOrganizationModule : IEndpointModule` plus an `OpenApiModuleTags` entry)

| Route | Helper | Behavior |
| --- | --- | --- |
| `POST /v3/dataStores/edOrgs/refresh` | `MapSecuredPost` | Enqueue `{All,0}` with a null transaction (autocommit). Respond `202` with `Location: {PathBase}/v3/jobs/{jobId}` and body `{ jobId, message: "Education organizations refresh has been queued for all instances" }`. |
| `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh` | `MapSecuredPost` | `int` route binding; a non-integer gets the existing 400. A tenant-scoped existence check, which reads no connection string; absent → `404 "Data store not found."` with no job. Otherwise enqueue `{One,id}` → `202` with message `"…for the specified instance"`. |

In multi-tenant mode the existing tenant middleware applies; in single-tenant mode a `Tenant` header is ignored. A failed existence check or a failed enqueue returns 500 with no `Location` and logs `RefreshRequestFailed` (§6.11). `Location` is relative, `{PathBase}/v3/jobs/{jobId}`, as in the pinned contract. Both routes carry the pinned `DataStores` tag. OpenAPI declares 202 + Location, 400, 401, 403, 404 (per-store route only) and 500, with `dataStoreId` as an `int32` path parameter.

### 6.5 Handler (`EducationOrganizationRefreshJobHandler`)

1. **Resolve targets** in the job scope (§6.7): all of the tenant's stores ordered by `Id`, or the single one.
   - `One` and absent → `JobPermanentException(EdOrgRefreshDataStoreNotFound)`.
   - `All` and none → return.
   - A Configuration Service database failure of the lookup is a **transient** failure, and no target is attempted. It is decided like every other failure in step 3: a retry while attempts remain; on the final attempt `EdOrgRefreshTemporarilyUnavailable` for `One`, and for `All` the composite listing only `EdOrgRefreshTemporarilyUnavailable`. The log entry records that the lookup failed, with its diagnostic.
2. **Process each target sequentially** (Q4): `ct.ThrowIfCancellationRequested()`, then run §6.2. Outcomes: `Refreshed | Removed | Busy | LeaseLost | Failed(projectionFailure) | DbFailure`.
   - `Removed` (`DataStoreNotFound` at acquisition, or `DataStoreRemoved` later) is not a failure for `All`. For `One` it is `EdOrgRefreshDataStoreNotFound`.
   - A caller-token `OperationCanceledException`, `JobLeaseLostException` or `JobFenceUnavailableException` propagates immediately, after a best-effort release.
3. **Decide**, after every target has been processed (AC11):
   - **No failures** → return (`Completed`).
   - **Else, any transient failure** (`Busy`, `LeaseLost`, `DbFailure`, a reader `Transient`, a failed target lookup) with `Attempt < MaxAttempts` → throw `EducationOrganizationRefreshRetryException` (transient).
   - **Else terminal:**
     - `One` → the single code: `failure.ToJobErrorCode()` for a permanent failure; `EdOrgRefreshTemporarilyUnavailable` or `EdOrgRefreshTargetBusy` for a transient one.
     - `All` → `JobPermanentException(EdOrgRefreshIncomplete, categories)` (§6.6).
4. **Retry semantics: at-least-once.** Every attempt re-reads every target under target coordination, including targets an earlier attempt or another job already published. Re-reading is intentional and safe: §6.2 orders publications by lease. There is no freshness skip and no completion marker.
5. **No HTTP context** is used.
6. **Limitations (stated, tested):**
   - DMS-1437 exhaustion without a handler run gives `AttemptsExhausted` and no composite. Earlier publications stay.
   - If a committed activation's acknowledgement was lost and no attempt remains, the job shows `AttemptsExhausted` although data was published.
   - The documentation says `Error` means "not confirmed complete".

### 6.6 Composite error contract (DMS-1437 additive extension; D3)

**Registration** — `AddJobCompositeErrorCode(string code, string message, IReadOnlyList<string> categoryCodes)`:
- The code and message follow the existing rules.
- Each category code must be registered (consumer or infrastructure), distinct, and not itself composite.
- Validation is **eager**: `AddJobCompositeErrorCode` validates when it is called, during service registration, so startup fails before the host is built. The `IJobErrorCodeRegistry` singleton is built on first resolution, which not every replica performs, so a check deferred to its construction would not run on every replica.
- **Registration order:** every category code is registered before the composite that lists it. A category registered later is refused as unknown.
- The call **fails** unless the worst case fits:
  `message.Length + " Problem categories: ".Length + Σ len(categoryCodes) + 2×(n−1) + ".".Length ≤ 1000`.
- `AddEducationOrganizationRefresh` follows the order: it first calls the idempotent `AddDmsEducationOrganizationProjectionReader`, which registers the 13 `EdOrgProjection*` codes, then registers the refresh's single codes, and only then the `EdOrgRefreshIncomplete` composite.

**Raising** — `JobPermanentException(JobErrorCode errorCode, IReadOnlyList<JobErrorCode> categories)` (an additive constructor). Only `.Code` values are read; caller `.Message` text is **never** used.

**Composition** — the executor's terminal path calls `IJobErrorCodeRegistry.TryCompose(string primaryCode, IReadOnlyList<string> categoryCodes, out string message)`, a new member.

- **Fail closed.** The primary must be a registered composite. Every category must be in its allowlist. The list must not be empty. Any violation → `HandlerFailed`, logged as `JobCompositeErrorRejected` (the code names are sanitized).
- **Categories:** de-duplicated in first-occurrence order.
- **Text:**
  `registry[primary].Message + " Problem categories: " + string.Join(", ", categories) + "."`
  Then, for each category in order, append `" " + registry[category].Message` only while the total stays ≤ 1000.
- **Guarantees:**
  - Every category code is always present.
  - Length ≤ 1000 for any registered primary, by the startup check.
  - Content is registry text only.
  - Deterministic.
- **The single-code path is unchanged byte for byte.**

**Refresh capacity** (computed): primary 137 + 21 + 440 (15 category codes) + 28 + 1 = **627** ≤ 1000. The registration check and a test with all 15 categories both enforce it.

**Refresh-all category order:** distinct codes in ascending order of the data store ID that first produced them.
- Permanent → `ToJobErrorCode()`.
- Reader-transient, DB failure, lease lost or a failed target lookup → `EdOrgRefreshTemporarilyUnavailable`. A failed lookup attempts no data store, so it is the only category.
- Busy → `EdOrgRefreshTargetBusy`.

**Classification:**

| Source | Category | Snapshot | Job effect |
| --- | --- | --- | --- |
| Reader `Success` | — | Published | — |
| Reader `Failure` Transient | Transient | Kept | Retry, then `TemporarilyUnavailable` |
| Reader `Failure` Permanent | Permanent | Kept | Its code |
| `Busy` | Transient | Kept | Retry, then `TargetBusy` |
| `LeaseLost` / CMS DB failure before activation | Transient | Kept | Retry, then `TemporarilyUnavailable` |
| CMS DB failure of the target lookup | Transient | Kept (no target attempted) | Retry, then `TemporarilyUnavailable` (`One`) or the composite listing it (`All`) |
| Prune or release failure after activation | none | Published | Ignored (logged) |
| Fence refusal / job-lease loss / caller cancellation | DMS-1437 | Kept unless already activated | No outcome write, or release |
| Store removed | — | Cascaded | `All`: ignored; `One`: `DataStoreNotFound` |

### 6.7 Read models (`IEducationOrganizationSnapshotRepository`, PG + MS)

- **`GetRefreshTargetsAsync(int? id, ct)`** — `Id` plus its contexts (unique per F14). Tenant-scoped, ordered by `Id`. **Never selects `ConnectionString`.**
- **`DataStoreExistsAsync(int, ct)`** — tenant-scoped.
- **`GetTenantAggregateAsync(ct)`** — the read boundary differs by provider:
  - **PostgreSQL:** `BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY`. One snapshot for every statement. It blocks no writer and takes no gate.
  - **SQL Server:** one transaction.
    - (i) `AcquireTenantGateAsync(…, shared: true)` — a new shared mode, `sp_getapplock @LockMode='Shared'`. Writers take the gate exclusively (F12), so ordinary rows and managed links cannot change until commit.
    - (ii) Read the target rows `WITH (HOLDLOCK, ROWLOCK)`, inside statement (a), by `FORCESEEK` on the target's primary key for each data store. The shared locks on the clustered rows are held to commit. Activation's update lock is compatible with them, but its **exclusive modification** must wait, so `ActiveGeneration` cannot change during the read.
    - (iii) The statements below, in order. Statement (b) takes (a)'s active `(DataStoreId, Generation)` pairs as an `OPENJSON` parameter and reaches the rows `WITH (FORCESEEK)`, so it seeks each active generation's key range instead of scanning the table; the 1.5 tests observe that it does not wait for a concurrent large stage, reclaim or prune.
- **Statements:**
  - (a) Ordinary stores `LEFT JOIN DataStoreManage m ON m.DataStoreId = ds.Id`, plus their targets' `ActiveGeneration`.
  - (b) Rows for the active `(DataStoreId, Generation)` pairs, ordered by `(DataStoreId, EducationOrganizationId)`.
  - (c) Manage records with `DataStoreId IS NULL` (all statuses, D5), ordered by `Id`. By F13 this is exactly the unlinked and orphaned set.
- **Mapping:** `databaseTemplate = Template`; `databaseName = HistoricalDataStoreId is null ? null : TargetDatabaseName` (the existing manage API rule).
- **Semantics:** a concurrent catalog write or publication lands either wholly before or wholly after the read.
  - On SQL Server, a catalog writer waits for the gate up to its 5 s bound, then fails with its existing response and rolls back (§6.12).

### 6.8 Tenant aggregate GET and the excluded-GET guard

`GET /v3/tenants/{tenantName}/dataStores/edOrgs` is mapped with `MapSecuredGet` in both tenancy modes. The EAT tenant-gating test excludes this pattern and asserts that it exists in all four fixtures.

**Multi-tenant mode**, after authorization:
1. A missing or whitespace `Tenant` header → 400 with the middleware's exact body (a shared constant, `TenantResolutionMiddleware.MissingTenantHeaderMessage`, written by the same `FailureResponseWriter`).
2. Header ≠ path (OrdinalIgnoreCase) → 400 "The 'Tenant' header does not match the tenant name in the route.", with **no lookup**.
3. `GetTenantByName` → NotFound: 404 "Tenant not found."; Unknown: 500, logging `TenantAggregateReadFailed` without the failure's message.
4. Create a nested `AsyncServiceScope` and set its provider to `Multitenant(id, canonicalName)`. Only then resolve the repository from it.

**Single-tenant mode:** `default` (any case) is accepted; any other name → 404. Uses a nested scope with `NotMultitenant`. Any header is ignored.

**Response:**
- Shape: `{ id, name, dataStores[{ id, dataStoreManageId, name, dataStoreType, status, databaseTemplate, databaseName, educationOrganizations[] }] }`.
- Organizations: `{ educationOrganizationId, nameOfInstitution, shortNameOfInstitution, discriminator, parentId }`.
- Unmanaged store: `status: "Created"` and the manage fields null.
- Nulls are written explicitly.
- No internal metadata.
- A failed aggregate read → 500, logging `TenantAggregateReadFailed`.
- OpenAPI declares 200, 400, 401, 403, 404 and 500. The schemas are `TenantDetailsResponse`, `TenantDataStoreResponse` and `EducationOrganizationResponse`, and the operation carries the module's `DataStores` tag rather than the pinned `Tenants`: both are documented difference (d) in E1. A second tag would break the one-tag-per-operation rule, and `TenantModule`'s tag exists only in multi-tenant mode.

**Excluded-GET guard:** `MapSecuredGet("/v3/dataStores/edOrgs")` → 404 with the unknown-route body, excluded from OpenAPI. `/v3/dataStores/{id}/edOrgs` stays unmapped.

### 6.9 Scheduling (`EducationOrganizationRefreshScheduleReconciler : BackgroundService`)

Added as a hosted service by `AddEducationOrganizationRefresh` when `JobSettings:SchedulerEnabled=true`; independent of `EnableDataStoreManagement`. It runs at startup, then 5 minutes after each run ends (`TimeProvider`). It needs `JobRuntimeEnvironment` for the tenancy mode.

**Upsert:**
- **Multi-tenant:** page through all tenants, 100 at a time in identifier order. For each tenant, create a scope with `Multitenant(id, name)` installed before the schedule service is resolved, and call `UpsertAsync(ScheduledRefresh, Refresh, 1, {All,0}, interval, RunFirstOccurrenceImmediately: true)`.
- **Single-tenant:** one upsert with `NotMultitenant`.
- If enumeration fails (a failed result or an exception), what was already upserted stands, and **nothing is disabled**. A failed upsert for one tenant is logged and the others continue; it does not stop the disable phase, because no disable decision depends on the enumeration.
- If the schedules cannot be listed, nothing is disabled.

**Disable** — each enabled schedule of the type is decided on its own:

| Mode | Schedule tenant | Decision |
| --- | --- | --- |
| Multi-tenant | null | `DisableById` |
| Multi-tenant | non-null | Fresh `GetTenant(id)`: NotFound → disable; Success → keep; Unknown → keep |
| Single-tenant | non-null | Disable |

Every disable keeps history.

**Failures:** logged as `EducationOrganizationScheduleReconciliationFailed` (Warning: operation, tenant ID, exception type chain, provider code; never a message) and retried at the next run. They never stop the host or affect dispatch of existing schedules. Each run ends with `EducationOrganizationScheduleReconciled` (Info: counts and whether the disable phase was skipped).

**Documented:**
- Reconciliation is periodic: each run starts 5 minutes after the previous run completed, so a new tenant is scheduled by a later run, not within a fixed time. Enumeration and upserts add to that delay, and a failed run defers the tenant to the next one.
- All replicas must use the same `MultiTenancy` value.
- A replica with the scheduler on and workers off logs `JobWorkersDisabled` (E2).

### 6.10 Configuration

| Setting | Default | Validation |
| --- | --- | --- |
| `AppSettings:EdOrgsRefreshIntervalInMins` (env `AppSettings__EdOrgsRefreshIntervalInMins`) | `60` | 1–527040, `ValidateOnStart` |
| `DmsEducationOrganizationProjectionSettings:*` | unchanged | E2 rules 1–2 |
| Compose: new `CONFIGURATION_SERVICE_DMS_EDORG_*` variables mapped onto the reader settings (empty defaults). The `CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED` default stays `true`. Bootstrap is set explicitly only in the fresh-install `.env.example` files and in the CMS-E2E environment. | — | step 5.1 |

### 6.11 Observability

Every event uses its event name as the `EventId` name. Values are sanitized. URLs, tokens, cursors, bodies, payloads, exception messages and connection data are never logged.

| Event | Level | Notes |
| --- | --- | --- |
| `EducationOrganizationTargetRefreshed` | Info | tenant, dataStoreId, generation, itemCount, contractVersion, pageCount |
| `…TargetBusy` | Info | — |
| `…TargetLeaseLost` | Warning | with reason: `Expired` / `Taken` / `RenewalUncertain` |
| `…TargetRemoved` | Info | — |
| `…TargetRefreshFailed` | Warning | category, code, stage, httpStatus, problemType, correlationId, pagesRead |
| `…RefreshFinished` | Info / Warning | counts and decision |
| `…SnapshotPruneFailed` | Warning | exception type chain and provider code only |
| `…LeaseReleaseFailed` | Warning | exception type chain and provider code only |
| `…ScheduleReconciled` | Info | — |
| `…ScheduleReconciliationFailed` | Warning | — |
| `JobCompositeErrorRejected` | Warning | — |
| `JobWorkersDisabled` | Warning | at startup |
| `RefreshRequestFailed` | Error | a refresh route's failed existence check or enqueue: request, exception type chain, provider code, operation, trace ID |
| `TenantAggregateReadFailed` | Error | the tenant aggregate's failed tenant lookup or read: request, exception type chain, provider code, operation, trace ID |

### 6.12 Locking, ordering and bounded failure (both providers)

| Operation | Lock order | Bound / failure behavior |
| --- | --- | --- |
| Ordinary DELETE (HTTP) | gate X → `DataStore` X → cascade target X → rows X | Gate ≤ 5 s, then `TimeoutException` → existing `FailureUnknown` → **500**, rolled back, **no automatic retry** (the client may retry) |
| Ordinary POST/PUT (HTTP) | gate X (reservations) → rows | Same: existing failure response, rolled back, no automatic retry |
| Managed create/publication/delete (jobs) | job fence → gate X → … → `DataStore` → cascade | Existing DMS-1437/DMS-1439 transient handling |
| Refresh acquire | `DataStore` key-share/S → target X | 5 s lock timeout; error → `DbFailure` (transient) |
| Refresh reclaim | target X → rows X | same |
| Refresh renewal | target X (single `UPDATE`) | `R/2` timeout → `LeaseLost` |
| Refresh stage | target S → rows X (FK check on target) | 5 s; error → `DbFailure` |
| Refresh activate | job row (fence) → target X | F7 |
| Refresh prune | rows X | best effort |
| Aggregate read (MS) | gate S → target S (held) → reads | gate wait ≤ 5 s → 500 |
| Aggregate read (PG) | snapshot only, no locks | — |

- The order is designed so that each refresh operation takes `DataStore` before target, and target before rows. The delete path takes the same direction.
- Deadlocks are **not ruled out**: SQL Server lock escalation, key-range locks and FK checks can produce cycles the design does not foresee. Every refresh step treats deadlock (PG `40P01`, MS `1205`) and lock timeout (PG `55P03`, MS `1222`) as `DbFailure` (transient) and rolls back.
- HTTP catalog writers keep their existing error behavior.

**Tests:** blocked HTTP DELETE and POST while an aggregate read holds the shared gate past its bound (via a seam) → 500, state unchanged; the same requests after release succeed. A managed job blocked the same way retries per DMS-1437. Refresh deadlock and timeout classification is driven by forced errors.

### 6.13 Compatibility and deployment

- Additive migrations.
- New job type rollout: every worker must run this release before any instance enqueues.
- Behavior changes:
  - an authorized `GET /v3/dataStores/edOrgs`: 400 → 404;
  - one `/v3/tenants/…` route in single-tenant mode;
  - new refresh routes;
  - startup requirements per E2, with migration guidance;
  - compose worker defaults are unchanged; bootstrap is set explicitly only in the fresh-install templates and CMS E2E.
- The tenant FK on schedules blocks tenant deletion (there is no API to delete a tenant).

### 6.14 Operator documentation (step 5.3)

New `docs/CMS-EDUCATION-ORGANIZATION-REFRESH.md` covers:
- routes and the E1 differences;
- consistency and retained stale data;
- the target lease, busy retries and the suspension guarantee;
- schedule, skip and coalescing rules vs manual refresh;
- `Error` meanings, including the composite format and the exhaustion limitation;
- log events;
- the aggregate's tenant resolution (routing, not authorization);
- the excluded GETs.

Updates: `CONFIGURATION.md`, `CONFIGURATION-SERVICE-BACKGROUND-JOBS.md` (job type, composite codes, consumer obligations), `EDUCATION-ORGANIZATION-PROJECTION.md`, `MULTI-TENANCY-GETTING-STARTED.md`. Bootstrap, operational and migration guidance lands with step 5.1.

---

## 7. Phases and steps

### 7.0 Step workflow and report format

**Per step:**
1. Make the smallest focused change.
2. Inspect the diff.
3. Run `dotnet csharpier format <paths>`, then re-read the formatted files in Rider.
4. Build the touched projects (Sonar S101/S125 apply).
5. Run the listed validation.
6. Commit locally. No amend, squash or push.

**Report:**
- the SHA;
- every edited file, with one line on what changed;
- behavior changes;
- exact commands and results, plus what was not run and why;
- residual risks;
- AC coverage;
- the running status table;
- an explicit **stop for approval**.

A rejected step gets a focused follow-up commit.

**Environment:** SQL Server lanes run on LocalDB first, long runs and full suites included. The `dms1439-sql2025-test` container is used when a lane needs real SQL Server or container behavior that LocalDB cannot provide, such as a result that differs only on LocalDB or engine-specific behavior. PG and MS test processes never run at the same time.

### 7.0.1 Review checkpoints (reduced plan)

Agreed at the step 2.4 review. Every planned step, test and validation is kept; only the review grouping changes. Each checkpoint is one approved local commit, plus any follow-up commit a rejection requires, and the next checkpoint starts only after approval.

| # | Checkpoint | Contents |
| --- | --- | --- |
| 1 | 2.5 | Execution integration through the real executor and fence (both providers) |
| 2 | 3.1 + the refresh part of 3.3 | Refresh routes, with their real-database API integration tests |
| 3 | 3.2 + the aggregate part of 3.3 | Aggregate GET and the excluded-GET guard, with the remaining API integration tests |
| 4 | 4.1 + 4.2 | Schedule reconciler, with its reconciliation integration tests (both providers) |
| 5 | 5.1 | Startup requirement and shipped-configuration repairs, with the 5.3 configuration, bootstrap, upgrade and deployment instructions |
| 6 | 5.2 | Operational Instance E2E, with the 5.3 operational scheduling instructions finalized and verified against the working E2E |
| 7 | 5.4 | Live cross-component validation |
| 8 | 5.5 | CMS E2E |
| 9 | 5.6 | Pre-push validation, including a drift recheck of all documentation |

There is no standalone 3.3 or 5.3 checkpoint. Checkpoint 2 is approved before checkpoint 3 starts.

### Phase 0 — Design record

**0.1 Commit the approved specification**
- Objective: an in-repo record.
- Files: `reference/design/edorg-refresh-DMS-1441/spec.md`.
- Change: documentation only.
- Depends on: approval.
- Validation: renders; links resolve.
- Done when: committed.
- Risks: none.

### Phase 1 — Persistence

**1.1 Schema, cascades and deletion cleanup (both providers)**
- Objective: AC1, AC20 (database), AC21.
- Files:
  - PG/MS `0042_Create_EducationOrganizationSnapshot_Tables.sql`
  - PG/MS `DatabaseTestBase.cs` (Respawn)
  - PG/MS `DataStoreManageDeletionTests.cs` (FK set)
  - new PG/MS `EducationOrganizationSnapshotSchemaTests.cs`
  - new PG/MS `EducationOrganizationSnapshotDeletionTests.cs`
- Change: the two tables (§6.1). No production C#.
- Depends on: 0.1.
- Validation:
  - Columns, types, nullability, PK, FKs and their actions, CKs.
  - Replay is idempotent.
  - Duplicate PK is rejected.
  - Bigint values above `Int32` round-trip.
  - Ordinary delete cascades; a forced failure rolls back.
  - Managed delete cascades.
  - Delete-first and insert-first races, driven by signals.
  - Full regression runs of `DataStoreManageDeletionTests` and `DataStoreTransactionTests`.
- Done when: green on both providers.
- Risks: the FK-set test (intended update); migration number collision with `main` (checked); cascade paths (one chain).

**1.2 Repository contract + PostgreSQL target protocol**
- Objective: §6.2 steps 2–4 and 6–10 on PG.
- Files:
  - `BE/EducationOrganizationRefresh/IEducationOrganizationSnapshotRepository.cs`
  - `PG/Repositories/EducationOrganizationSnapshotRepository.cs`
  - PG DI branch
  - PG `EducationOrganizationSnapshotWriteTests.cs`
- Change: `AcquireAsync`, `ReclaimAsync`, `RenewAsync`, `StageAsync`, `ActivateAsync(DbTransaction, …)`, `PruneAsync` and `ReleaseAsync`, with result unions.
- Depends on: 1.1.
- Validation:
  - {A,B,C}→{B,D}; empty success.
  - A live lease → `Busy` with no insert and no exception.
  - Absent store, or another tenant's store → `DataStoreNotFound`.
  - Expiry set in the past through SQL → takeover; the old owner's renew, stage and activate are refused.
  - Renewal of an expired lease is refused even with no new owner.
  - **Expiry during a lock wait:**
    1. Another transaction holds the target row lock **without changing the row**.
    2. A renewal starts while the lease is still valid.
    3. The holder waits until the database clock passes `LeaseExpiresAt`, then releases, still within the renewal's `R/2` lock timeout. This wait is a condition on the database clock, not a sleep.
    4. Expected: the renewal returns `LeaseLost`, and `LeaseExpiresAt` is unchanged (the lease is not revived).
    - The same scenario is run for acquisition (the expired lease is taken over, not reported `Busy`), stage (refused) and activation (refused).
  - **Repeated abandonment** (acquire, stage a non-empty generation, abandon ×3), followed by a successful publish. The test runs twice: once with no published generation (`ActiveGeneration` null), once with a published generation `a`.
    - At each checkpoint, assert the target row's allocated generation (`LastGeneration`, `LeaseGeneration`, `ActiveGeneration`) **separately from** the set of distinct `Generation` values present in the organization rows.

      | Checkpoint | Organization-row generations, never published | Organization-row generations, published `a` |
      | --- | --- | --- |
      | After acquisition and reclaim, before staging | `{}` (empty) | `{a}` |
      | After staging the non-empty generation `g` | `{g}` | `{a, g}` |
      | After a successful activation and prune | `{g}` | `{g}` |
  - Reclaim never deletes the active generation or the owner's own generation.
  - Activation inside a real job fence.
  - Prune keeps the active generation.
  - Delete races at acquire and at stage.
  - Forced `40P01`/`55P03` → classified.
  - 50 000-item stage.
- Done when: PG green.
- Risks: statement-clock use (`clock_timestamp()`) is checked by a test inside a long transaction, and LTV ordering by the lock-wait test.

**1.3 SQL Server target protocol**
- Objective: same as 1.2, on MS.
- Files: MS repository, MS DI branch, MS write tests.
- Depends on: 1.2.
- Validation: the 1.2 list, including the lock-wait expiry tests and the exact retained-generation-ID tests, plus:
  - the batched `OPENJSON` insert rejects an invalid parent or generation, and the foreign key and check constraints stay trusted;
  - a paused 50,000-row stage, reclaim and prune each hold only intent locks at page and table level on the snapshot table while another data store completes a refresh;
  - two concurrent acquisitions of an absent target: one `Acquired`, one `Busy`, no duplicate-key error (key-range);
  - forced `1205`/`1222` → classified.
- Done when: MS green.
- Risks: `HOLDLOCK` escalation (row locks requested); lock escalation of large inserts and deletes, for example under lock-memory pressure (batches reduce the risk; contention is bounded by lock waits).

**1.4 PostgreSQL read models and aggregate boundary**
- Objective: §6.7 on PG.
- Files: interface read methods and DTOs, PG repository, PG `DataStoreCatalog.cs` (shared-gate variant for symmetry), PG read tests.
- Depends on: 1.2.
- Validation:
  - Targets are tenant-scoped and ordered. A store whose connection string cannot be decrypted is still listed. Contexts are exact.
  - Aggregate: unmanaged, linked, pending and `Deleted` stores; numeric ordering; inactive generations invisible; other tenants absent.
  - **Real interleavings** through an internal seam that pauses between statements (a), (b) and (c): a managed delete, an ordinary delete, an activation and a publication each committing mid-read → the response is pure pre-state.
  - The FK refuses a dangling manage link.
- Done when: PG green.
- Risks: the seam stays internal and test-only.

**1.5 SQL Server read models and aggregate boundary**
- Objective: §6.7 on MS.
- Files: MS repository, MS `DataStoreCatalog.cs` (`Shared` mode), MS read tests.
- Depends on: 1.3, 1.4.
- Validation:
  - The 1.4 list.
  - Repository-level ordinary delete and managed publication blocked by the shared gate:
    - released within 5 s → completes after the read, and the response is pre-state;
    - held past 5 s → the writer fails, rolls back, and the state is unchanged.
  - Activation blocked by the held target share lock → waits, then publishes after the read.
  - An aggregate read during a paused 50,000-row stage, reclaim and prune completes without waiting and returns the active generations.
  - Shared-gate hold time measured and reported (explicit `OperationalProbe` fixture). The gate is held until the client has read every organization, so the hold grows with the result size and the network as well as the server.
- Done when: MS green.
- Risks: A5 (every link writer takes the gate) is re-verified by search, and the callers are listed in the report.

### Phase 2 — Refresh execution

**2.1 Contract, interval option and code registration**
- Objective: AC6 (interval), AC7 (payload), §6.3.
- Files:
  - `BE/EducationOrganizationRefresh/` (payload, scope, validator, job-type constants, error codes, `EducationOrganizationRefreshOptions` and its validator, `AddEducationOrganizationRefresh`)
  - `FE/…/WebApplicationBuilderExtensions.cs` (call)
  - `FE/appsettings.json` (interval)
  - unit tests and a FE startup test
- Change: options and codes only. No reader-requirement change: that is step 5.1.
- Depends on: 0.1.
- Validation: payload contract; validator matrix; FE startup refuses an invalid interval and names it; full FE unit suite.
- Done when: green.
- Risks: binding next to `AppSettingsValidator` (separate type).

**2.2 Composite error extension (DMS-1437)**
- Objective: §6.6.
- Files:
  - `BE/Jobs/JobPermanentException.cs` (constructor)
  - `IJobErrorCodeRegistry.cs` (`TryCompose`)
  - `JobErrorCodeRegistry.cs` (composite registrations, startup capacity check, composition)
  - `JobServiceCollectionExtensions.cs` (`AddJobCompositeErrorCode`)
  - `JobExecutor.cs` (terminal path)
  - `JobDiagnostics`/log event (`JobCompositeErrorRejected`)
  - unit tests; PG/MS `JobLeaseRepositoryTests` (a 1000-character message persists)
  - `JobErrorCode.Infrastructure` is **not** changed: no new built-in code.
- Depends on: 0.1.
- Validation:
  - Registration refuses a worst case over 1000, unknown or duplicate categories, and a composite as a category.
  - Composition:
    - a primary with zero spare room beyond its categories → codes listed, no prose, ≤ 1000;
    - all categories → every code present, ≤ 1000;
    - duplicates collapsed, in order;
    - a forged message on a valid code → canonical text only;
    - an unknown or disallowed category, an empty list, or an unregistered primary → `HandlerFailed`;
    - the single-code path is byte-identical (existing `JobExecutorTests` unchanged).
- Done when: jobs unit and provider suites green.
- Risks: shared code; the change is additive.

**2.3 Target refresher (lease, admission, renewal loop, classification)**
- Objective: §6.2 orchestration for one target.
- Files: `BE/EducationOrganizationRefresh/EducationOrganizationTargetRefresher.cs` (+ internal test-only seams `AfterAcquire`, `AfterReclaim` and `AfterAdmission`, which are null in production so no await is added); unit tests with FakeItEasy fakes of the reader and repository, a fake fence, `FakeTimeProvider` and a recording logger.
- Depends on: 1.2 (interface), 2.1.
- Validation:
  - Exact call order; the admission renewal is the immediate predecessor of `ReadAllAsync`, with no awaited call between.
  - A failed renewal → `targetCts` cancelled → `LeaseLost`, not propagated. The caller token → propagated.
  - Renewal ticks on fake time; a renewal timeout → `LeaseLost`.
  - Release on every non-activated exit, bounded at 5 s.
  - The renewal loop is stopped and awaited before the pre-stage renewal; no renewal call is observed after the read returns.
  - Prune failure → still `Refreshed`.
  - Redaction sentinels.
- Done when: unit green.
- Risks: the timer/loop lifetime is disposed on every path (tested).

**2.4 Job handler and registration**
- Objective: §6.5; AC2, 7, 8, 10, 11.
- Files: `EducationOrganizationRefreshJobHandler.cs`; `AddJobHandler` and `AddJobCompositeErrorCode` in `AddEducationOrganizationRefresh`; unit tests.
- Depends on: 2.2, 2.3, 1.4.
- Validation:
  - The decision table: all OK; permanent; transient with attempts left; transient on the final attempt; mixed; busy; `One` missing; `All` empty.
  - Every target is attempted.
  - Category order.
  - Retry re-reads every target.
  - No HTTP services in the container.
- Done when: unit green.
- Risks: rollout of the job type (documented).

**2.5 Execution integration through the real executor and fence (both providers)**
- Objective: AC9–13 under real DMS-1437 behavior.
- Files: PG/MS `EducationOrganizationRefreshJobTests.cs` (the `JobRuntimeIntegrationTests` harness, with a signalled reader double).
- Depends on: 1.3, 1.5, 2.4.
- Validation:
  - **Admission:** A pauses at `AfterAcquire` (and, separately, at `AfterReclaim`); the lease expiry is set in the past; B acquires and enters the reader; A resumes → A's reader call count stays 0 and its outcome is `LeaseLost`.
  - **Mid-read theft:** A is in the reader and the lease is taken over through SQL. A fake-time renewal tick cancels A's read, and B publishes.
  - **Pause after admission** (the documented delay model):
    1. A pauses at `AfterAdmission`.
    2. A's lease expiry is set in the past, and B acquires, enters the reader and publishes.
    3. A resumes and enters the reader; this is allowed under the failure model.
    4. Expected: A's next renewal tick cancels its read (`LeaseLost`). A forced stage and activation by A are refused. B's generation remains active, and the retained generation IDs are exactly `{B's}`.
  - **Job sources:** manual, scheduled and recovered executions against one target, with a different target in parallel.
  - **Retry:** A's retry re-reads the target A had published (call count 2).
  - **Interleaving:** A publishes, B publishes, A retries → A republishes a later generation, in lease order.
  - **Lost acknowledgement** (a fault after a fence commit) → the next attempt re-reads and republishes.
  - **Exhaustion** without a handler run → `AttemptsExhausted`, with the handler counter unchanged.
  - **Mixed failures** through retry → terminal `GET /v3/jobs` composite.
  - **Prune failure** after activation → `Completed`, new snapshot.
  - **Repeated abandonment** respects the 2-generation bound.
  - **Fence refusal** after staging → snapshot kept.
  - **`EnableDataStoreManagement=false`** → the refresh still runs.
  - **No `HttpContext`.**
  - **Persisted-column sentinel scan.**
- Done when: both green.
- Risks: duration. Lease expiry is driven by database column updates and loop ticks by fake time; there are no sleeps.

### Phase 3 — HTTP surface

**3.1 Refresh routes**
- Objective: AC3–5.
- Files: `FE/Modules/EducationOrganizationModule.cs`, `OpenApiModuleTags`, FE tests (`EducationOrganizationModuleTests.cs`, `EndpointAuthorizationTests.cs`), `ManagedDataStoreApiTestHost` (a request path under the simulated path base is routed without it, so a returned Location can be requested again), and the refresh half of PG/MS `EducationOrganizationHttpTests.cs`.
- Depends on: 2.1, 1.4.
- Validation: 202, body and exact Location (with and without PathBase); distinct messages; 404 creates no job; non-integer → 400; enqueue failure → 500 with no Location; EAT admin metadata; OpenAPI.
- Done when: FE green.
- Risks: precedence against `{id}`.
- Review: checkpoint 2, together with the refresh tests of 3.3 (POST then an immediate poll, a cross-tenant 404 that creates no job, Location under PathBase, and the header and path cases of the refresh routes).

**3.2 Aggregate GET and the excluded-GET guard**
- Objective: AC14–19.
- Files: the module, `DataModel/Model/Tenant/TenantDetailsResponse.cs`, `TenantResolutionMiddleware.cs` (constant only), `EndpointAuthorizationTests.cs`, FE tests (`TenantAggregateHttpTests.cs`).
- Depends on: 1.4, 3.1.
- Validation:
  - The 400/400/404 order, with probes.
  - The request scope is unchanged.
  - `default` in any case.
  - Mapping, nulls and ordering.
  - Guard responses.
  - A full-response sentinel scan.
  - Existing `DataStoreModule` tests unchanged.
- Done when: FE green with all four EAT fixtures.
- Risks: the EAT edits (intended).
- Review: checkpoint 3, together with the remaining tests of 3.3 (the full aggregate JSON, exact int64 text, the header and path cases of the aggregate route, no orphans after delete, and the blocked HTTP writers).

**3.3 API integration on real databases (both providers)**
- Objective: API-level evidence for AC3–5 and 15–21, plus blocked HTTP writers.
- Files: PG/MS `EducationOrganizationHttpTests.cs` (`ManagedDataStoreApiTestHost`, both tenancy modes). The refresh fixture uses its own fully migrated database. The aggregate fixture uses the shared test database, so it can seed snapshots through the target protocol, and runs a second host whose repository pauses the read.
- Deletion consistency: on PostgreSQL, a delete that commits while the read is paused leaves the response showing the whole pre-delete state. On SQL Server, the paused read holds the gate, so the same delete is a blocked writer (below).
- Depends on: 3.2, 1.5.
- Validation:
  - POST, then an immediate poll.
  - A cross-tenant 404 creates no job.
  - The full aggregate JSON from snapshots seeded through the protocol.
  - Exact int64 text.
  - Header and path cases.
  - No orphans after delete.
  - Location resolves under PathBase.
  - **Blocked HTTP writers (SQL Server lane):** while an aggregate read holds the gate past its bound, ordinary DELETE and POST return 500 with the state unchanged; after release they succeed.
- Done when: both green.
- Risks: temp-path collisions (lanes never run concurrently).
- Review: no checkpoint of its own. Its tests are split between checkpoints 2 and 3 as listed under 3.1 and 3.2, and every test listed here is kept.

### Phase 4 — Scheduling

**4.1 Schedule reconciler**
- Objective: AC13 (reconciliation).
- Files: `BE/EducationOrganizationRefresh/EducationOrganizationRefreshScheduleReconciler.cs`; registration gated on `SchedulerEnabled` in `AddEducationOrganizationRefresh`; unit tests (fakes plus `FakeTimeProvider`); an FE startup test that the host resolves and runs the reconciler only when the scheduler is on.
- Depends on: 2.4.
- Validation: per-tenant upsert under the right context; the §6.9 disable table; partial enumeration → no disables; Unknown → keep; mode switches; periodic runs; not registered when the scheduler is off; independent of management.
- Done when: unit green.
- Risks: mixed `MultiTenancy` across replicas (documented as unsupported).

**4.2 Reconciliation integration (both providers)**
- Files: PG/MS `EducationOrganizationRefreshScheduleTests.cs`.
- Depends on: 4.1.
- Validation:
  - Stable IDs; interval change.
  - Two reconcilers, with a tenant inserted between one reconciler's enumeration and its disable phase → the new tenant stays enabled.
  - A mode switch keeps rows and jobs.
  - A due occurrence becomes a refresh job, and the skip rule holds while it is active (`MaterializeNextDue` driven directly).
- Done when: both green.
- Risks: none beyond 4.1.
- Review: one checkpoint with 4.1.

### Phase 5 — Deployment, operations, documentation, live validation

**5.1 Startup requirement and every shipped-configuration repair (one commit)**
- Objective: E2 rules 1, 2 and 5–7 together.
- Files:
  - `EOP/DmsEducationOrganizationProjectionSettingsValidator.cs` (remove the early return)
  - new `BE/EducationOrganizationRefresh/RefreshReaderRequiredValidator.cs` (worker-gated)
  - FE startup `JobWorkersDisabled` log
  - `eng/docker-compose/{local,published}-config.compose.yml`: reader variable mappings only. **The worker default is unchanged.**
  - `eng/docker-compose/.env.example`: an explicit, commented bootstrap `CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED=false`, plus empty reader variables.
  - `eng/azure-vm/compose/*`: the same changes.
  - CMS E2E environment: explicit bootstrap (no DMS).
  - Instance E2E compose and `build-dms.ps1`: reader variables passed through, preparing the operational wiring for 5.2.
  - every start script or CI path found by the inventory: either explicit bootstrap or operational
  - affected validator and startup tests
  - Pester compose-wiring tests
  - `docs/CONFIGURATION.md` sections on bootstrap, operational and migration
- Depends on: 2.1, 4.1.
- Validation:
  - The AC6 startup matrix.
  - Existing validator and startup tests updated where the early return made "blank + invalid" pass; each change is listed.
  - **Configuration-preservation matrix.** Pester renders `docker compose config` with throwaway `.env` files and asserts the resulting `JobSettings__WorkerEnabled`, `AppSettings__EnableDataStoreManagement` and reader values. The FE startup test (`WebApplicationFactory`) runs the same value sets and asserts start or refusal:

    | Management | Worker variable | Reader settings | Rendered worker | Startup |
    | --- | --- | --- | --- | --- |
    | `true` | absent | missing | `true` | **refused**, naming the reader settings |
    | `true` | absent | valid | `true` | starts operational |
    | `true` | `true` | missing | `true` | refused |
    | `true` | `false` | missing | `false` | starts, logs `JobWorkersDisabled` (explicit choice) |
    | absent | absent | missing | `true` | refused |
    | — | fresh copy of `.env.example` | missing | `false` | starts in bootstrap, logs the warning |

    No upgrade case renders workers off unless the variable is explicitly `false`.
  - Every compose file renders.
  - Local compose up with the fresh `.env.example` → CMS healthy, and the warning is logged.
  - The CMS E2E setup starts.
  - The full FE unit suite.
- Done when: every shipped stack starts in its declared mode, and the matrix is green.
- Risks: missed inventory entries. A repo-wide search for `JobSettings__WorkerEnabled` and `CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED` is part of the step.
- As implemented:
  - **Validation split.** `DmsEducationOrganizationProjectionSettingsValidator` always checks the bounds, the body size, the contract versions, every `TenantCredentials` entry and a half-set shared pair. It still checks the base URL's format, and requires a complete pair, only when `DmsBaseUrl` is set. `RefreshReaderRequiredValidator` is registered by `AddEducationOrganizationRefresh` when `JobSettings:WorkerEnabled` is true, read at registration as the host reads it for the worker. It adds only what the other validator does not check: `DmsBaseUrl` missing, and no complete pair while it is missing. No failure is reported twice.
  - **`JobWorkersDisabled`** is `BE/Jobs/JobWorkersDisabledNotice`, a hosted service the host registers in place of the worker (EventId `1441_11`).
  - **Azure VM stack.** It had no `JobSettings__WorkerEnabled` line; its Configuration Services ran workers through the `appsettings.json` default. The shared Configuration Service environment gains `JobSettings__WorkerEnabled: ${CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED:-true}`, which keeps that default. Each stack gets its own reader variables (`CONFIGURATION_SERVICE_ST_DMS_EDORG_*`, `CONFIGURATION_SERVICE_MT_DMS_EDORG_*`), and its `.env.example` sets bootstrap. `provision/UPDATE.md` names the choice an in-place update must make.
  - **Inventory.** Every tracked full environment file that composes the Configuration Service, and its mode (asserted by `EducationOrganizationRefreshWiring.Tests.ps1`):

    | File | Used by | Mode |
    | --- | --- | --- |
    | `.env.example` | fresh local installs; `start-*`/`bootstrap-*` seed `.env` from it | bootstrap |
    | `.env.multitenancy` | multi-tenancy getting-started example | bootstrap |
    | `.env.e2e`, `.env.cursorpartitions.e2e` | DMS E2E, CDC, SDK, performance scripts | bootstrap |
    | `.env.routeContext.e2e` | Instance Management E2E | bootstrap until 5.2 wires it as operational |
    | `.env.smoke`, `.env.smoke.ds61` | scheduled smoke tests | bootstrap |
    | `.env.template`, `.env.template.ds61` | database template builds | bootstrap |
    | `.env.config.e2e`, `.env.config.mssql.e2e`, `.env.config.mssql.multitenant.e2e` | CMS E2E | bootstrap |
    | `eng/azure-vm/compose/.env.example` | fresh Azure VM installs | bootstrap |

    Overlays (`.env.ds52`, `.env.ds61`, `.env.bootstrap.*`, `.env.mssql`, `.env.document-cache.e2e`, `.env.ide.example`) inherit the mode of their base file.
  - **Test hosts that run workers** without DMS (the CMS E2E managed-store deployment and the backend process-recovery hosts) set reader settings that name an unused local port.

**5.2 Operational Instance E2E: provisioning → workers on → scheduled execution**
- Objective: AC13 operational behavior and E2 point 5 (Q8).
- Files: the Instance E2E environment setup (`build-dms.ps1` InstanceE2E fixture phase) and a new test in `Tests.DmsProjectionE2E` (`Live/OperationalRefreshTests.cs`).
- Change, in the environment setup:
  1. CMS starts in bootstrap (workers off).
  2. The fixture provisioning creates the per-tenant projection claim sets and applications (moved from the test fixture into the environment phase, or reused from it).
  3. CMS is recreated with the reader settings, `WorkerEnabled=true`, `SchedulerEnabled=true` and `EdOrgsRefreshIntervalInMins=1`.
  4. The variables are exported for the tests. One of them is a **read-only** connection string to the deployed CMS database, used only to inspect `dmscs.JobSchedule` and `dmscs.Job`.
- Test, with **no manual refresh until the last step**:
  1. **Schedule exists.** Read `Tenant_255901`'s `EducationOrganization.ScheduledRefresh` schedule row: enabled, interval 1.
  2. **First scheduled publication.** Seed the existing hierarchy. Poll until both hold, with a deadline of 3 intervals:
     - a `Completed` job exists with `SourceScheduleId` equal to that schedule's ID;
     - `GET /v3/tenants/Tenant_255901/dataStores/edOrgs` shows exactly the 7 seeded organizations for 255901/2025.
     Record that job's `ScheduledOccurrence` as T1.
  3. **Recurrence.** Change representative source data through the DMS API (rename one seeded school; the rename is reverted in cleanup). Poll until both hold, with a deadline of 3 intervals:
     - a later `Completed` job exists with the same `SourceScheduleId` and a `ScheduledOccurrence` after T1;
     - the aggregate shows the new name.
     This proves a later durable occurrence published the change.
  4. **Manual refresh.** POST refresh → poll the job → `Completed`, and its `SourceScheduleId` is null.
- Depends on: 5.1, 3.3.
- Validation: `build-dms.ps1 InstanceE2ETest` for the projection shard (PG; MS when the environment supports it), with exact results.
- Done when: green on PG; MS status reported.
- Risks: E2E run time (+2–6 min); container recreation in setup; the read-only CMS database connection must stay test-only and never be logged.

**5.3 Operator documentation** (§6.14)
- Depends on: 5.2.
- Validation: lint; links resolve; every documented setting is read by code.
- Done when: reviewed.
- Risks: drift (re-checked in 5.6).
- Review: no checkpoint of its own. The configuration, bootstrap, upgrade and deployment instructions land in 5.1; the operational scheduling instructions are finalized in 5.2 and verified against the working E2E; 5.6 rechecks all documentation for drift.

**5.4 Live cross-component validation (concrete)**
- Objective: the Jira live verification; AC8–11 against a real DMS.
- Files:
  - `Live/RefreshHost.cs`
  - `Live/EducationOrganizationRefreshLiveTests.cs`
  - csproj references to the CMS backend provider projects (CMS only)
  - `RecordedRequest` gains `DataStoreId`, parsed from the page query
- Harness:
  - A scratch CMS database `dmscs_refresh_e2e_<run>` on the server of `INSTANCE_E2E_DATABASE_2_CONNECTION_STRING`, deployed with `DatabaseDeploy` and dropped through `CleanupRegistry`. Missing permission is a setup failure; the test never skips.
  - Tenant and `DataStore` rows with the fixture IDs (`OVERRIDING SYSTEM VALUE` / `IDENTITY_INSERT`) and their contexts.
  - The production reader with the fault handler, the CMS repositories, `AddEducationOrganizationRefresh` and the DMS-1437 runtime (`JobWorkerService`, `PollInterval=1 s`, `MaxAttempts` per case).
  - The terminal status is awaited by polling `GetJobStatus` against a deadline.
- Cases (all required):
  1. **Multi-page success:** `PageSize=2`; refresh-all on `Tenant_255901` → 2025 holds exactly the 7 seeded organizations over 4 pages, and 2024 holds its actual projection; `Completed`.
  2. **Mixed failure:**
     - Setup: a planted snapshot for 2024; case 1's snapshot for 2025.
     - Fault: a permanent `problem-target-schema-incompatible` on page 3 of 2025 only.
     - Expected: 2024 is replaced and 2025 keeps its 7 organizations; `Error`, with the composite listing `EdOrgProjectionTargetSchemaIncompatible`.
  3. **Timeout:** `PageRequestTimeoutSeconds=1`, and page 2 of 2025 stalls until its token is cancelled → snapshot kept; with `MaxAttempts=1` → `Error` with `EdOrgRefreshTemporarilyUnavailable`.
  4. **Unavailable DMS:** an unused local port → all targets transient, snapshots kept, `Error`.
  5. **Inaccessible target:** a scratch store ID unknown to the deployed CMS, with valid contexts → `target-not-found` → refresh-one `Error` with that message.
  6. **Retry to success:** `MaxAttempts=2`, `RetryBackoffBase=1 s`, a transient fault on attempt 1 only → `Completed`; the reader double shows every target re-read on attempt 2.
- Depends on: 2.5.
- Validation: the projection shard of `build-dms.ps1 InstanceE2ETest` on PG, and on MS where supported. An engine not run is reported as a limitation.
- Done when: all 6 cases pass on PG; MS status reported.
- Risks: environment permissions; run time.

**5.5 CMS E2E (deployed stack, both IdP modes; bootstrap, no DMS)**
- Files: `CFG/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/EducationOrganizations.feature` and its steps.
- Depends on: 3.3, 5.1.
- Validation, in both `@SelfContainedOnly` and `@KeycloakOnly` lanes:
  - refresh 202 and an immediate poll (`Pending`);
  - read-only → 403;
  - the single-tenant `default` aggregate;
  - `@MultitenantOnly` header rules;
  - excluded GETs → 404.
- Done when: both lanes green.
- Risks: the managed-store suppression test (`ManagedDataStoreExternalSuppressionTests`) asserts on every `Error` job, and with the scheduler on the refresh schedule (step 4.1) adds refresh jobs to that host; its assertions must account for them.

**5.6 Pre-push validation** (no commit unless fixes are needed)
- Runs:
  - CSharpier check;
  - CMS solution build;
  - all CMS unit suites;
  - PG and MS integration suites (separately);
  - EAT;
  - CMS E2E in both IdP modes;
  - Instance and live projection E2E;
  - compose Pester tests.
- All results are reported exactly.
- Then the cumulative-diff review and a Codex AC challenge come before any request to push.

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| An overlapping read after an execution or scheduling delay longer than `D` | Read-only and never published (unconditional, tested). Cancelled at the next renewal under normal scheduling; no hard timing bound is claimed (§6.2). |
| Large snapshots and the SQL Server shared-gate hold time | Staging happens outside the fence; hold time measured (1.5); writers fail boundedly (§6.12) |
| Slow sequential refresh-all | Renewals keep both leases; documented (Q4) |
| Busy target forces a job retry | Documented. Leases cover one target at a time, so waits are short. |
| Composite extension touches shared jobs code | Additive; existing tests unchanged; startup capacity check |
| Operational deployment change | One commit with every repair (5.1). Compose worker defaults are unchanged. Bootstrap is explicit only in fresh-install templates and CMS E2E. The preservation matrix is tested. A missing configuration fails loudly. |
| At-least-once re-reads increase DMS load on retries | Exponential backoff; `MaxAttempts`; documented |
| Exhaustion hides the summary | Stated limitation, tested |

## 9. Assumptions

- **A1.** DMS-1440 Discovery and token behavior is as documented.
- **A2.** Resolved (F13).
- **A3.** Resolved (F14).
- **A4.** Explicit JSON nulls are asserted on full HTTP bodies.
- **A5.** Every managed-link or ordinary-row writer takes the tenant gate (F12). Re-verified in 1.5.

## 10. Decisions

| ID | Decision | Status |
| --- | --- | --- |
| D1 | Snapshot = active generation | Agreed |
| D2 | Option A. Sequencing agreed (5.1, then 5.2). Compose defaults preserved; bootstrap is explicit (v4). | Agreed; **confirm the v4 preservation design** |
| D3 | Composite registered-category error (§6.6) | **Approved** (the v3 review) |
| D4 | Job-level retry, at-least-once re-read; exhaustion and acknowledgement limitations stated | **Approved** (the v3 review) |
| D5 | Include `Deleted` tombstones | Agreed |
| D6 | Relative `{PathBase}/v3/jobs/{id}` | Agreed |
| D7 | Canonical tenant name | Agreed |
| D8 | Reconciler gated on `SchedulerEnabled` | Agreed |
| D9 | Renewed target lease (JobSettings timing). Lock-then-validate for every ownership decision. Strict admission renewal. Cancel on loss. Orderly loop shutdown. Fenced publication. Execution and scheduling delay model stated. | Architecture accepted; **confirm the v4 LTV correction** |
| D10 | PG `REPEATABLE READ READ ONLY`; MS shared gate + `HOLDLOCK` targets | Agreed with corrections, now applied |

## 11. Challenge invitation (Codex)

Please challenge whether **v4 as a whole** satisfies every current Jira AC. In particular:

- **v3 findings 1–5.**
  - Null-safe reclaim with exact retained-ID tests.
  - Lock-then-validate on every ownership decision, on both providers, with the lock-wait expiry tests.
  - Compose defaults preserved, with explicit bootstrap and the preservation matrix.
  - The restated delay model and the post-admission pause test.
  - Recurring scheduled execution proven through `SourceScheduleId` and changed source data.

Each item in the standing checklist below still applies.

1. **Reader admission.**
   - Can a stale owner enter the reader without an execution or scheduling delay longer than `D`?
   - Is every ownership decision lock-then-validate on both providers?
   - Can any path stage, activate or reclaim after a takeover?
   - Are the takeover, reclaim and staging locks sufficient?
2. **Composite contract.** Is the bound provable for every registrable composite? Is registry-only text enforced end to end? Do the category lists satisfy AC8 for every supported mix?
3. **At-least-once retry.** Is it consistent with AC9–12 and with the tests?
4. **Deployment.** Does any shipped or upgraded deployment start in an unintended mode? Does 5.2 actually prove scheduled execution?
5. **Locking.** Are the corrected compatibility explanation and the bounded-failure behavior for HTTP writers accurate, and do the tests cover them?
6. **Test strength.** Would each listed test fail on a plausible wrong implementation? For example:
   - renewal using transaction-start time;
   - admission placed before reclaim;
   - acquisition inserting on busy;
   - composite using `.Message`;
   - a category dropped on overflow;
   - prune failure treated as fatal;
   - a request-scope context mutation;
   - text-ordered IDs.
