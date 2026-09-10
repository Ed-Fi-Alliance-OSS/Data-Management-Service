# DMS-1441: Add CMS education-organization refresh, projection, and tenant aggregation

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Create the CMS education-organization refresh, snapshot, and read workflow required for Management API v3 parity.

- Refresh endpoints: `POST /v3/dataStores/edOrgs/refresh` and `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh`, returning `202 Accepted`, `jobQueuedResult`, and a job-status `Location`.
- Read endpoints: `GET /v3/dataStores/edOrgs`, `GET /v3/dataStores/{dataStoreId}/edOrgs`, and `GET /v3/tenants/{tenantName}/dataStores/edOrgs`.
- Persistence: tenant-scoped snapshots keyed by data store and education-organization ID, with transactional replacement and cleanup on ordinary or managed data-store deletion.
- Execution: DMS-1437 manual and scheduled jobs call the DMS-1440 reader, preserve prior snapshots on failed target reads, and report partial refresh failure truthfully.
- Aggregate behavior: read routes share one snapshot-backed query model and overlay DMS-1439 managed lifecycle metadata, including pending managed rows with no ordinary data-store link.

### Description

CMS stores application education-organization IDs but cannot discover the organizations present in each configured data store, refresh a durable projection, expose refresh job status, or return the data-store and tenant education-organization reads required by Management API v3.

This story delivers tenant-scoped snapshots, mandatory manual refresh, the all-data-store read, the single-data-store read, the tenant aggregate route, and required scheduled refresh matching pinned Admin API behavior.

**Scope**

- Add provider-equivalent CMS snapshot persistence keyed by tenant, data store, and education-organization ID, including refresh metadata.
- Add refresh-all and refresh-one endpoints that enqueue DMS-1437 jobs and return the v3 job response.
- Add refresh job handlers that decrypt the existing ordinary data-store connection, call the CMS-owned DMS-1440 reader, and transactionally replace snapshots per successful target.
- Add configurable scheduled refresh per tenant using DMS-1437's durable `Schedules` and `Jobs` persistence, the same refresh-all handler as manual refresh, and the same duplicate-suppression rules.
- Add `GET /v3/dataStores/edOrgs`, `GET /v3/dataStores/{dataStoreId}/edOrgs`, and `GET /v3/tenants/{tenantName}/dataStores/edOrgs`; all three read from the same tenant-scoped snapshot/aggregate query model and merge ordinary data stores, snapshots, and DMS-1439 management metadata where the route shape requires it.
- Remove a data store's snapshot when its ordinary CMS catalog row is deleted, including deletion through DMS-1439 managed lifecycle.
- Add truthful partial-failure, stale-snapshot, retry, authorization, observability, and tenant-isolation behavior.

**API surface**

- `POST /v3/dataStores/edOrgs/refresh`
- `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh`
- `GET /v3/dataStores/edOrgs`
- `GET /v3/dataStores/{dataStoreId}/edOrgs`
- `GET /v3/tenants/{tenantName}/dataStores/edOrgs`
- `GET /v3/jobs/{jobId}` from DMS-1437

Admin API v3 refresh response parity:

Both refresh routes return `202 Accepted`, a `Location` header for the job-status route, and a `jobQueuedResult` body:

```http
Location: /v3/jobs/RefreshEducationOrganizationsJob-tenant-a-123_65b37e7cba4448f58d4bcbf28f3b04a1
```

```json
{
  "jobId": "RefreshEducationOrganizationsJob-tenant-a-123_65b37e7cba4448f58d4bcbf28f3b04a1",
  "message": "Education organizations refresh has been queued for all instances"
}
```

For `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh`, the message distinguishes the single-data-store request, for example:

```json
{
  "jobId": "RefreshEducationOrganizationsJob-tenant-a-456_f9d7049ebdb04855ac574867c08b76e8",
  "message": "Education organizations refresh has been queued for the specified instance"
}
```

Exact message prose follows the pinned Admin API contract/source when it is normative; otherwise tests should assert operation distinction and `jobId` parity without coupling to prose.

Admin API v3 education-organization read response parity:

`GET /v3/dataStores/edOrgs` and `GET /v3/tenants/{tenantName}/dataStores/edOrgs` return `tenantDetailsResponse` with `id`, `name`, and `dataStores`. `GET /v3/dataStores/{dataStoreId}/edOrgs` returns the pinned single-data-store education-organization response for that ordinary data store. Each data store item uses `id` for the ordinary data-store ID and `dataStoreManageId` for the managed lifecycle ID when available. Unmanaged ordinary stores have `dataStoreManageId`, `databaseTemplate`, and `databaseName` as `null`; pending managed rows with no ordinary link have `id` and `dataStoreType` as `null` and an empty `educationOrganizations` array.

```json
{
  "id": "default",
  "name": "default",
  "dataStores": [
    {
      "id": 3788,
      "dataStoreManageId": null,
      "name": "odsinstance1 tenant1",
      "dataStoreType": "odsinstance1 tenant1",
      "status": "Created",
      "databaseTemplate": null,
      "databaseName": null,
      "educationOrganizations": []
    },
    {
      "id": null,
      "dataStoreManageId": 684,
      "name": "odsinstance1",
      "dataStoreType": null,
      "status": "PendingCreate",
      "databaseTemplate": "Minimal",
      "databaseName": null,
      "educationOrganizations": []
    },
    {
      "id": null,
      "dataStoreManageId": 685,
      "name": "odsinstance2",
      "dataStoreType": null,
      "status": "PendingCreate",
      "databaseTemplate": "Minimal",
      "databaseName": null,
      "educationOrganizations": []
    }
  ]
}
```

Education-organization entries, when present, use this shape inside `educationOrganizations`:

```json
{
  "educationOrganizationId": 255901001,
  "nameOfInstitution": "Grand Bend High School",
  "shortNameOfInstitution": "Grand Bend HS",
  "discriminator": "edfi.School",
  "parentId": 255901
}
```

No `tenantName` property, internal snapshot timestamps, job payload, connection string, or expected source identity is part of these responses unless a future pinned Admin API contract adds it.

**Architecture and boundaries**

CMS owns both the Management API projection and its target-database reader. DMS-1440 isolates the versioned DMS database contract behind CMS provider adapters. CMS does not reference DMS projects, proxy the public DMS API, create service OAuth credentials, or add a reverse service call.

Scheduled refresh mirrors Admin API's logical separation between a recurring trigger and each refresh execution. CMS startup reconciliation creates or updates one stable DMS-1437 schedule per configured tenant from `EdOrgsRefreshIntervalInMins`; each due occurrence enqueues the same refresh-all job used by the manual route. Unlike Admin API's in-memory Quartz trigger, the CMS schedule and its next-run/lease state are durable. The schedule is independent of `EnableDataStoreManagement`.

Each successful target refresh replaces that target's snapshot in one CMS transaction. Failure leaves the previous snapshot intact. An all-target job may keep successful replacements, but if any target fails its aggregate DMS-1437 job ends in `Error` with a bounded sanitized summary.

**Dependencies**

- DMS-1437 durable jobs, schedules, dispatch, and polling.
- DMS-1440 CMS target-database education-organization reader.
- The versioned DMS database contract and configured target-provider setting consumed by DMS-1440.
- Existing CMS ordinary data-store repository, connection-string encryption/decryption, tenant context, and authorization policies.
- Existing [`TenantResolutionMiddleware`](../../../src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Middleware/TenantResolutionMiddleware.cs). Ordinary `GET /v3/dataStores...` routes use the normal request tenant context; `/v3/tenants...` bypass means DMS-1441 must install path-derived tenant context itself for that route.
- DMS-1439 for the complete v3 aggregate's management metadata, pending/unlinked lifecycle entries, and managed-delete cleanup integration. Snapshot persistence and refresh of ordinary unmanaged stores can be developed before DMS-1439 lands.
- The final pinned Admin API/OpenAPI revision for the all-data-store, single-data-store, and tenant aggregate education-organization read response contracts.

**Blockers**

- [DMS-1437](DMS-1437-durable-jobs-and-schedules.md) — must deliver durable manual/scheduled job enqueue, execution, status polling, and schedule dispatch.
- [DMS-1440](DMS-1440-edorg-reader.md) — must deliver the CMS target-database education-organization reader.
- [DMS-1439](DMS-1439-managed-data-store-lifecycle.md) — blocks the complete tenant aggregate and managed-delete snapshot cleanup. Snapshot persistence and refresh for ordinary unmanaged stores can proceed before DMS-1439 completes.
- Contract gate — `ADMINAPI-1496` targets refresh endpoints returning `202 Accepted`; pin the exact OpenAPI/source revision before conformance implementation. The all-data-store, single-data-store, and tenant aggregate GET response contracts must also be pinned before conformance sign-off. Refinement must also confirm the canonical `{tenantName}` accepted in single-tenant mode; CMS currently has no equivalent named setting.

**Out of scope**

- Writing education organizations to DMS or validating application assignments against the snapshot.
- Real-time/event-driven synchronization or change-data capture.
- A CMS-to-DMS HTTP client, service credential, or message broker.
- Deleting or modifying target data-store domain data.

**Risks and implementation considerations**

- Snapshots are eventually consistent by design. Operator documentation must explain manual and scheduled refresh, the configured interval, error status, and retained stale data.
- An incompatible DMS database contract or a target-provider mismatch blocks refresh by design. Deployment documentation must identify the supported contract versions and diagnostics.
- An all-target refresh can be expensive. Bounded concurrency, command timeouts, and job deduplication need provider-backed load validation.
- Long refresh duration, downtime, or lease recovery can make an occurrence late. Operator documentation must explain DMS-1437's single-run coalescing behavior; it must not produce a catch-up burst or overlapping tenant refreshes.

**Non-binding implementation guidance**

- Resolve tenant-scoped aggregate dependencies lazily after installing the path-derived tenant context. Resolving from the request service provider or from a nested scope are both acceptable if lifetime/disposal and tenant isolation tests pass.
- A single-tenant schedule may use a null/internal sentinel tenant key, but that is a persistence choice and must not define the public `{tenantName}` contract.
- Avoid tests coupled to exact `jobQueuedResult.message` prose unless the pinned OpenAPI/reference contract requires exact text.

**Minimum persistence contract**

Provider migrations must use equivalent PostgreSQL and SQL Server types. Exact table and column names should follow CMS conventions, but the snapshot table needs at least:

| Logical column | Portable type intent | Notes |
| --- | --- | --- |
| `tenant` | nullable bounded text/string | Tenant isolation key matching CMS conventions. |
| `dataStoreId` | integer | Ordinary CMS data-store ID. |
| `educationOrganizationId` | `int64`/`bigint`/`long` | Part of uniqueness for one tenant/data store. |
| `nameOfInstitution` | bounded text/string | Non-null. |
| `shortNameOfInstitution` | nullable bounded text/string | Nullable. |
| `discriminator` | bounded text/string | Non-null Admin API value such as `edfi.School`. |
| `parentId` | nullable `int64`/`bigint`/`long` | Nullable direct parent ID. |
| `refreshedAt` | UTC timestamp/date-time | Internal snapshot metadata. |
| `sourceContractVersion` | bounded text/string | Internal diagnostics for compatibility. |

Uniqueness must prevent duplicate `educationOrganizationId` within a tenant/data-store snapshot. Internal refresh/job metadata must not appear in `tenantDetailsResponse` or the single-data-store education-organization read response.

### Acceptance Criteria

1. PostgreSQL and SQL Server CMS schemas persist tenant-scoped snapshot rows with data-store ID, the full v3 education-organization model, and internal refresh timestamps. Uniqueness prevents duplicate organization IDs within one tenant/data store.
2. Refresh-all uses the current tenant's ordinary data stores, including unmanaged stores. It does not attempt to query pending management records that lack an ordinary data-store link.
3. Refresh-one returns `404` when the ordinary data store is absent or belongs to another tenant.
4. Both refresh routes require the existing admin policy, atomically enqueue a `Pending` DMS-1437 job, and return `202 Accepted`, a `/v3/jobs/{jobId}` `Location`, and `jobQueuedResult` with the same job ID. The response message distinguishes refresh-all from refresh-one and follows the pinned contract/reference behavior; exact prose is not an acceptance requirement unless the pinned contract makes it one.
5. The returned job is immediately readable through DMS-1437 and no scheduling race can produce an initial `404`.
6. CMS validates the configured target provider at startup. DMS-1440 validates each target's versioned DMS database contract and required core tables/columns before projection; failures produce actionable diagnostics.
7. A refresh handler establishes the persisted tenant context, resolves/decrypts the existing data-store connection only inside the job scope, invokes DMS-1440, and never persists that connection in the job payload or snapshot.
8. DMS-1440 verifies the target `dms.EffectiveSchema` contract/version and required core objects before returning a projection. A mismatch is non-transient, preserves the previous snapshot, and appears in the sanitized job error.
9. A successful target read transactionally replaces only that tenant/data-store snapshot and removes organizations no longer present. Other tenants and stores are untouched.
10. A target failure preserves its previous complete snapshot, emits secret-safe structured diagnostics, and is classified as transient or non-transient using DMS-1437 retry rules.
11. A refresh-all job that has any target failure ends in `Error` after processing the selected targets, with a bounded sanitized summary. Successful target snapshots remain committed and are not rolled back by another target's failure.
12. Concurrent or scheduled refresh attempts for the same tenant/target are deduplicated or serialized so no older completion can overwrite a newer snapshot.
13. `EdOrgsRefreshIntervalInMins` configures required periodic refresh and must be positive. In multi-tenant mode, startup reconciliation creates/updates one stable active DMS-1437 schedule per current tenant repository record and disables (without deleting history) schedules for removed tenants. In single-tenant mode it maintains one schedule for the canonical single-tenant context. Each occurrence uses the same refresh-all handler, has no HTTP-context dependency, prevents target overlap, and remains active when `EnableDataStoreManagement=false`.
14. `GET /v3/dataStores/edOrgs` uses read-only-or-admin and returns the current tenant's `tenantDetailsResponse` payload. It uses normal CMS tenant resolution, returns existing CMS `400` for missing/invalid tenant context when multi-tenancy is enabled, includes ordinary stores plus DMS-1439 pending/unlinked management rows when available, and never leaks another tenant's records.
15. `GET /v3/dataStores/{dataStoreId}/edOrgs` uses read-only-or-admin and returns the pinned single-data-store education-organization response for one ordinary data store and its current snapshot. It returns `404` when the ordinary data store is absent, belongs to another tenant, or is only a pending managed row without an ordinary data-store ID.
16. `GET /v3/tenants/{tenantName}/dataStores/edOrgs` uses read-only-or-admin and returns `tenantDetailsResponse`. In multi-tenant mode it first requires `Tenant`; missing returns existing CMS `400`. It compares header/path before lookup; mismatch returns existing `400` without existence disclosure. A matching pair is resolved with the non-tenant-scoped tenant repository; unknown returns `404`. Tenant-scoped aggregate dependencies are not resolved or called until `TenantContext.Multitenant` is installed, and scope disposal cannot leak context.
17. In single-tenant mode no `Tenant` header is required and context remains `NotMultitenant`. `{tenantName}` must equal the canonical single-tenant value confirmed during contract refinement or return `404`; this story must not invent a new configuration property or default value without that product decision.
18. Aggregate responses include every ordinary tenant data store with its snapshot. An unmanaged ordinary store has `dataStoreManageId`, `databaseTemplate`, and `databaseName` as `null`, status `Created`, and `dataStoreType` from the ordinary CMS data-store record. Data stores are ordered by numeric ID; embedded education organizations are ordered by numeric ID.
19. When DMS-1439 is present, linked management metadata overlays the ordinary store. Pending or orphaned management records without a linked ordinary store are also present after ordinary stores, ordered by management ID, with `id` and `dataStoreType` as `null`, lifecycle status, nullable `databaseTemplate`/`databaseName` per the pinned contract, and an empty education-organization list.
20. No response or log exposes ordinary/admin connection strings, decrypted secrets, internal job payload, or another tenant's snapshot.
21. Education-organization IDs and parent IDs remain `int64` end to end.
22. Deleting an unmanaged ordinary data store deletes its snapshot in the same CMS transaction. DMS-1439 managed deletion reaches the same cleanup when it removes the linked ordinary row; education-organization read routes never return orphaned snapshot rows after either path.
23. CMS consumes DMS-1440 through its own backend abstraction and provider adapters. No CMS project references a DMS application/library project, internal startup task, request pipeline, or HTTP host.

### Tasks

**Implementation**

1. Pin the Admin API/OpenAPI revision that includes `ADMINAPI-1496` v3 refresh `202 Accepted` behavior and the final all-data-store, single-data-store, and tenant aggregate education-organization GET contracts before route/conformance work.
2. Add provider-equivalent snapshot persistence, replacement/delete transactions, deterministic aggregate reads, and concurrency guards.
3. Add manual refresh enqueue routes and fenced/idempotent DMS-1437 handlers over DMS-1440, including contract-compliant operation-specific messages and truthful partial failure.
4. Add tenant schedule reconciliation for multi- and single-tenant modes using DMS-1437, including tenant removal/history behavior.
5. Add the all-data-store, single-data-store, and late-resolved tenant aggregate read endpoints, merge DMS-1439 metadata, and document refresh consistency, retained-stale-data, scheduling, and tenant-resolution behavior.

**Verification**

- Unit tests for route contracts, exact tenant error semantics, authorization, target-provider startup diagnostics, schedule reconciliation, interval validation, merge/default behavior, all-data-store and single-data-store read behavior, partial failure, database-contract mismatch retention, deduplication, deletion cleanup, and tenant setup.
- PostgreSQL and SQL Server CMS integration tests for snapshot replacement/removal, ordinary and managed delete cleanup, tenant isolation, transactional failure, `int64` IDs, and concurrency.
- API-level tests for both refresh routes, immediate polling, exact tenant `404`/header-mismatch `400` behavior, path-derived repository tenant context, normal tenant-context behavior for `/v3/dataStores...` reads, cross-tenant isolation, `/v3/jobs/{jobId}` `Location`, manual and scheduled job outcomes, all-data-store response shape, single-data-store response shape, tenant response shape, and managed/unmanaged/pending stores.
- Live cross-provider validation against DMS stores containing representative education-organization hierarchies, plus one inaccessible store proving partial success is reported as `Error` and prior data is preserved.
