# DMS-1439: Add managed data-store lifecycle endpoints and reconciliation to CMS

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Implement the CMS equivalent of the Management API v3 managed data-store lifecycle resource with Admin API payload parity.

- Endpoints: `GET /v3/dataStores/manage`, `POST /v3/dataStores/manage`, `GET /v3/dataStores/manage/{id}`, and `DELETE /v3/dataStores/manage/{id}`.
- Ordinary route guards: existing `PUT /v3/dataStores/{id}` and `DELETE /v3/dataStores/{id}` return `409 Conflict` for linked managed data stores.
- Persistence: tenant-scoped managed lifecycle aggregate with request values, generated database name, expected source identity, linked ordinary data-store ID/name, lifecycle status, timestamps, and retry/reconciliation metadata.
- Execution: DMS-1437 durable create/delete jobs call the DMS-1438 provisioner, then reconcile physical target state with the ordinary CMS `DataStore` routing catalog.
- Feature flag: `EnableDataStoreManagement`, default `true`, gates managed routes and managed create/delete work without disabling education-organization refresh.

This ticket depends on DMS-1437 for jobs and DMS-1438 for physical database create/delete. It does not create AdminAPI itself; it adds the compatible CMS control-plane behavior.

### Description

CMS can register an existing data store but cannot provision, observe, or physically delete one. The v3 managed resource requires a separate lifecycle aggregate whose asynchronous work ultimately creates or removes an ordinary CMS `DataStore` registration.

This story delivers `/v3/dataStores/manage`, durable create/delete reconciliation, and safe integration with the existing routing catalog.

**Scope**

- Add tenant-scoped managed data-store persistence for request values, generated database name, expected source identity, linked ordinary data-store ID/name, lifecycle status, and timestamps.
- Add collection, create, by-ID, and delete routes under `/v3/dataStores/manage`.
- Add create and delete job handlers using DMS-1437 and the DMS-1438 provisioner.
- Build and encrypt the ordinary runtime connection string using existing CMS facilities and insert/remove it through the existing data-store repository boundary.
- Add duplicate, lifecycle-state, database-name, provider/configuration, and ownership validation.
- Prevent ordinary update or metadata-only deletion from bypassing managed lifecycle.
- Add `EnableDataStoreManagement`, default `true`, gating managed routes and managed work only.

**API surface**

- `GET /v3/dataStores/manage`
- `POST /v3/dataStores/manage`
- `GET /v3/dataStores/manage/{id}`
- `DELETE /v3/dataStores/manage/{id}`
- Existing `PUT /v3/dataStores/{id}` and `DELETE /v3/dataStores/{id}` receive atomic guards for managed records.

No managed PUT is in the v3 contract.

Admin API v3 payload parity:

`POST /v3/dataStores/manage` accepts only `addDataStoreManageRequest`:

```json
{
  "name": "datastore1",
  "databaseTemplate": "Minimal"
}
```

Successful create returns `202 Accepted` with no response body and a `Location` header pointing to the absolute management resource URL, for example:

```http
Location: https://server.example/v3/dataStores/manage/684
```

`GET /v3/dataStores/manage?offset=0&limit=25&direction=Descending` returns an array of `dataStoreManageModel` objects with only these wire fields. Pending managed records that have not linked to an ordinary data store yet return `null` for `dataStoreId`, `dataStoreName`, and `databaseName` when that is the pinned Admin API contract:

```json
[
  {
    "id": 684,
    "name": "datastore1",
    "dataStoreId": null,
    "dataStoreName": null,
    "status": "PendingCreate",
    "databaseTemplate": "Minimal",
    "databaseName": null,
    "lastRefreshed": "2026-07-20T21:35:02.4632854",
    "lastModifiedDate": "2026-07-20T21:35:02.4632854"
  },
  {
    "id": 685,
    "name": "datastore2",
    "dataStoreId": null,
    "dataStoreName": null,
    "status": "PendingCreate",
    "databaseTemplate": "Minimal",
    "databaseName": null,
    "lastRefreshed": "2026-07-20T21:35:42.2375482",
    "lastModifiedDate": "2026-07-20T21:35:42.2375482"
  }
]
```

`GET /v3/dataStores/manage/{id}` returns one `dataStoreManageModel` object or `404`:

```json
{
  "id": 684,
  "name": "datastore1",
  "dataStoreId": null,
  "dataStoreName": null,
  "status": "PendingCreate",
  "databaseTemplate": "Minimal",
  "databaseName": null,
  "lastRefreshed": "2026-07-20T21:35:02.4632854",
  "lastModifiedDate": "2026-07-20T21:35:02.4632854"
}
```

Nullable fields remain present or serialized according to the pinned contract and CMS JSON conventions. Internal fields such as expected source identity, artifact identity, retry metadata, lease metadata, generated target database name when not exposed, admin connection references, and diagnostics are never part of the API payload.

**Architecture and boundaries**

CMS owns the management aggregate and state machine. `dmscs.DataStore` remains the ordinary DMS routing registration and is populated only after physical provisioning succeeds. Each CMS-only consistency boundary is atomic: managed row plus job enqueue; ordinary row plus link/`Created`; `PendingDelete` plus job; and snapshot/ordinary removal plus retained `Deleted` tombstone. Existing repository calls that create independent transaction scopes cannot be composed to claim this atomicity. Job handlers reconcile physical and CMS state idempotently because physical databases and CMS cannot share a transaction.

Lifecycle statuses follow current v3 behavior: `PendingCreate`, `CreateInProgress`, `Created`, `CreateFailed`, `CreateError`, `PendingDelete`, `DeleteInProgress`, `Deleted`, `DeleteFailed`, and `DeleteError`.

**Dependencies**

- DMS-1437 durable CMS jobs.
- DMS-1438 CMS-owned runtime-safe DMS-template provisioner.
- Existing CMS `IDataStoreRepository`, connection-string encryption, authorization policies, tenant context, and provider-specific migration conventions.
- Existing DMS data-store cache refresh; no new callback dependency.

**Blockers**

- [DMS-1437](DMS-1437-durable-jobs-and-schedules.md) — must deliver durable job enqueue, execution, retry, and polling.
- [DMS-1438](DMS-1438-template-provisioner.md) — must deliver the CMS-owned trusted template provisioner used by create/delete reconciliation.

**Out of scope**

- Managed update/rename/copy, database size reporting, migrations, or progress percentages.
- Creating template artifacts.
- Application/API-client assignment to the new data store.
- Immediate DMS cache invalidation or a reverse service callback.
- Education-organization refresh.

**Risks and implementation considerations**

- Physical and CMS state cannot be committed atomically. Correctness depends on the idempotent state machine and DMS-1438 ownership check.
- Operators need explicit documentation for administrative database privileges and the DMS cache visibility window.

**Minimum persistence contract**

Provider migrations must use equivalent PostgreSQL and SQL Server types. Exact table and column names should follow CMS conventions, but the managed lifecycle table needs at least:

| Logical column | Portable type intent | API exposure |
| --- | --- | --- |
| `id` | integer identity/key | `dataStoreManageModel.id` |
| `tenant` | nullable bounded text/string | Internal tenant isolation only |
| `name` | bounded text/string, normalized unique while active | `name` |
| `dataStoreId` | nullable integer | `dataStoreId` |
| `dataStoreName` | nullable bounded text/string or derived linked value | `dataStoreName` |
| `status` | bounded text/string | `status` |
| `databaseTemplate` | bounded text/string | `databaseTemplate` |
| `databaseName` | nullable bounded text/string, unique where required | `databaseName`; may remain null while a managed record is pending if the pinned API contract does not expose the generated target name before creation. |
| `lastRefreshed` | nullable UTC timestamp/date-time | `lastRefreshed` |
| `lastModifiedDate` | nullable UTC timestamp/date-time | `lastModifiedDate` |
| `expectedSourceIdentity` | UUID/GUID | Internal only; used for ownership and reconciliation |
| `targetDatabaseName` | bounded text/string, unique where required | Internal only when the API `databaseName` is null before physical creation; used by the lifecycle job and provisioner. |
| `artifactIdentity`, `artifactHash`, `provider`, `contractVersion` | bounded text/string | Internal only; used for retry/reconcile safety |
| `createdAt`, `updatedAt`, `deletedAt` | UTC timestamp/date-time | Internal except where mapped to `lastModifiedDate` by contract |
| `jobId`, `failureCategory`, `failureSummary`, `attemptMetadata` | nullable bounded text/string or JSON | Internal only |

The linked ordinary `DataStore` row remains the routing catalog. The management table must not store decrypted connection strings or administrative credentials.

### Acceptance Criteria

1. Managed records are tenant-scoped and persist all fields needed to return `dataStoreManageModel`, plus an internal expected source identity, generated target database name when not exposed as `databaseName`, and retry/reconciliation metadata that are never exposed.
2. `POST /v3/dataStores/manage` trims `name`, requires 1–46 characters matching `^[A-Za-z0-9 _]+$`, and accepts only case-sensitive `Minimal` or `Sample`. The 46-character limit reflects Admin API parity after `ADMINAPI-1493` and keeps the generated database name within the 63-character portable limit before prefix stripping. Normalized uniqueness uses the trimmed value. Invalid values return the established CMS `400` problem details.
3. POST is create-only. It returns `400` for an active managed name, an existing ordinary data-store name, or an unsafe/overlength generated name. Database naming starts with `EdFi_Ods`, converts spaces to underscores, trims underscores, strips repeated leading case-insensitive `edfi_ods` variants, appends the case-sensitive template, and enforces a portable 63-character maximum. Static configuration is validated at startup; transient artifact/target outages discovered after acceptance are retryable job failures, not client errors.
4. POST atomically inserts `PendingCreate` and its durable job, then returns `202 Accepted`, no required body, and an absolute `Location` for `/v3/dataStores/manage/{id}`.
5. Collection GET supports the OpenAPI paging/sorting, `id`, and `name` filters and returns tenant-scoped management models. By-ID GET returns one model or `404` without revealing another tenant's record. Pending managed records without a linked ordinary `DataStore` return nullable linked fields as shown by the pinned Admin API examples: `dataStoreId`, `dataStoreName`, and `databaseName` are `null` until the contract says otherwise.
6. Create execution transitions through `CreateInProgress`, calls DMS-1438 with the persisted target name/template/expected source identity, then atomically creates the encrypted ordinary `DataStore`, links it, and marks `Created` in a single CMS transaction.
7. A transient create failure is recorded as `CreateFailed` while DMS-1437 will retry; exhausting attempts records `CreateError`. Retries reconcile an already-owned physical target and an already-linked ordinary record without duplicates.
8. `DELETE /v3/dataStores/manage/{id}` accepts only `Created`, atomically writes `PendingDelete` and its job in a single CMS transaction, and returns `204`. Absent/`Deleted` returns `404`; all other lifecycle states return a status-specific `400`.
9. Delete execution transitions through `DeleteInProgress`, verifies and drops the DMS-1438-owned physical target, then atomically removes its snapshot and linked ordinary catalog row, retains the management tombstone, and marks `Deleted` in a single CMS transaction.
10. A transient delete failure is `DeleteFailed`; exhausting attempts is `DeleteError`. Retry is idempotent when the owned database or ordinary catalog row is already absent.
11. Existing ordinary `PUT /v3/dataStores/{id}` and `DELETE /v3/dataStores/{id}` return `409 Conflict` for a linked managed data store. Stable CMS problem details include the absolute `/v3/dataStores/manage/{manageId}` location. PostgreSQL and SQL Server enforce the link check and mutation atomically in the repository transaction, preventing TOCTOU races and bypass through another handler.
12. Management responses and logs never expose the ordinary or administrative connection string, expected source identity, package credentials, or decrypted secrets.
13. Managed POST/DELETE use the existing admin policy. Managed reads use the existing read-only-or-admin policy. All repository and job operations enforce tenant isolation.
14. With `EnableDataStoreManagement=false`, all managed routes return the established CMS `400` disabled-feature problem details and managed jobs are not claimed/scheduled; job and education-organization capabilities remain available.
15. With the feature enabled or unset, default behavior is enabled. Startup validates required administrative connection and template-catalog configuration with actionable diagnostics.
16. Successful ordinary registration becomes visible through the existing DMS cache refresh. Documentation states that routability is eventually consistent with `DataStoreCacheExpirationSeconds`; no CMS-to-DMS callback is introduced.
17. PostgreSQL and SQL Server exhibit the same observable API/state behavior.

### Tasks

**Implementation**

1. Add provider-equivalent managed lifecycle migrations, constraints, records, mapping, and deterministic database-name validation.
2. Add managed-aggregate persistence operations that provide one atomic transaction for every CMS consistency boundary, including failure-injection tests.
3. Add routes, validators, authorization, feature gating, stable problem details, and absolute resource locations.
4. Add fenced/idempotent DMS-1437 create/delete handlers around DMS-1438, encrypted ordinary registration, snapshot cleanup, and lifecycle transitions.
5. Add atomic ordinary PUT/DELETE repository guards and document managed-resource conflict/remediation behavior.

**Verification**

- Unit tests following CMS conventions for validators, route results, authorization mapping, state transitions, retry classification, feature flag, and secret redaction.
- PostgreSQL and SQL Server integration tests for schema constraints, each aggregate transaction boundary, injected rollback, duplicate races, tenant isolation, and ordinary data-store linking/removal.
- API-level integration tests for every route/status, exact name/database-name vectors, absolute `Location`, filters, disabled mode, cross-tenant behavior, and ordinary PUT/DELETE guards.
- Live provider tests covering successful create/delete, process interruption and lease recovery, retry after each cross-system boundary, unowned collision refusal, and DMS discovery after cache expiry.
