# DMS-1437: Add durable CMS background jobs, schedules, and Management API v3 job polling

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Build the shared CMS durable job and schedule infrastructure required by managed data-store lifecycle and education-organization refresh.

- Endpoint: `GET /v3/jobs/{jobId}` for Management API v3 job polling.
- Persistence: provider-equivalent job, attempt, lease, retry, schedule, and occurrence state for PostgreSQL and SQL Server.
- Execution: in-process hosted workers with atomic claim/reclaim, fencing, retry/backoff, shutdown cancellation, schedule dispatch, and tenant context propagation.
- Boundaries: this ticket does not implement managed lifecycle create/delete handlers, education-organization refresh handlers, public schedule management, or arbitrary user-authored jobs.

### Description

CMS has an in-process hosted-service precedent but no durable job resource, recurring schedule resource, or recoverable asynchronous execution. Managed lifecycle and education-organization refresh both need work to survive process restarts, avoid concurrent duplicate execution, retry safely, and expose the v3 job contract.

This story delivers tenant-scoped CMS job and schedule persistence, a recoverable dispatcher, and `GET /v3/jobs/{jobId}` without adding Quartz, a message broker, or a new service.

**Scope**

- Add provider-equivalent CMS persistence for jobs, attempts, timestamps, sanitized errors, retry timing, and lease ownership/expiry.
- Add provider-equivalent CMS persistence for recurring schedules: stable schedule ID, tenant, job type, non-secret payload/target identifiers, interval, enabled state, next-run time, and claim/lease metadata.
- Add provider-specific atomic claim/lease operations that allow multiple CMS replicas while permitting expired work to be reclaimed.
- Add a schedule dispatcher that atomically claims due schedules, enqueues ordinary `Pending` jobs, advances the next-run time, and reclaims expired schedule leases.
- Add an extensible in-process job-handler dispatch contract and a hosted worker using the existing CMS `BackgroundService` pattern.
- Add bounded retry/backoff, cancellation on shutdown, configurable retention/cleanup, structured logs, and operational metrics.
- Add the v3 job-status read endpoint.
- Establish explicit tenant context inside every background execution scope.

**API surface**

- `GET /v3/jobs/{jobId}`
- Response schema: `jobStatusResult`
- Response fields: `jobId`, `status`, `createdAt`, nullable `finishedAt`, nullable `errorMessage`
- Statuses: `Pending`, `InProgress`, `Completed`, `Error`
- Errors: `400` for missing/invalid tenant context when multi-tenancy is enabled, plus `401`, `403`, `404`, and existing CMS problem-details behavior

Example `200 OK` response:

```json
{
  "jobId": "RefreshEducationOrganizationsJob-tenant-a-123_65b37e7cba4448f58d4bcbf28f3b04a1",
  "status": "InProgress",
  "createdAt": "2026-09-10T16:30:00Z",
  "finishedAt": null,
  "errorMessage": null
}
```

**Architecture and boundaries**

CMS owns job and schedule persistence because they are Management API control-plane resources. The `Jobs` model preserves Admin API's externally visible `JobStatuses` semantics while adding the retry/lease state required for recovery. The separate `Schedules` model preserves Admin API's distinction between a recurring trigger and each resulting job execution, but makes the trigger durable instead of using Admin API's in-memory Quartz schedule. Hosted workers poll/claim both due schedules and due jobs from the CMS database. Work is at-least-once, so handlers must be idempotent. Persisted payloads contain a type and tenant-scoped entity identifiers, never a connection string, token, package credential, or other secret.

The enqueue API must allow a caller to create its control-plane record and corresponding job atomically in one CMS transaction. A refresh caller that needs only a job may insert the job directly in that transaction. A due schedule creates the same ordinary job record used by a manual request; scheduled executions do not use a separate handler or status model.

**Dependencies**

- Existing CMS PostgreSQL and SQL Server repository/migration conventions.
- Existing [`TenantContext`](../../../src/config/backend/EdFi.DmsConfigurationService.Backend/Services/TenantContext.cs) and scoped provider.
- Existing [`TokenCleanupService`](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/TokenCleanupService.cs) as a hosted-service pattern, not as job infrastructure.

**Blockers**

None. DMS-1437 can start independently.

**Out of scope**

- Managed data-store lifecycle handlers.
- Education-organization refresh handlers.
- A generic user-authored job API or arbitrary payload execution.
- A public schedule-management API or arbitrary user-defined schedules.
- Job cancellation, prioritization, or progress percentages not present in v3.

**Risks and implementation considerations**

- Exact operational defaults and payload/error bounds require refinement and load validation before coding. They may be configurable, but must preserve the lease-renewal invariant and fixed schedule misfire policy in AC 20.
- Provider-specific claim and reclaim statements must compare lease expiry using database UTC time rather than worker-process clocks (for example, PostgreSQL `now()` and SQL Server `SYSUTCDATETIME()`). Cross-provider integration tests must demonstrate equivalent observable claim, expiry, and reclaim behavior.

**Non-binding implementation guidance**

- Follow repository JSON conventions (`System.Text.Json`) and prefer a small explicit envelope/handler registry over serialized .NET type names.
- A GUID is a reasonable opaque job-ID implementation if it matches the pinned API representation; the story does not require a specific GUID version or text formatting beyond that contract.
- Choose polling, lease, retry, payload/error bounds, and retention defaults from provider-backed load/operational review rather than treating spike examples as product constants.

**Minimum persistence contract**

Provider migrations must use equivalent PostgreSQL and SQL Server types. Exact table and column names should follow CMS conventions, but the durable job table needs at least:

| Logical column | Portable type intent | Notes |
| --- | --- | --- |
| `jobId` | bounded text/string, unique | Opaque API identifier returned as `jobStatusResult.jobId`. |
| `tenant` | nullable bounded text/string | Null/internal value only for single-tenant if that matches existing CMS tenancy conventions. |
| `jobType` | bounded text/string | Allowlisted handler key. |
| `payloadVersion` | small integer | Reject unknown versions before handler execution. |
| `payloadJson` | bounded JSON/text | Non-secret target CMS IDs only. |
| `sourceScheduleId` | nullable bounded text/string or GUID | Links scheduled occurrences to the durable schedule. |
| `scheduledOccurrence` | nullable UTC timestamp or occurrence key | Participates in duplicate prevention. |
| `status` | bounded text/string | API values remain `Pending`, `InProgress`, `Completed`, `Error`. |
| `createdAt`, `finishedAt`, `nextAttemptAt` | UTC timestamp/date-time | `finishedAt` and `nextAttemptAt` are nullable. |
| `errorMessage` | nullable bounded text/string | Sanitized client-facing text only. |
| `attemptCount` | integer | Incremented persistently. |
| `leaseOwner`, `leaseExpiresAt`, `fencingToken` | nullable bounded text/string, nullable UTC timestamp, integer/bigint | All state/result writes match the current lease owner and token. |

The durable schedule table needs at least stable schedule ID/key, tenant, schedule type, job type, payload target identifiers, interval minutes, enabled flag, next-run UTC timestamp, lease owner, lease expiry, and fencing/version. A unique active schedule constraint per tenant/schedule type and a unique schedule-occurrence constraint are required.

### Acceptance Criteria

1. PostgreSQL and SQL Server CMS schemas persist an opaque unique job ID compatible with the pinned OpenAPI representation, tenant, supported job type, versioned target payload, optional source-schedule/occurrence identity, status, created/finished timestamps, bounded sanitized error, attempt count, next-attempt time, lease owner/expiry, and a monotonically increasing fencing token.
2. Enqueue persists `Pending` before the originating API response is returned; an immediate authorized GET never races to `404` for an accepted job.
3. `GET /v3/jobs/{jobId}` returns the OpenAPI response shape and `404` for an absent job or a job belonging to another tenant.
4. Job GET uses the existing read-only-or-admin policy. It never exposes payload secrets, stack traces, administrative connection details, or another tenant's data.
5. A provider-specific atomic claim/reclaim uses database UTC, permits at most one unexpired lease, and increments the fencing token; renewal and every retry/terminal write match job ID, owner, and fencing token. A stale worker cannot mutate job or consumer state after losing its lease.
6. The worker creates a dependency-injection scope, establishes the job's persisted tenant context, invokes the registered handler, and clears/disposes the scope after completion.
7. A completed handler sets `Completed` and `finishedAt`; a terminal failure sets `Error`, `finishedAt`, and a sanitized client-facing error only while the handler still owns the matching lease version.
8. A transient handler failure increments persisted attempts, returns the job to `Pending`, records `nextAttemptAt`, clears lease fields, and leaves `finishedAt` null. Exhausting the configured attempt limit produces terminal `Error`.
9. Work left `InProgress` by process termination is reclaimable after lease expiry. The reclaim increments the fence, and an earlier worker's late commit is rejected.
10. The worker renews each active lease before expiry using database UTC. Renewal failure cancels local execution; every handler, including external process/database operations, observes cancellation and remains idempotent across retry.
11. Process shutdown stops new claims, cancels active handlers, and either safely returns a still-owned job to `Pending` or leaves it for lease expiry. A late completion is fenced.
12. Jobs use a versioned, size-bounded payload containing target CMS IDs only. Dispatch accepts only explicitly registered job types and supported payload versions; unknown, invalid, or oversized stored payloads fail terminally before handler execution. Payload deserialization cannot activate arbitrary runtime types.
13. Job retention/cleanup is configurable and cannot remove pending, leased, or retryable jobs.
14. Polling, lease, renewal, attempt, retry-backoff, retention, and cleanup settings have documented startup-validated defaults. Renewal occurs comfortably before lease expiry, retry/retention values are bounded, and invalid combinations fail startup with actionable diagnostics. Exact defaults require operational review before the story enters implementation.
15. Structured logs and metrics expose job type, tenant-safe identifiers, queue delay, attempt, duration, lease recovery, and outcome without logging payload secrets. API timestamps use UTC and the pinned OpenAPI date-time representation.
16. No Quartz package, external broker, or new deployable service is introduced.
17. PostgreSQL and SQL Server CMS schemas persist recurring schedules with a stable unique ID/key, tenant, job type, non-secret target/payload, interval, enabled state, next-run time, lease owner/expiry/version. A uniqueness constraint permits only one active schedule for a given tenant and schedule type.
18. Claiming a due schedule, inserting its `Pending` job with source schedule and scheduled-occurrence identity, and advancing the schedule's next-run time are atomic and fenced. A unique schedule-occurrence constraint prevents concurrent replicas or retries from enqueueing the same occurrence twice; expired schedule leases are reclaimable.
19. A scheduled job uses the same handler, retry rules, status transitions, tenant context, polling representation, retention rules, and observability as a manually enqueued job. Disabling a schedule prevents future enqueue without deleting prior job history.
20. Missed intervals are coalesced: after downtime, the dispatcher enqueues at most one immediately due occurrence and advances `nextRunAt` to the first future interval. It does not enqueue one catch-up job per missed interval.

### Tasks

**Implementation**

1. Add equivalent PostgreSQL/SQL Server job and schedule migrations, constraints, configurable bounds, and mapping models.
2. Add provider repository operations for atomic enqueue, claim/reclaim, renewal, fenced transitions, schedule occurrence creation, and cleanup.
3. Add the allowlisted versioned handler registry, worker/scheduler hosted services, cancellation, validated options/defaults, logs, and metrics.
4. Add tenant-safe `GET /v3/jobs/{jobId}` using existing authorization/problem-details conventions.
5. Document job/schedule configuration, recovery behavior, retention, observability, and operational tuning constraints for DMS-1437 consumers.

**Verification**

- Unit tests for job/schedule state transitions, next-run calculation, retry classification, error sanitization, tenant setup, and retention rules.
- PostgreSQL and SQL Server integration tests for atomic manual enqueue, atomic scheduled enqueue/next-run advancement, concurrent claims, renewal, lease expiry/reclaim, attempt persistence, schedule uniqueness, and tenant isolation.
- API-level tests for authorization, immediate polling, all response states, and cross-tenant `404` behavior.
- Restart simulations proving abandoned in-progress jobs and due schedule leases are reclaimed without losing or duplicating an occurrence.
- Two-worker tests where execution outlives the original lease: the current owner renews successfully, or a reclaimed worker completes and the stale worker's late state/result write is rejected.
