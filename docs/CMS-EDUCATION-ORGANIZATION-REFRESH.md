# Configuration Service Education Organization Refresh

The Configuration Service keeps a snapshot of the education organizations in each data store. A refresh job reads
a data store through the [DMS education organization reader](./EDUCATION-ORGANIZATION-PROJECTION.md) and publishes
what it read as that data store's snapshot. `GET /v3/tenants/{tenantName}/dataStores/edOrgs` serves the published
snapshots of a tenant's data stores.

A tenant's data stores are refreshed on a schedule, and on request through
`POST /v3/dataStores/edOrgs/refresh` (every data store of the caller's tenant) and
`POST /v3/dataStores/{dataStoreId}/edOrgs/refresh` (one data store).

## Requirements

- **Job workers and the reader.** Refresh jobs run only on instances with `JobSettings:WorkerEnabled=true`, which
  require the reader settings at startup. An instance in bootstrap queues refresh jobs and runs none of them. See
  [Bootstrap and operational modes](./CONFIGURATION.md#bootstrap-and-operational-modes).
- **The scheduler.** Schedules are created and dispatched only by instances with `JobSettings:SchedulerEnabled=true`
  (the default).
- **The same settings on every instance.** `AppSettings:EdOrgsRefreshIntervalInMins` and `AppSettings:MultiTenancy`
  must have the same value on every instance with the scheduler on. Each instance writes its own value to the
  schedules, so different values make the schedules follow whichever instance reconciled last.

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
does not see it, so a scheduled and a manual refresh can both be queued at the same time. They never read the same
data store at the same time. A refresh holds a lease on each data store while it reads and publishes that data store.
A refresh that finds the data store held logs `EducationOrganizationTargetBusy` and still refreshes the job's other
data stores. While attempts remain, the job is then retried after the `JobSettings` retry backoff.

In the `dmscs.Job` table, a scheduled refresh has `SourceScheduleId` (its schedule's `dmscs.JobSchedule.Id`) and
`ScheduledOccurrence` set, and a manual refresh has neither. No API lists jobs: `GET /v3/jobs/{jobId}` reads one job
by the identifier a refresh request returned.

## Checking that scheduled refresh runs

After an instance moves from bootstrap to operational:

1. Its log shows `EducationOrganizationScheduleReconciled` at startup, with the numbers of schedules written,
   disabled and kept, and of failures. `EducationOrganizationScheduleReconciliationFailed` names the operation that
   failed, and the next run retries it.
2. For each occurrence, the log shows `ScheduleOccurrenceEnqueued`, then `EducationOrganizationTargetRefreshed` for
   each data store and `EducationOrganizationRefreshFinished` for the job.
3. `GET /v3/tenants/{tenantName}/dataStores/edOrgs` shows each data store's education organizations once the first
   scheduled refresh has completed. A change made through DMS appears after a later scheduled refresh, at most about
   one interval plus the refresh's own run time after the change.

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
