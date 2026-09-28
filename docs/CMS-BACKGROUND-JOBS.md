# Configuration Service Background Jobs

The DMS Configuration Service (CMS) runs durable background jobs in-process, with no broker and no
extra deployable service. A job is a row in the CMS database: consumers enqueue it inside their
own CMS transaction, a worker on any CMS instance claims and runs it under a lease, and clients
poll its status through `GET /v3/jobs/{jobId}`. Recurring work is described by schedules, which a
dispatcher turns into ordinary jobs.

This release provides the infrastructure only. Job types are registered by later features
(managed data-store lifecycle and education-organization refresh); there is no public API to
enqueue a job or manage a schedule.

The design record, with every decision and its evidence, is
[`reference/design/jobs-DMS-1437/spec.md`](../reference/design/jobs-DMS-1437/spec.md).

## Components

| Component                      | Runs when                        | Does                                                                                   |
| ------------------------------ | -------------------------------- | -------------------------------------------------------------------------------------- |
| Worker                         | `JobSettings:WorkerEnabled`      | Exhausts jobs out of attempts, claims eligible jobs, and runs up to `MaxConcurrentJobs` |
| Schedule dispatcher            | `JobSettings:SchedulerEnabled`   | Enqueues one job for each due schedule occurrence                                      |
| Retention sweep                | `JobSettings:RetentionEnabled`   | Deletes finished jobs older than `FinishedJobRetention`                                |
| `GET /v3/jobs/{jobId}`         | Always                           | Returns the status of one job of the caller's tenant                                   |

Each switch is independent and is read at startup. Every CMS instance may run all three; the
database coordinates them, so running several instances is safe and adds capacity.

## Configuration

All settings are in the `JobSettings` section and are documented, with defaults and accepted
ranges, in [Configuration: JobSettings](./CONFIGURATION.md#jobsettings). The service validates
them at startup and refuses to start when one is out of range or when the lease leaves no room
for a late renewal.

Fixed limits that are not configurable: a payload of at most 4000 characters; a public error
message of at most 1000 characters; a 5 s wait for the row lock of an ownership write and of a
fence; a fence needs at least 2 s of lease left once its lock is held; exhaustion changes at most
1000 rows per transaction; a schedule materialization must finish within 10 s.

## Job lifecycle

```text
enqueue ──────────────────────────► Pending
Pending ─claim────────────────────► InProgress   (attempt + 1, new lease)
InProgress ─handler succeeds──────► Completed
InProgress ─permanent failure─────► Error        (registered message)
InProgress ─transient failure─────► Pending      (retry after backoff), or Error when out of attempts
InProgress ─shutdown──────────────► Pending      (released; the attempt still counts)
InProgress, lease expired ─claim──► InProgress   (reclaimed by any instance, attempt + 1)
out of attempts ─worker sweep─────► Error        ("The job exceeded the maximum number of attempts.")
Completed / Error ─retention──────► deleted
```

### Attempt semantics

- `AttemptCount` counts executions started. The claim increments it before any handler code runs.
- A transient failure retries after `RetryBackoffBase × 2^(attempt − 1)`, capped at
  `RetryBackoffMaximum`, while attempts remain; otherwise the job fails with `AttemptsExhausted`.
- A job released during shutdown keeps the attempt it used. A job released on its final attempt
  fails with `AttemptsExhausted` on the next worker sweep, without running again. A handler that
  returns normally on its final attempt is not released; its job completes (see the handler
  contract).
- Lowering `MaxAttempts` fails every waiting job already at or over the new limit on the next sweep.
- A job can therefore end in `Error` with `AttemptsExhausted` without a final handler execution:
  after a crash, lost ownership certainty, or a shutdown release during its final attempt, or after
  `MaxAttempts` is lowered, the worker sweep writes that outcome and no handler code runs.

### What consumers must provide

The infrastructure leaves two obligations to the features that register job types. The managed
data-store lifecycle (DMS-1439) must meet both:

- **Converge on exhaustion.** A consumer whose own records must reflect a failed job (for example
  `CreateError` or `DeleteError`) cannot rely on its handler seeing the final failure, because of
  the exhaustion paths above. It must reconcile from the job's persisted terminal state, and it
  must allow for `FinishedJobRetention`. That setting is configurable, so a finished job may be
  deleted sooner than the default seven days.
- **Suppress its own job types when its feature is off.** Claims take any eligible job, whatever
  its type. `WorkerEnabled=false` stops every job type, and a job type with no registered handler
  is still claimed and fails with `UnsupportedJobType`. Neither of these leaves managed jobs
  unclaimed and unscheduled while refresh jobs and polling keep running. The lifecycle feature
  must design that suppression itself.

### Failure classification and public error text

A handler throws `JobPermanentException(JobErrorCode)` for a failure that retrying cannot fix; the
job fails with that code's registered message. Any other exception is transient. The public
`errorMessage` is always a registered, fixed string, never exception text. Built-in codes:

| Code                        | Message                                                      |
| --------------------------- | ------------------------------------------------------------ |
| `UnsupportedJobType`        | The job type is not supported by this service.               |
| `UnsupportedPayloadVersion` | The job payload version is not supported by this service.    |
| `InvalidPayload`            | The job payload is not valid for its job type.               |
| `TenantUnavailable`         | The tenant the job belongs to is not available.              |
| `AttemptsExhausted`         | The job exceeded the maximum number of attempts.             |
| `HandlerFailed`             | The job handler failed.                                      |

Consumers register their own codes with `AddJobErrorCode(code, message)`. A permanent failure
whose code is not registered is recorded as `HandlerFailed`.

## Polling endpoint

`GET /v3/jobs/{jobId}` follows the Admin API v3 contract.

- Authorization: the `edfi_admin_api/full_access` and `edfi_admin_api/readonly_access` scopes may
  read; `edfi_admin_api/authMetadata_readonly_access` alone receives 403, and an anonymous request
  401.
- `jobId` is an opaque string matched exactly (case, length, and trailing spaces included) within
  the caller's tenant. An unknown job and another tenant's job both return the same 404 problem
  details (`urn:ed-fi:api:not-found`, detail `Job not found.`).
- With multi-tenancy enabled, the `Tenant` header is required as for every tenant-scoped route.
- A committed enqueue is visible to the very next request.

The 200 body has exactly five properties; times are UTC with a `Z` suffix, and null values are
present:

```json
{
  "jobId": "0f8a3c5e2b7d4e19a6c1f0d2e3b4a5c6",
  "status": "Error",
  "createdAt": "2026-09-24T12:30:45Z",
  "finishedAt": "2026-09-24T12:31:00.25Z",
  "errorMessage": "The job exceeded the maximum number of attempts."
}
```

`status` is one of `Pending`, `InProgress`, `Completed`, or `Error`. `finishedAt` is set only for
`Completed` and `Error`, and `errorMessage` only for `Error`.

### Differences from the upstream OpenAPI document

The contract was compared with the Admin API v3 document generated from ODS-Admin-API commit
`ed115fd8` (`reference/design/jobs-DMS-1437/admin-api-v3-ed115fd8.yaml`). The CMS document matches
it on the path parameter, the declared responses (200, 401, 403, 404, and 500, each error as
`application/problem+json`), the property names, types, and formats, and the nullability of
`createdAt`, `finishedAt`, and `errorMessage`. It differs as follows:

- `jobId` and `status` are non-nullable. Upstream marks them nullable only as a generator artifact;
  the upstream code never returns null for them.
- The schema is named `JobStatusResponse`, not `jobStatusResult`, and lists `jobId` and `status` as
  `required`.
- The schema does not declare `additionalProperties: false`.
- Problem responses reference the framework `ProblemDetails` schema, not `problemDetails`.
- The operation's tag is `JobModule`, as for every CMS module, not `Jobs`.

None of these changes a 200 response body. One runtime difference is deliberate: the not-found
problem type is the CMS `urn:ed-fi:api:not-found`, not upstream's
`urn:ed-fi:management-api:not-found`, consistent with every other CMS route.

## Writing a job handler

A consumer registers a handler, a payload type, and a validator for a job type and the payload
versions it accepts:

```csharp
services.AddJobHandler<RefreshHandler, RefreshPayload, RefreshPayloadValidator>(
    "DataStore.RefreshEducationOrganizations", 1);
services.AddJobErrorCode("DataStoreNotFound", "The data store no longer exists.");
```

Registration fails at startup for an invalid job type, a bad version list, a payload type that
breaks the payload contract, or a type registered twice.

### Payload contract

Payloads carry CMS identifiers only. A payload type is a sealed record or class whose public
properties are `int`, `long`, `short`, `Guid`, `bool`, enums, strings marked
`[JobIdentifier(MaxLength)]` (at most 256), read-only lists of those, or nested types following the
same rules. Dates, floating point, dictionaries, `object`, and JSON nodes are rejected, as are
System.Text.Json attributes. Payloads are read strictly: camelCase names matched case-sensitively,
no unknown or duplicate properties, no nulls, and a maximum depth of 8. The enqueuer validates
the payload with the type's validator and stores its own re-serialization, never the caller's text.

### The handler contract

`IJobHandler<TPayload>.ExecuteAsync(JobExecutionContext context, TPayload payload, CancellationToken ct)`
receives the job identifier, the job's tenant (already installed in the scope the handler is
resolved from), the attempt number and maximum, and a fence.

- Honor the cancellation token. It is cancelled when the instance shuts down and when the execution
  loses certainty that it still owns the job.
- A normal return means the work is complete. A handler that stops before its work is done must
  let the `OperationCanceledException` propagate, or call `ThrowIfCancellationRequested`, rather
  than return. The executor cannot tell whether a normal return concealed unfinished work, so
  while attempts remain it does not count a return after the instance's stop signal as success:
  it releases the job to `Pending` while it still certainly owns the job, and writes nothing
  otherwise. A handler that finished just as shutdown began may therefore run again. On the final
  attempt a release would end in `AttemptsExhausted`, so there a normal return completes the job,
  and a handler that returns without finishing its work would be recorded as `Completed`.
- Assume at-least-once execution. A job may run again after a crash, a lost lease, a shutdown, or
  an ambiguous database result, so every handler must be idempotent: reconcile what an earlier
  attempt already did before acting.
- Keep calls to external systems outside the fence, and make them idempotent too.

### Fence rules

`context.Fence.ExecuteAsync(work, ct)` runs `work(transaction, ct)` in a CMS database transaction
that holds the job's row lock, and commits only if the execution still owns the job:

- A fence waits for a renewal in progress, and a renewal never runs during a fence.
- The fence is refused (`JobLeaseLostException`) without touching the database when the
  execution's ownership is already uncertain. Otherwise it first acquires the job's row lock, and
  is then refused with the same exception when the lease no longer matches or when less than 2 s of
  lease remains, both judged by database time after the lock is held.
- The work, the final ownership check, and the commit share one deadline: the smaller of
  `FenceTimeout` and the remaining lease less 1 s.
- `JobFenceUnavailableException` means the row lock was not acquired within 5 s; it is transient,
  and the execution still owns the job.
- A `JobLeaseLostException` ends the execution's certainty before it reaches the handler, even if
  the handler catches it. No outcome is written afterwards, and every later fence of the execution
  is refused. The handler must stop. Recovery follows the persisted state: the job stays
  `InProgress` and is reclaimed after its lease expires only if attempts remain; otherwise the
  worker sweep fails it with `AttemptsExhausted`.
- The uncertainty reason (in `OwnershipUncertainExit` and the `ownership_uncertain` metric) says
  why. `FenceTimeout`: the shared deadline ended the work, the post-work check, or the final
  ownership check, and the configured `FenceTimeout` set that deadline, so lease time may remain.
  `FenceLeaseLost`: the lease was missing, expired, mismatched, or too short, or the deadline set
  by the remaining lease ended the work or the check. The first reason an execution records is
  kept.
- A fence commit whose outcome is unknown, including one the deadline ended, ends the execution's
  certainty the same way, with reason `FenceCommitUnknown`. The next attempt, if one remains, must
  reconcile the fenced changes that may already be committed.
- The fence's 5 s lock timeout also applies to the work's own statements: PostgreSQL
  `lock_timeout` is set for the fence transaction, and SQL Server `LOCK_TIMEOUT` for its session.
  A statement in the work that waits longer for a lock fails with the provider's own error
  (PostgreSQL `55P03`, SQL Server `1222`), not `JobFenceUnavailableException`, which describes
  only the fence's own row lock. Like any other exception from the work, it rolls the fenced
  changes back and propagates to the handler.

Write the database changes that must happen only while the job is owned through the fence, using
the supplied transaction.

## Enqueueing

`IJobEnqueuer.EnqueueAsync(command, transaction, ct)` validates the job type, payload version, and
payload, then inserts the job. Pass an `ICmsTransaction.Transaction` from `ICmsTransactionFactory`
to make the job commit or roll back with the consumer's own writes; a transaction that has already
completed is rejected rather than written outside it. The new job's identifier is a GUID in `N`
format.

## Schedules

A schedule is identified by its tenant and schedule type, and keeps its identifier across disable
and re-enable. `IJobScheduleService.UpsertAsync` validates the schedule's job exactly as the
enqueuer does before storing it. On each poll, the dispatcher enqueues one job per due occurrence
in a single transaction that also advances the schedule:

- After downtime spanning several intervals, one job is enqueued and the next run moves to the
  first interval boundary after the current database time (coalescing); missed occurrences are not
  replayed.
- An occurrence is enqueued at most once, including when two instances dispatch at the same time.
- While the schedule's previous job is still `Pending` (including a retry waiting for its next
  attempt) or `InProgress` (including one whose lease expired and awaits recovery), a due
  occurrence is skipped: no job is enqueued, the schedule still advances to the first future
  interval, and `ScheduleOccurrenceSkippedActiveJob` is logged. Skipped intervals are discarded,
  not queued. The next occurrence that falls due after the previous job ends in `Completed` or
  `Error` is enqueued normally.
- A disabled schedule enqueues nothing more; its past jobs are kept.

Scheduled jobs run through the same worker and the same lifecycle as any other job. The skip rule
only keeps one schedule's own jobs from overlapping. It does not coordinate a scheduled job with
manually enqueued jobs or with jobs from other schedules that touch the same tenant or target. A
consumer that needs that, such as the education-organization refresh (DMS-1441), must deduplicate
or serialize that work itself.

## Recovery

- **Instance crash:** the job's lease expires, and any instance reclaims it (attempt + 1) or, when
  out of attempts, fails it on the next sweep. A crash during schedule dispatch rolls the whole
  occurrence back, and a later poll dispatches it again.
- **Shutdown:** the worker stops claiming, cancels running handlers, and releases each job it still
  owns back to `Pending`. A job whose release fails is left for lease expiry. The host's shutdown
  timeout bounds the wait for a handler that ignores cancellation.
- **Ambiguous database results:** when a renewal, a fence commit, or an outcome write fails in a way
  that leaves its result unknown, the execution stops, nothing is retried, and the job status is not
  re-read. What happens next depends only on what the database persisted:

  | Actual database outcome       | What follows                                                               |
  | ----------------------------- | -------------------------------------------------------------------------- |
  | Completion or failure committed | The job is finished; retention removes it later                          |
  | Retry or release committed    | The job is `Pending` and runs again when eligible                          |
  | Outcome rolled back           | The job stays `InProgress` until its lease expires, then is reclaimed if attempts remain, or fails with `AttemptsExhausted` |
  | Fence committed, no completion | Fenced changes persist; the next attempt must reconcile them               |

- **Database unavailable:** each hosted service logs the failure and tries again on its next
  interval; none of them stops the host.
- **All times are database UTC.** Leases, retries, schedules, and retention use the database clock,
  so clock differences between instances do not matter.

## Retention

The retention sweep runs when the service starts and then every `RetentionInterval`. It deletes
`Completed` and `Error` jobs whose `finishedAt` is older than `FinishedJobRetention`, at most
`RetentionBatchSize` per batch, skipping rows other transactions hold. `Pending` and `InProgress`
jobs are never deleted. A fractional retention is rounded up to whole seconds.

## Observability

Logs are structured, carry the event name as their `EventId` name, and never include payloads,
exception messages, or stack traces. Failures report the exception type chain, a provider error
code when there is one (PostgreSQL `SqlState`, SQL Server error number), and the operation.
Identifiers are sanitized before logging.

| Event                               | Level    | Meaning                                                   |
| ----------------------------------- | -------- | --------------------------------------------------------- |
| `JobClaimed` / `JobReclaimed`       | Info     | A job started; reclaimed means an expired lease was taken |
| `JobCompleted`                      | Info     | The handler succeeded                                     |
| `JobRetryScheduled`                 | Warning  | A transient failure; the job will run again               |
| `JobFailed`                         | Warning  | The job failed for good, with its `JobErrorCode`          |
| `JobReleasedOnShutdown`             | Info     | Released back to `Pending` during shutdown                |
| `OwnershipUncertainExit`            | Warning  | The execution stopped without writing an outcome          |
| `LateWriteRejected`                 | Warning  | Another instance owned the job by the time of the write   |
| `WriteOutcomeUnknown`               | Warning  | An outcome write's result is unknown                      |
| `JobsExhausted`                     | Warning  | The sweep failed jobs that were out of attempts           |
| `WorkerPollFailed`                  | Warning / Error | A worker poll failed (Warning for a database failure result, Error for an exception); retried next poll |
| `ScheduleOccurrenceEnqueued`        | Info     | A schedule occurrence became a job                        |
| `ScheduleOccurrenceAlreadyEnqueued` | Warning  | The occurrence already existed (defensive recovery)       |
| `ScheduleOccurrenceSkippedActiveJob` | Info    | Skipped because the schedule's previous job is active     |
| `ScheduleMaterializationFailed`     | Warning  | A dispatch rolled back; retried next poll                 |
| `RetentionDeleted`                  | Info     | Finished jobs deleted by one sweep                        |
| `RetentionFailed`                   | Warning  | A retention sweep stopped early; retried next interval    |
| `JobStatusReadFailed`               | Error    | `GET /v3/jobs/{jobId}` could not read the job (500)       |

Metrics come from the `EdFi.DmsConfigurationService.Jobs` meter (no exporter is configured by
the service):

| Instrument                              | Type                | Tags                     |
| --------------------------------------- | ------------------- | ------------------------ |
| `dmscs.jobs.claimed`                    | Counter             | `job_type`, `reclaimed`  |
| `dmscs.jobs.finished`                   | Counter             | `job_type`, `outcome`    |
| `dmscs.jobs.ownership_uncertain`        | Counter             | `job_type`, `reason`     |
| `dmscs.jobs.queue_delay`                | Histogram (ms)      | `job_type`, `reclaimed`  |
| `dmscs.jobs.duration`                   | Histogram (ms)      | `job_type`               |
| `dmscs.schedules.occurrences_enqueued`  | Counter             | `schedule_type`          |
| `dmscs.jobs.retention_deleted`          | Counter             | —                        |

`dmscs.jobs.queue_delay` measures database time from a job's `NextAttemptAt` to its claim. A
reclaim keeps the earlier `NextAttemptAt`, so `reclaimed=true` samples also include the abandoned
attempt and its lease, which inflates apparent queue latency. Filter on `reclaimed=false` to
measure normal queue delay.

## Deployment

- **Database:** the `dmscs.JobSchedule` and `dmscs.Job` tables are added by the CMS deploy scripts
  (`0033`, `0034`) on PostgreSQL and SQL Server. The change is additive; a downgrade leaves the
  tables in place.
- **Job type rollout:** every instance with the worker enabled must register every job type and
  payload version that may be enqueued, because a claimed job of an unknown type or version fails
  permanently. When a release introduces a new job type, finish rolling it out before enabling the
  feature that enqueues it, or run instances of the previous release with
  `JobSettings:WorkerEnabled=false` until they are replaced.
- **Capacity:** with an idle queue and a healthy database, a committed job is typically claimed
  within about one `PollInterval` (about half of one on average). This is an estimate, not an upper
  bound: each poll also runs the exhaustion sweep before it claims, and a slow sweep, claim, or
  failed poll delays the next claim. Under a backlog, throughput is roughly
  `instances × MaxConcurrentJobs ÷ mean job duration`. Both are estimates, validated by the
  operational assessment in `reference/design/jobs-DMS-1437/operational-assessment.md`.
- **Docker Compose:** the provided compose files map `DMS_CONFIG_JOBS_*` variables onto
  `JobSettings__*`. Test hosts set all three switches to `false`.
