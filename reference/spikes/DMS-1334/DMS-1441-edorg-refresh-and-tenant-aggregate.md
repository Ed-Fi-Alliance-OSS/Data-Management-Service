# DMS-1441: Add CMS education-organization refresh, projection, and tenant aggregation

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Create the CMS education-organization refresh, snapshot, and read workflow required for Management API v3 parity.

- Refresh endpoints: `POST /v3/dataStores/edOrgs/refresh` and `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh`, returning `202 Accepted`, `jobQueuedResult`, and a job-status `Location`.
- Read endpoint (if single tenant use tenantName “default”): `GET /v3/tenants/{tenantName}/dataStores/edOrgs`.
- Persistence: tenant-scoped snapshots keyed by data store and education-organization ID, with transactional replacement and cleanup on ordinary or managed data-store deletion.
- Execution: DMS-1437 manual and scheduled jobs call the DMS-1440 authenticated HTTP reader, preserve prior snapshots on failed DMS/target reads, and report partial refresh failure truthfully.
- Aggregate behavior: the tenant read route uses a snapshot-backed query model and overlays DMS-1439 managed lifecycle metadata, including pending managed rows with no ordinary data-store link.

### Description

CMS stores application education-organization IDs but cannot discover the organizations present in each configured data store, refresh a durable projection, expose refresh job status, or return the tenant education-organization read required by Management API v3.

This story delivers tenant-scoped snapshots, mandatory manual refresh, the tenant aggregate route, and required scheduled refresh matching pinned Admin API behavior.

**Scope**

- Add provider-equivalent CMS snapshot persistence keyed by tenant, data store, and education-organization ID, including refresh metadata.
- Add refresh-all and refresh-one endpoints that enqueue DMS-1437 jobs and return the v3 job response.
- Add refresh job handlers that resolve ordinary CMS data-store IDs, call the DMS-1440 authenticated HTTP reader, and transactionally replace snapshots per successful target only after all pages for that target are read.
- Add configurable scheduled refresh per tenant using DMS-1437's durable `Schedules` and `Jobs` persistence, the same refresh-all handler as manual refresh, and the same duplicate-suppression rules.
- Add `GET /v3/tenants/{tenantName}/dataStores/edOrgs`; it reads from the tenant-scoped snapshot/aggregate query model and merges ordinary data stores, snapshots, and DMS-1439 management metadata where the route shape requires it.
- Remove a data store's snapshot when its ordinary CMS catalog row is deleted, including deletion through DMS-1439 managed lifecycle.
- Add truthful partial-failure, stale-snapshot, retry, DMS service-authentication, authorization, observability, and tenant-isolation behavior.

**API surface**

- `POST /v3/dataStores/edOrgs/refresh`
- `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh`
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

`GET /v3/tenants/{tenantName}/dataStores/edOrgs` returns `tenantDetailsResponse` with `id`, `name`, and `dataStores`. Each data store item uses `id` for the ordinary data-store ID and `dataStoreManageId` for the managed lifecycle ID when available. Unmanaged ordinary stores have `dataStoreManageId`, `databaseTemplate`, and `databaseName` as `null`; pending managed rows with no ordinary link have `id` and `dataStoreType` as `null` and an empty `educationOrganizations` array.

Per `ADMINAPI-1488`, `GET /v3/dataStores/{dataStoreId}/edOrgs` returns `404` and the advertised unscoped `GET /v3/dataStores/edOrgs` route was an unimplemented documentation error. DMS-1441 does not implement either as a successful parity endpoint unless product explicitly decides to diverge from Admin API.

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

CMS owns the Management API refresh workflow, schedules, retries, job status, snapshot persistence, and tenant aggregate responses. DMS owns the domain-data read and exposes the education-organization projection through DMS-1440's authenticated HTTP contract. CMS does not reference DMS projects, read DMS domain tables, validate `dms.EffectiveSchema`, or depend on generated DMS DDL for education-organization refresh.

CMS calls the DMS-1440 reader with the current tenant and the CMS ordinary `dataStoreId`, follows the DMS-discovered endpoint template, authenticates with a least-privilege service token, and pages until `nextCursor` is null. The refresh handler commits a replacement snapshot only after a complete successful read for that one target. A failed Discovery call, token request, DMS request, page read, malformed response, timeout, cancellation, or unsuccessful HTTP status leaves that target's previous snapshot unchanged.

Scheduled refresh mirrors Admin API's logical separation between a recurring trigger and each refresh execution. CMS startup reconciliation creates or updates one stable DMS-1437 schedule per configured tenant from `EdOrgsRefreshIntervalInMins`; each due occurrence enqueues the same refresh-all job used by the manual route. Unlike Admin API's in-memory Quartz trigger, the CMS schedule and its next-run/lease state are durable. The schedule is independent of `EnableDataStoreManagement`.

Each successful target refresh replaces that target's snapshot in one CMS transaction. Failure leaves the previous snapshot intact, including when the DMS instance is unavailable or has not yet discovered a newly registered data store. An all-target job may keep successful replacements, but if any target fails its aggregate DMS-1437 job ends in `Error` with a bounded sanitized summary.

**Dependencies**

- DMS-1437 durable jobs, schedules, dispatch, and polling.
- DMS-1440 authenticated DMS education-organization projection endpoint and CMS HTTP reader.
- Existing CMS ordinary data-store repository, tenant context, authorization policies, and HTTP client infrastructure.
- Existing [`TenantResolutionMiddleware`](../../../src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Middleware/TenantResolutionMiddleware.cs). The `/v3/tenants...` bypass means DMS-1441 must install path-derived tenant context itself for that route.
- DMS-1439 for the complete v3 aggregate's management metadata, pending/unlinked lifecycle entries, and managed-delete cleanup integration. Snapshot persistence and refresh of ordinary unmanaged stores can be developed before DMS-1439 lands.
- The final pinned Admin API/OpenAPI revision for the tenant aggregate education-organization read response contract.

**Blockers**

- [DMS-1437](DMS-1437-durable-jobs-and-schedules.md) — must deliver durable manual/scheduled job enqueue, execution, status polling, and schedule dispatch.
- [DMS-1440](DMS-1440-edorg-reader.md) — must deliver the DMS authenticated projection endpoint and CMS HTTP reader.
- [DMS-1439](DMS-1439-managed-data-store-lifecycle.md) — blocks the complete tenant aggregate and managed-delete snapshot cleanup. Snapshot persistence and refresh for ordinary unmanaged stores can proceed before DMS-1439 completes.
- Contract gate — `ADMINAPI-1496` targets refresh endpoints returning `202 Accepted`; pin the exact OpenAPI/source revision before conformance implementation. The tenant aggregate GET response contract must also be pinned before conformance sign-off. In single-tenant mode, Admin API accepts only its default tenant name `default`, so CMS should support `/v3/tenants/default/dataStores/edOrgs` unless a future pinned contract changes that value.

**Out of scope**

- Writing education organizations to DMS or validating application assignments against the snapshot.
- Real-time/event-driven synchronization or change-data capture.
- Direct CMS SQL reads from DMS domain tables or a versioned CMS relational read contract.
- A message broker.
- Deleting or modifying target data-store domain data.
- Administrative template restoration, database lifecycle, and physical deletion; those remain DMS-1438/DMS-1439 concerns and may still require administrative database credentials.
- Successful `GET /v3/dataStores/edOrgs` or `GET /v3/dataStores/{dataStoreId}/edOrgs` responses; those routes are excluded from Admin API parity by `ADMINAPI-1488` unless product explicitly decides to diverge.

**Risks and implementation considerations**

- Snapshots are eventually consistent by design. Operator documentation must explain manual and scheduled refresh, the configured interval, error status, and retained stale data.
- DMS endpoint, token, or target unavailability blocks only the affected refresh attempt by design. Deployment documentation must identify the DMS base/discovery configuration, service credential, supported projection contract versions, and diagnostics.
- An all-target refresh can be expensive. Bounded concurrency, per-request and total-target timeouts, pagination limits, response-size handling, and job deduplication need validation against representative DMS deployments.
- Long refresh duration, downtime, or lease recovery can make an occurrence late. Operator documentation must explain DMS-1437's single-run coalescing behavior; it must not produce a catch-up burst or overlapping tenant refreshes.

**Non-binding implementation guidance**

- Resolve tenant-scoped aggregate dependencies lazily after installing the path-derived tenant context. Resolving from the request service provider or from a nested scope are both acceptable if lifetime/disposal and tenant isolation tests pass.
- A single-tenant schedule may use a null/internal sentinel tenant key, but that is a persistence choice and must not change the public `{tenantName}` contract. The single-tenant public route uses Admin API's default tenant name `default`.
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
| `sourceContractVersion` | bounded text/string | Internal diagnostics for the DMS HTTP projection contract version, for example `educationOrganizationProjection.v1`. |

Uniqueness must prevent duplicate `educationOrganizationId` within a tenant/data-store snapshot. Internal refresh/job metadata must not appear in `tenantDetailsResponse`.

### Acceptance Criteria

1. PostgreSQL and SQL Server CMS schemas persist tenant-scoped snapshot rows with data-store ID, the full v3 education-organization model, and internal refresh timestamps. Uniqueness prevents duplicate organization IDs within one tenant/data store.
2. Refresh-all uses the current tenant's ordinary data stores, including unmanaged stores. It does not attempt to query pending management records that lack an ordinary data-store link.
3. Refresh-one returns `404` when the ordinary data store is absent or belongs to another tenant.
4. Both refresh routes require the existing admin policy, atomically enqueue a `Pending` DMS-1437 job, and return `202 Accepted`, a `/v3/jobs/{jobId}` `Location`, and `jobQueuedResult` with the same job ID. The response message distinguishes refresh-all from refresh-one and follows the pinned contract/reference behavior; exact prose is not an acceptance requirement unless the pinned contract makes it one.
5. The returned job is immediately readable through DMS-1437 and no scheduling race can produce an initial `404`.
6. CMS validates DMS endpoint discovery, service-authentication settings, timeout bounds, page-size bounds, and supported projection contract versions at startup where configuration is static. Dynamic Discovery or token failures are classified inside the refresh job.
7. A refresh handler establishes the persisted tenant context, resolves the current ordinary data-store IDs only inside the job scope, invokes DMS-1440 over authenticated HTTP, and never persists service tokens, connection strings, endpoint secrets, cursors, or raw HTTP bodies in the job payload or snapshot.
8. DMS-1440 owns validation of internal schema/mapping state and required core data relationships before returning a projection. Non-transient DMS problem types preserve the previous snapshot and appear in the sanitized job error without exposing physical schema details.
9. CMS follows DMS pagination until `nextCursor` is null and treats each cursor as opaque. A successful complete target read transactionally replaces only that tenant/data-store snapshot and removes organizations no longer present. Other tenants and stores are untouched.
10. A target failure, unavailable DMS instance, unavailable target data store, failed page, malformed response, timeout, or cancellation preserves the previous complete snapshot, emits secret-safe structured diagnostics, and is classified as transient or non-transient using DMS-1437 retry rules.
11. A refresh-all job that has any target failure ends in `Error` after processing the selected targets, with a bounded sanitized summary. Successful target snapshots remain committed and are not rolled back by another target's failure.
12. Concurrent or scheduled refresh attempts for the same tenant/target are deduplicated or serialized so no older completion can overwrite a newer snapshot.
13. `EdOrgsRefreshIntervalInMins` configures required periodic refresh and must be positive. In multi-tenant mode, startup reconciliation creates/updates one stable active DMS-1437 schedule per current tenant repository record and disables (without deleting history) schedules for removed tenants. In single-tenant mode it maintains one schedule for the canonical single-tenant context. Each occurrence uses the same refresh-all handler, has no HTTP-context dependency, prevents target overlap, and remains active when `EnableDataStoreManagement=false`.
14. DMS-1441 does not implement successful `GET /v3/dataStores/edOrgs` or `GET /v3/dataStores/{dataStoreId}/edOrgs` Management API parity endpoints. The per-data-store route returns `404` for Admin API parity; the unscoped all-data-store route remains absent or `404` unless product explicitly pins a divergence.
15. `GET /v3/tenants/{tenantName}/dataStores/edOrgs` uses read-only-or-admin and returns `tenantDetailsResponse`. In multi-tenant mode it first requires `Tenant`; missing returns existing CMS `400`. It compares header/path before lookup; mismatch returns existing `400` without existence disclosure. A matching pair is resolved with the non-tenant-scoped tenant repository; unknown returns `404`. Tenant-scoped aggregate dependencies are not resolved or called until `TenantContext.Multitenant` is installed, and scope disposal cannot leak context.
16. In single-tenant mode no `Tenant` header is required and context remains `NotMultitenant`. `{tenantName}` must equal Admin API's default tenant name `default` or return `404`; the supported path is `/v3/tenants/default/dataStores/edOrgs`.
17. Aggregate responses include every ordinary tenant data store with its snapshot. An unmanaged ordinary store has `dataStoreManageId`, `databaseTemplate`, and `databaseName` as `null`, status `Created`, and `dataStoreType` from the ordinary CMS data-store record. Data stores are ordered by numeric ID; embedded education organizations are ordered by numeric ID.
18. When DMS-1439 is present, linked management metadata overlays the ordinary store. Pending or orphaned management records without a linked ordinary store are also present after ordinary stores, ordered by management ID, with `id` and `dataStoreType` as `null`, lifecycle status, nullable `databaseTemplate`/`databaseName` per the pinned contract, and an empty education-organization list.
19. No response or log exposes ordinary/admin connection strings, decrypted secrets, internal job payload, or another tenant's snapshot.
20. Education-organization IDs and parent IDs remain `int64` end to end.
21. Deleting an unmanaged ordinary data store deletes its snapshot in the same CMS transaction. DMS-1439 managed deletion reaches the same cleanup when it removes the linked ordinary row; education-organization read routes never return orphaned snapshot rows after either path.
22. CMS consumes DMS-1440 through its own backend HTTP abstraction. No CMS project references a DMS application/library project, internal startup task, request pipeline, generated DDL, or DMS domain database schema.

### Tasks

**Implementation**

1. Pin the Admin API/OpenAPI revision that includes `ADMINAPI-1496` v3 refresh `202 Accepted` behavior and the final tenant aggregate education-organization GET contract before route/conformance work.
2. Add provider-equivalent snapshot persistence, replacement/delete transactions, deterministic aggregate reads, and concurrency guards.
3. Add manual refresh enqueue routes and fenced/idempotent DMS-1437 handlers over the DMS-1440 HTTP reader, including contract-compliant operation-specific messages, endpoint discovery, token acquisition, paging, timeout/cancellation, and truthful partial failure.
4. Add tenant schedule reconciliation for multi- and single-tenant modes using DMS-1437, including tenant removal/history behavior.
5. Add the late-resolved tenant aggregate read endpoint, merge DMS-1439 metadata, and document refresh consistency, retained-stale-data, scheduling, tenant-resolution behavior, and excluded GET-route parity.

**Verification**

- Unit tests for route contracts, exact tenant error semantics, authorization, DMS endpoint/service-auth startup diagnostics, schedule reconciliation, interval validation, merge/default behavior, tenant aggregate read behavior, partial failure, DMS problem-type retention, deduplication, deletion cleanup, and tenant setup.
- PostgreSQL and SQL Server CMS integration tests for snapshot replacement/removal, ordinary and managed delete cleanup, tenant isolation, transactional failure, `int64` IDs, and concurrency.
- API-level tests for both refresh routes, immediate polling, exact tenant `404`/header-mismatch `400` behavior, unsupported GET-route absence/`404` behavior, path-derived repository tenant context, cross-tenant isolation, `/v3/jobs/{jobId}` `Location`, manual and scheduled job outcomes, tenant response shape, and managed/unmanaged/pending stores.
- Live cross-component validation against DMS stores containing representative education-organization hierarchies, plus unavailable DMS, inaccessible target, timeout, and multi-page responses proving partial success is reported as `Error` and prior data is preserved.
