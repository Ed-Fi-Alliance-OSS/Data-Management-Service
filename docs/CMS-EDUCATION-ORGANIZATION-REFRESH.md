# Configuration Service Education Organization Refresh

The Configuration Service keeps a snapshot of the education organizations in each data store. A refresh job reads
a data store through the [DMS education organization reader](./EDUCATION-ORGANIZATION-PROJECTION.md) and publishes
what it read as that data store's snapshot. `GET /v3/tenants/{tenantName}/dataStores/edOrgs` serves the published
snapshots of a tenant's data stores.

A tenant's data stores are refreshed on a [schedule](#scheduled-refresh), and on request through the
[refresh routes](#refresh-routes).

## Requirements

- **Job workers and the reader.** Refresh jobs run only on instances with `JobSettings:WorkerEnabled=true`, which
  require the reader settings at startup. An instance in bootstrap queues refresh jobs and runs none of them. See
  [Bootstrap and operational modes](./CONFIGURATION.md#bootstrap-and-operational-modes).
- **The scheduler.** Schedules are created and dispatched only by instances with `JobSettings:SchedulerEnabled=true`
  (the default).
- **The same settings on every instance.** `AppSettings:EdOrgsRefreshIntervalInMins` and `AppSettings:MultiTenancy`
  must have the same value on every instance with the scheduler on. Each instance writes its own value to the
  schedules, so different values make the schedules follow whichever instance reconciled last.
- **Every worker on this release.** A worker of an earlier release cannot run the `EducationOrganization.Refresh` job
  type and fails such a job as `UnsupportedJobType`. An upgraded instance with the scheduler on creates every
  tenant's schedule at startup, with its first refresh due immediately, and the refresh routes queue a refresh as
  soon as they are called. While instances of the earlier release still run workers:

  1. Start each upgraded instance with `JobSettings:SchedulerEnabled=false`.
  2. Do not call the [refresh routes](#refresh-routes).
  3. Once every worker runs this release with its reader configured (see the first requirement), set
     `JobSettings:SchedulerEnabled` back to its intended value on the instances that should run the scheduler, and
     restart them. Their startup [reconciliation](#scheduled-refresh) creates the schedules.

## Refresh routes

| Route | Refreshes | Responses |
| --- | --- | --- |
| `POST /v3/dataStores/edOrgs/refresh` | every data store of the caller's tenant | `202` |
| `POST /v3/dataStores/{dataStoreId}/edOrgs/refresh` | one data store of the caller's tenant | `202`; `400` for a non-integer `dataStoreId`; `404` "Data store not found." when the tenant has no such data store, with no job queued |

Both routes require the administrator scope (`edfi_admin_api/full_access`); a read-only client gets `403`. See
[Roles and scopes](./ROLES-SCOPES.md). In multi-tenant mode the `Tenant` header selects the tenant, as for every
Configuration Service route; in single-tenant mode it is ignored.

A `202` means the refresh was queued, not that it ran. Its body is
`{ "jobId": "...", "message": "Education organizations refresh has been queued for all instances" }` (or
`"...for the specified instance"`), and `Location` is the job's status route, `/v3/jobs/{jobId}`, relative to the
request's path base. Poll it with `GET /v3/jobs/{jobId}` (see
[Polling endpoint](./CONFIGURATION-SERVICE-BACKGROUND-JOBS.md#polling-endpoint)) until `status` is `Completed` or
`Error`; [Job outcomes and errors](#job-outcomes-and-errors) explains both.

A failed existence check or a failed enqueue returns `500` without `Location` and logs `RefreshRequestFailed`. The
enqueue was not confirmed: a lost acknowledgement can leave a queued job that the caller has no identifier for.

## Tenant aggregate

`GET /v3/tenants/{tenantName}/dataStores/edOrgs` returns a tenant with its data stores and each data store's published
education organizations. It requires the read-only or administrator scope.

**Tenant resolution.**

- **Multi-tenant mode.** The `Tenant` header is required (`400` with the tenant middleware's message when it is
  missing) and must name the same tenant as the path, ignoring case (`400` "The 'Tenant' header does not match the
  tenant name in the route." otherwise). An unknown tenant is `404` "Tenant not found.". `id` and `name` hold the
  tenant's canonical name.
- **Single-tenant mode.** The only tenant is `default`, in any case; any other name is `404`. The `Tenant` header is
  ignored.

Tenant selection is routing, not authorization: any caller with the scope can read any tenant. Configuration Service
tokens carry no tenant claim.

**Response.**

```json
{
  "id": "Tenant1",
  "name": "Tenant1",
  "dataStores": [
    {
      "id": 2,
      "dataStoreManageId": null,
      "name": "District 255901 2025",
      "dataStoreType": "District",
      "status": "Created",
      "databaseTemplate": null,
      "databaseName": null,
      "educationOrganizations": [
        {
          "educationOrganizationId": 255901,
          "nameOfInstitution": "Grand Bend ISD",
          "shortNameOfInstitution": "GBISD",
          "discriminator": "edfi.LocalEducationAgency",
          "parentId": null
        }
      ]
    }
  ]
}
```

- Data stores are in identifier order, and each data store's education organizations in `educationOrganizationId`
  order. Every property is written, `null` included.
- A data store that is not managed has `status` `"Created"` and `null` management fields. A
  [managed](./CMS-MANAGED-DATA-STORES.md) data store carries its management record's identifier, status, template
  and database name. A management record linked to no data store follows the data stores, with `null` `id` and
  `dataStoreType` and no education organizations.
- A data store that no refresh has published yet has no education organizations.
- A failed read is `500` and logs `TenantAggregateReadFailed`.

**Excluded routes.** `GET /v3/dataStores/edOrgs` and `GET /v3/dataStores/{dataStoreId}/edOrgs` are not part of the
API. Both answer `404`, as an unknown route does, and neither is in the OpenAPI document.

**Differences from the upstream Admin API contract.**

- (a) Job identifiers are GUIDs.
- (b) There is no Swagger-referer bypass.
- (c) Problem types use the `urn:ed-fi:management-api:` prefix.
- (d) Schema and tag names are the Configuration Service generator's. The tenant aggregate carries the `DataStores`
  tag, as the refresh routes do, rather than `Tenants`.
- (e) `id` and `name` hold the tenant's canonical name.

## Consistency

**Retained data.** A refresh that does not complete for a data store leaves that data store's previous snapshot in
place. The aggregate can therefore show education organizations that DMS no longer has, until a later refresh of that
data store succeeds. A refresh of every data store publishes each data store that it read successfully, whatever
happens to the others. Deleting a data store deletes its snapshot.

**Consistent reads.** One aggregate read sees one state: a data store change or a refresh publication lands either
wholly before the read or wholly after it. On PostgreSQL the read uses a read-only, repeatable-read transaction and
blocks nobody.

**SQL Server holds a shared tenant lock while it reads.** On SQL Server, the read holds the tenant's shared catalog
lock and the data stores' snapshot locks until it commits. Writes to the tenant's data store catalog wait for that
lock for at most 5 seconds. That includes creating and deleting data stores, and publishing a managed data store.
A large tenant read over a slow connection can hold the lock longer: about 4.4 to 8.8 seconds was measured for 50,000
education organizations over a slow link, against about 0.2 seconds locally. A write that waits past its bound fails:
an HTTP request answers `500` and is rolled back, with no automatic retry, and a managed data store job retries as a
transient failure. Keep tenant reads away from bulk catalog changes, or retry the write.

**SQL Server aggregate reads can also delay refresh lease renewals.** The snapshot locks that a tenant read holds
also block the write that renews a refresh's [data store lease](#data-store-leases). A renewal waits for at most half
of `JobSettings:RenewalInterval`, less one second. If the read holds its locks longer, the refresh can no longer be
sure it still holds the lease: it logs `EducationOrganizationTargetLeaseLost` (`RenewalUncertain`), cancels its read
of that data store, and its job retries like any transient failure. This is separate from the catalog writes above
and their 5-second bound.

**At-least-once reads.** Every attempt of a refresh job reads every data store it covers again, including data stores
an earlier attempt or another job already published. A later publication of the same data store replaces the earlier
one.

## Data store leases

A refresh reads and publishes one data store at a time, holding a lease on that data store while it does. The lease
lasts `JobSettings:LeaseDuration` and is renewed every `JobSettings:RenewalInterval` during the read (see
[JobSettings](./CONFIGURATION.md#jobsettings)).

- A refresh that finds the data store's lease held by another refresh logs `EducationOrganizationTargetBusy` and moves
  on to its next data store. Its job then retries while attempts remain, and otherwise fails with
  `EdOrgRefreshTargetBusy`.
- A refresh that loses its lease (because a renewal fails, or the lease expired and another refresh took it over)
  stops reading that data store and logs `EducationOrganizationTargetLeaseLost`. Its job retries like any transient
  failure.
- Only the refresh that holds a data store's live lease can write that data store's snapshot. Publications therefore
  happen in the order the leases were acquired, and an older read never replaces a newer snapshot.

**Limitation: a delayed refresh can still read.** A refresh that is delayed for longer than `LeaseDuration` between
renewing its lease and sending its DMS requests can still send them after the lease has expired and another refresh
has taken it over. Such a delay comes from a suspended process or a starved thread. Both refreshes then read the same
data store at the same time. The delayed refresh cannot write: every write checks the lease again under a row lock,
so it can neither stage nor publish nor remove another refresh's rows. Under normal scheduling its next renewal
fails and cancels its read; under starvation or suspension, no upper bound on that overlap is guaranteed. The
overlap only costs DMS reads.

## Scheduled refresh

Each tenant has one schedule of type `EducationOrganization.ScheduledRefresh`. In single-tenant mode, there is one
schedule for the deployment. Every `AppSettings:EdOrgsRefreshIntervalInMins` minutes (default `60`, from 1 to 527040)
it enqueues an `EducationOrganization.Refresh` job for every data store of the tenant.

**Reconciliation.** Every instance with the scheduler on keeps the schedules in line with the tenants. It does this at
startup, and again 5 minutes after each run completes. Each run does two things:

- It creates or updates the schedule of every tenant.
- It disables the schedules of tenants that no longer exist, and the schedules of the other tenancy mode after a
  change of `AppSettings:MultiTenancy`.

A disabled schedule keeps its identifier and its past jobs, so a schedule that becomes active again keeps the same
identifier.

The Configuration Service has no API to delete a tenant. A tenant's schedules and jobs reference its `dmscs.Tenant`
row through foreign keys that restrict deletion, so once a tenant has a schedule or a job, deleting its row directly
in the database fails as well.

**Timing.** A new tenant is scheduled by a later reconciliation run, not within a fixed time. Each run starts 5 minutes
after the previous one completed. Reading the tenants and writing their schedules add to that delay, and a run that
fails leaves the tenant to the next run. A new schedule's first refresh is due immediately, so the tenant's data stores
are refreshed as soon as the schedule exists and a worker is free.

**Changing the interval.** The interval is read at startup. After the setting changes, each instance writes the new
interval at its startup reconciliation, so restart every instance with the scheduler on. The next refresh is then due
at the earlier of the time already scheduled and the current time plus the new interval.

**Skips and missed occurrences.** The
[job framework's schedule rules](./CONFIGURATION-SERVICE-BACKGROUND-JOBS.md#schedules) apply:

- While the schedule's previous refresh is still `Pending` (including one waiting to retry) or `InProgress`, a due
  occurrence is skipped and `ScheduleOccurrenceSkippedActiveJob` is logged. A slow or retrying refresh therefore never
  piles up scheduled refreshes behind it.
- After downtime spanning several intervals, one refresh is enqueued, and missed occurrences are not replayed.

**Scheduled and manual refreshes.** A manual refresh is an ordinary job that belongs to no schedule. The skip rule
does not see it, so a scheduled and a manual refresh can both be queued at the same time. The
[data store leases](#data-store-leases) serialize them: while one refresh holds a data store's lease, the other finds
the data store busy, still refreshes its other data stores, and retries after the `JobSettings` retry backoff while
attempts remain. The leases serialize publication; a delayed refresh can still read past its lease, as the limitation
above describes.

In the `dmscs.Job` table, a scheduled refresh has `SourceScheduleId` (its schedule's `dmscs.JobSchedule.Id`) and
`ScheduledOccurrence` set, and a manual refresh has neither. No API lists jobs: `GET /v3/jobs/{jobId}` reads one job
by the identifier a refresh request returned.

## Job outcomes and errors

`Completed` means every data store the job covered was refreshed, or was deleted during a refresh of every data store.
`Error` means the refresh was **not confirmed complete**: some data stores may have been published before the job
failed, and the others keep their previous snapshots.

A transient failure (DMS or the data store unavailable, a busy or lost lease, a Configuration Service database failure)
is retried while attempts remain (`JobSettings:MaxAttempts`, with the `JobSettings` retry backoff). A permanent
failure, such as a target DMS rejects, is not retried on its own; when the same attempt also has a transient failure,
the job retries and reads every data store again.

**A refresh of one data store** fails with one code and its fixed message:

| Code | Message |
| --- | --- |
| `EdOrgRefreshDataStoreNotFound` | The data store no longer exists; its education organizations were not refreshed. |
| `EdOrgRefreshTemporarilyUnavailable` | DMS, the data store, or the Configuration Service database was temporarily unavailable. |
| `EdOrgRefreshTargetBusy` | The data store was being refreshed by another job. |
| `EdOrgProjection*` | The reader's permanent failures; see [Failure classification](./EDUCATION-ORGANIZATION-PROJECTION.md#failure-classification). |

**A refresh of every data store** fails with the composite code `EdOrgRefreshIncomplete`. Its message lists each
distinct problem category, in the order of the first data store (by identifier) that produced it, then adds each
category's message while the text stays within 1000 characters:

```text
Education organizations could not be refreshed for one or more data stores, whose previously refreshed education organizations were kept. Problem categories: EdOrgProjectionTargetSchemaIncompatible, EdOrgRefreshTemporarilyUnavailable. The data store's database is not compatible with the education organization projection. DMS, the data store, or the Configuration Service database was temporarily unavailable.
```

Every category code is always listed; only the messages after it can be cut. The categories are the 13
`EdOrgProjection*` codes, `EdOrgRefreshTemporarilyUnavailable` and `EdOrgRefreshTargetBusy`. A failed lookup of the
tenant's data stores attempts none of them, so on the final attempt it is `EdOrgRefreshTemporarilyUnavailable` for one
data store, and the composite listing only that category for every data store. See
[Composite error codes](./CONFIGURATION-SERVICE-BACKGROUND-JOBS.md#composite-error-codes).

**Limitations.**

- A job that runs out of attempts without a final handler run fails with `AttemptsExhausted` and no composite: after a
  crash, a shutdown during its final attempt, or a lowered `MaxAttempts`. Data stores it already published stay
  published.
- A job whose final publication committed but whose acknowledgement was lost, with no attempt left, also shows
  `AttemptsExhausted` although its data was published.

## Log events

Event names are the `EventId` names. Values are sanitized; URLs, tokens, cursors, bodies, payloads, exception
messages and connection data are never logged.

| Event | Level | When |
| --- | --- | --- |
| `EducationOrganizationTargetRefreshed` | Information | A data store was published: tenant, data store, generation, item count, contract version, page count. |
| `EducationOrganizationTargetBusy` | Information | Another refresh held the data store's lease. |
| `EducationOrganizationTargetLeaseLost` | Warning | The refresh lost the data store's lease: `Expired`, `Taken` or `RenewalUncertain`. |
| `EducationOrganizationTargetRemoved` | Information | The data store was deleted during its refresh. |
| `EducationOrganizationTargetRefreshFailed` | Warning | A data store's refresh failed in the reader or in the Configuration Service database. A reader failure has category, code, stage, HTTP status, problem type, correlation ID and pages read. A database failure has the `Transient` category, the refresh step, the exception type chain, the provider error code and the failed database operation. |
| `EducationOrganizationRefreshFinished` | Information or Warning | A job attempt ended: counts and the decision (`Completed`, `Retry`, `Failed`). |
| `EducationOrganizationSnapshotPruneFailed` | Warning | Removing a replaced generation failed; the next refresh of the data store removes it. |
| `EducationOrganizationLeaseReleaseFailed` | Warning | Releasing a lease failed; it expires on its own. |
| `EducationOrganizationScheduleReconciled` | Information | A reconciliation run ended, with its counts. |
| `EducationOrganizationScheduleReconciliationFailed` | Warning | A reconciliation operation failed; the next run retries it. |
| `JobCompositeErrorRejected` | Warning | A composite failure did not meet the registered contract; the job failed as `HandlerFailed`. |
| `JobWorkersDisabled` | Warning | At startup, on an instance with job workers off. |
| `RefreshRequestFailed` | Error | A refresh route's existence check or enqueue failed. |
| `TenantAggregateReadFailed` | Error | The tenant aggregate's tenant lookup or read failed. |

## Checking that scheduled refresh runs

After an instance moves from bootstrap to operational:

1. Its log shows `EducationOrganizationScheduleReconciled` at startup, with the numbers of schedules written,
   disabled and kept, and of failures. `EducationOrganizationScheduleReconciliationFailed` names the operation that
   failed, and the next run retries it.
2. For each occurrence, the log shows `ScheduleOccurrenceEnqueued`, then `EducationOrganizationTargetRefreshed` for
   each data store and `EducationOrganizationRefreshFinished` for the job.
3. `GET /v3/tenants/{tenantName}/dataStores/edOrgs` shows each data store's education organizations once the first
   scheduled refresh has completed. With processing healthy and idle, a change made through DMS appears after about
   one interval plus the refresh's own run time. A skipped occurrence behind an active refresh, retries, busy data
   stores and outages all extend that.

`./build-dms.ps1 InstanceE2ETest` exercises this sequence end to end. It starts the Configuration Service in
bootstrap, creates each tenant's reader claim set and application, and recreates the service with workers, the
scheduler and the reader on and a one-minute interval. The scheduled-refresh test then checks four things, with no
manual refresh until the last one:

- the tenant's enabled schedule;
- a completed scheduled job that published the seeded hierarchy;
- a completed job of a later occurrence that published a school renamed through DMS;
- a manual refresh that completes with no schedule.

See the
[Instance Management E2E README](../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md#operational-configuration-service).
