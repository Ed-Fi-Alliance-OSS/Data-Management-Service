# Request/Response Capture Design

## Status

Drafted 2026-11-20 as the design output of [DMS-1419](https://edfi.atlassian.net/browse/DMS-1419), the scoping story under epic [DMS-1418](https://edfi.atlassian.net/browse/DMS-1418) Request/Response Logging.
Not yet approved.

The capability being scoped is operator-controlled, per-API-client, time-bounded capture of full API request and response content for troubleshooting.
It gives parity with the ODS/API `RequestResponseContentLog` (ODS-5329, shipped in v7.0) and fixes the two limitations the API work group named: ODS capture is all-or-nothing across clients, and changing it needs a restart.

This is a scoping deliverable.
It records agreed decisions and the estimated stories that implement them, and it is judged on that standard: acceptance-criteria coverage, decision ownership, feasibility, story boundaries, estimates, and contradictions.
Implementation detail belongs in the story acceptance criteria, where it is tested.

- [README.md](./README.md) - manifest, acceptance-criteria coverage, story index, filing
- Jira: [DMS-1419](https://edfi.atlassian.net/browse/DMS-1419), epic [DMS-1418](https://edfi.atlassian.net/browse/DMS-1418)

**Citation convention.**
Unprefixed paths are relative to this repository's root, at `main` commit `bef28de1b`.
`Ed-Fi-ODS:` names [Ed-Fi-ODS](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/v7.3.2) at tag `v7.3.2`, the application code.
`Impl:` names [Ed-Fi-ODS-Implementation](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/tree/v7.3.2) at tag `v7.3.2`, the shipped configuration and scripts.
`ODS docs:` names the ODS/API v7.3 how-to [Enable logging API request and response content](https://docs.ed-fi.org/reference/ods-api/how-to-guides/how-to-enable-logging-api-request-and-response-content/), cited by line from its source, `odsApi_versioned_docs/version-7.3/how-to-guides/how-to-enable-logging-api-request-and-response-content.mdx` in [ed-fi-alliance-oss.github.io](https://github.com/Ed-Fi-Alliance-OSS/ed-fi-alliance-oss.github.io) at commit `5a93ad08d2`.

## Requirements

The ticket's own acceptance criteria are scoping outcomes.

| AC | Criterion | Answered in |
| --- | --- | --- |
| AC1 | Relationship to DMS-62 decided: merge, supersede, or keep as separate scope | [D1](#d1-relationship-to-dms-62) |
| AC2 | Redaction requirements agreed for credentials and sensitive payload fields | [D4](#d4-credential-exclusion), [D5](#d5-payload-policy) |
| AC3 | Configuration surface and log sink chosen | [D2](#d2-control-surface), [D6](#d6-capture-store-and-retention) |
| AC4 | Capture, configuration, redaction and documentation split into estimated stories | [Stories](#stories) |
| AC5 | This placeholder is closed once those stories exist | [Disposition](#disposition) |

The decisions must satisfy the capability requirements the ticket describes.
Each is quoted or condensed from the DMS-1419 description, under the heading named in the Source column.

| R | Requirement | Source |
| --- | --- | --- |
| R1 | Only the named client's traffic is captured | Acceptance criteria |
| R2 | Enable and disable take effect without a restart, across all running instances | Acceptance criteria; Runtime control |
| R3 | Capture expires automatically after a configured duration, so it cannot be left on by accident | Acceptance criteria; Runtime control |
| R4 | Credentials and tokens never appear in captured content (scope: [D4](#d4-credential-exclusion)) | Acceptance criteria; Security and privacy |
| R5 | Off by default, with no measurable overhead when off | Acceptance criteria |
| R6 | Captured content is retrievable in a form workflow or BI tooling can consume | Acceptance criteria; Storage and retention |
| R7 | Documentation states the PII exposure and retention obligations plainly | Acceptance criteria; Documentation |
| R8 | The control is operator-accessible and auditable: who enabled capture, for which client, when | Runtime control |
| R9 | Default to failures only, with an option to capture all requests for the targeted client | Capture |
| R10 | Full-payload capture is not routable to logging sinks or plugins by default | Security and privacy |
| R11 | Retention and purge behavior, given the volume capture can generate | Storage and retention |
| R12 | Enough context to be useful: correlation or trace ID, API client identity, URL and query string, HTTP method, status, and exception detail | Capture |

**Out of scope**, per the ticket: long-term audit logging or a compliance retention story, and any UI for browsing captured content.

## Current State

### DMS Logging and the Request Pipeline

**DMS captures request bodies only, at `Debug`, globally, and masked by default.**
`RequestInfoBodyLoggingMiddleware` runs with `AppSettings:MaskRequestBodyInLogs` (`ApiService.cs:329`, `:502`), which ships `true` (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json:20`).
DMS never captures a response body.
`docs/LOGGING.md:204-209` keeps bodies, tokens, and raw query strings out of Information-level logs.
[DMS-1550](https://edfi.atlassian.net/browse/DMS-1550), open, asks whether that rule depends on the log level.

**Logs leave the process.**
OTLP export ([DMS-1335](https://edfi.atlassian.net/browse/DMS-1335)) ships every Serilog event.
A [DMS-1462](https://edfi.atlassian.net/browse/DMS-1462) plugin may add an `ILoggerProvider`, which composes with the host's (`reference/design/plugins-DMS-1462/design.md:1068`).
[DMS-459](https://edfi.atlassian.net/browse/DMS-459), runtime sink plugins, was closed as Won't Do, but the provider rule above stands.
Anything sent through `ILogger` is therefore routable, so R10 rules out the logging pipeline as the capture sink.

**The pipeline.**
`ApiService.GetCommonInitialSteps` (`ApiService.cs:234-246`) runs `RequestResponseLogging`, `CoreExceptionLogging`, `TenantValidation`, `JwtAuthentication`, then `ResolveDataStore`.
`JwtAuthentication` sets the validated `ClientId`, which is the CMS `ApiClient.ClientId`.
`CoreExceptionLogging` builds the 403, 500 and 503 responses after the inner steps unwind, so the final response is visible only outside it (`Middleware/CoreExceptionLoggingMiddleware.cs:107-150`).
Every core response is a `JsonNode`, serialized once in `AspNetCoreFrontend.ToResult`.

**Request bodies are gone by the time core runs.**
The frontend clears its rented byte buffer after parsing (`AspNetCoreFrontend.cs:44-100`).
Malformed JSON reaches core with no raw body and no parsed body, only a parse error (`AspNetCoreFrontendRequestBodyTests.cs:251`), and type coercion later mutates `ParsedBody`.
GET, DELETE, and change-query requests never read a body (`includeBody: false` at `AspNetCoreFrontend.cs:903`, `:955`, `:1013`, `:1039`).
`FromRequest` keeps only the last value of a repeated query parameter (`AspNetCoreFrontend.cs:704`).

**Exception text can carry credentials.**
The generic arm of `CoreExceptionLogging` records the exception, whose full message chain `RequestResponseLogging` logs.
The `DatabaseConnectionUnavailableException` arm records nothing, because the provider message quotes a connection string (`CoreExceptionLoggingMiddleware.cs:107-145`).

**Client text reaches core in more places than the body.**
The token endpoint never enters the core resource pipelines, and the bearer token reaches core in the `Authorization` header.
The correlation ID comes from a client header when `AppSettings:CorrelationIdHeader` is set (`docs/CONFIGURATION.md:199`), and it is echoed in error bodies (`Response/FailureResponse.cs:88`).
Any verb on a data route reaches core's 405 pipeline, which runs `JwtAuthentication` (`Modules/CoreEndpointModule.cs:45`, `AspNetCoreFrontend.cs:1070`).
The raw verb text is kept in `RequestInfo.UnsupportedMethodName`, surfaced through `MethodName` (`Pipeline/RequestInfo.cs:54-63`), and echoed in the 405 body.

**The PRD.**
`docs/PRD-Ed-Fi-API-v8.0.md:558-571`: NFR-OBS-2 asks for temporary detailed request/response logging, and NFR-OBS-3 requires masked dictionary key values when it is enabled.
Its warning says no mechanism reverts elevated verbosity automatically.

### Control Plane

**DMS reads one CMS API client per request.**
The call is `GET v3/apiClients/{clientId}` (`Configuration/ConfigurationServiceApplicationProvider.cs:69`), cached per replica by `CachedApplicationContextProvider` for `ApplicationContextCacheExpirationSeconds`, default 600 (`Configuration/CachedApplicationContextProvider.cs:28-29`, `Configuration/CacheSettings.cs:17`).
It is fetched only for POST, ownership-based authorization, and profile resolution.

**Nothing pushes a CMS change to every replica.**
Caches are in-memory and local to each instance (`docs/CACHING-STRATEGY.md:7-20`).
DMS disables configuration reload (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs:26-29`).

**CMS audit is last-writer only.**
Rows carry `CreatedBy` and `ModifiedBy` through `IAuditContext` (`src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Infrastructure/IAuditContext.cs:11`), and there is no history table.
R8 therefore needs grant rows that are never overwritten.

**CMS authorization and precedents.**
Writes use `MapSecured*`, which requires `cms-client` plus `edfi_admin_api/full_access`.
DMS-facing reads use `MapLimitedAccess` (`Modules/ApiClientModule.cs:187`, `:214` under `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`).
`/v3/apiClients/{id}/reset-credential` (`ApiClientModule.cs:159`) is the precedent for an API-client sub-resource.
DbUp scripts exist per engine, under `src/config/backend/EdFi.DmsConfigurationService.Backend.{Postgresql,Mssql}/Deploy/Scripts/`.

**DMS has no database of its own.**
There is one `dms` schema per data-store database, provisioning is create-only with no migrations, and GETs may route to read replicas.

**Secret references do not reach DMS settings.**
[DMS-1554](https://edfi.atlassian.net/browse/DMS-1554) resolves only CMS-stored strings (`reference/design/secrets-DMS-1503/04-resolve-secret-references-in-connection-strings.md:18`).
A DMS setting is secret-sourced by environment injection or by a Phase A plugin ([DMS-1551](https://edfi.atlassian.net/browse/DMS-1551)).

### Related Jira

| Ticket | Status | Relevance |
| --- | --- | --- |
| [DMS-62](https://edfi.atlassian.net/browse/DMS-62) Request Logging and Tracing Improvements | Open | AC1, decided in [D1](#d1-relationship-to-dms-62) |
| [DMS-78](https://edfi.atlassian.net/browse/DMS-78) Audit Trail / Log Design | Open | Audit is out of scope here |
| DMS-1335 / DMS-459 / DMS-1462 | Done / Won't Do / Done | Why the logging pipeline is not the sink |
| [DMS-1550](https://edfi.atlassian.net/browse/DMS-1550) | Open | The documentation story must not contradict its outcome |
| [DMS-1477](https://edfi.atlassian.net/browse/DMS-1477) | In Progress | Tenancy is routing, not a security boundary: one store can span tenants |

## ODS Precedent

**The mechanism.**
`RequestResponseDetailsLoggerMiddleware` (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Middleware/RequestResponseDetailsLoggerMiddleware.cs`, cited below as `mw.cs`) writes through the single `AdoNetAppender` of its `RequestResponseContentLogger` logger (`mw.cs:240`). The code names no table: the appender's `commandText`, which the operator supplies, does, and the docs set it to `RequestResponseContentLog` (`ODS docs:96`, `:205`).
It is registered as a singleton (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Container/Modules/ApplicationModule.cs:505-516`).
It runs only for paths that start with `/data` or `/composites` (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Startup/OdsStartupBase.cs:451-454`), after correlation, tenant identification, authentication, and ODS instance identification (`OdsStartupBase.cs:400-440`).

**Nothing ships it.**
None of the ten log4net configurations in `Impl:` at `v7.3.2` defines the `RequestResponseContentLogger` logger or an `AdoNetAppender`.
The web API's own `Impl:Application/EdFi.Ods.WebApi/log4net.config` defines only `NHibernate` and the separate file-based `RequestResponseDetailsLogger`.
Neither repository creates the table: operators run the DDL from the docs by hand (`ODS docs:14-62`).
This rests on GitHub code search for `RequestResponseContentLog`, which indexes only each repository's default branch and finds it only in `mw.cs`, as part of the logger name, and in the docs, plus no file in either `v7.3.2` tree named for it.

| Aspect | ODS behavior | DMS decision | Reason |
| --- | --- | --- | --- |
| Sink | A table in the ODS database that handled the call. Each request repoints the shared appender at that database (`mw.cs:104-114`). The docs permit no other storage because the content may be sensitive (`ODS docs:7-21`) | A dedicated relational capture database ([D6](#d6-capture-store-and-retention)), never the logging pipeline | DMS has no database of its own, and a `dms` table per data store needs reprovisioning, splits rows across stores, and must avoid read replicas. The logging pipeline breaks R10. The departure from per-database confinement is recorded and goes to the gate; rows carry the tenant |
| Table creation | Operator-run DDL from the docs, for SQL Server (`dbo`) and PostgreSQL (`edfi`) | The product provisions the table ([D6](#d6-capture-store-and-retention)) | A store an operator must build by hand drifts from the writer |
| Enablement | Global `LogRequestResponseContentForMinutes` (`Ed-Fi-ODS:Application/EdFi.Ods.Common/Configuration/ApiSettings.cs:37`, default 0), shipped as 0 (`Impl:Application/EdFi.Ods.WebApi/appsettings.json:64`), and the appender must be configured. Both are read once, in the singleton's constructor (`mw.cs:40`) | Per-client CMS capture grants ([D2](#d2-control-surface)) | R1 and R2: the two limitations the work group named |
| Changing it | Needs a restart. The setting is read once, and log4net is loaded with the provider's defaults, which do not watch the file, since the shipped `appsettings.json` has no `Log4NetCore` section (`Impl:Application/EdFi.Ods.WebApi/Program.cs:62-73`) | Enable and disable propagate to every replica without a restart ([D2](#d2-control-surface)) | R2 |
| Capture window | Starts when the singleton is constructed, at the first `/data` or `/composites` request after startup (`mw.cs:43`; `ODS docs:303-312`). Each instance runs its own timer | Each grant has explicit StartsAt and ExpiresAt under an enforced maximum duration ([D2](#d2-control-surface)) | R3; the window is the operator's choice, not a side effect of traffic |
| What is captured | A log4net level. A 500 with a recorded exception logs at Error, any other status of 400 or above except 500 at Info, and everything else at Debug (`mw.cs:70-82`). The documented default threshold, ERROR, captures only exception 500s (`ODS docs:75-78`, `:88`, `:93`) | A per-grant mode: `Failures`, the default, or `All`. Failures means status 400 or above, or an exception ([D2](#d2-control-surface), [D6](#d6-capture-store-and-retention)) | R9, as a mode rather than a log level |
| The docs on levels | "Change ERROR to INFO to log all calls" (`ODS docs:75-78`). Stale since v7.2: v7.0 and v7.1 logged every non-500 at Info, and v7.2 moved responses below 400 to Debug. INFO now captures failures only, and all calls need DEBUG | Not applicable | Recorded so the parity claim is against behavior, not the docs |
| Routes | `/data` and `/composites` only. The token endpoint is never captured | The core data-plane pipelines only: resources, descriptors, change queries and partitions. Never token, identity, management, or CMS routes ([D4](#d4-credential-exclusion)) | R4, and DMS-1419's scope |
| Fields | Date (log4net `RawTimeStampLayout`, local time, no zone), thread, correlation ID (a client header, else a `correlationId` query parameter, else generated, `Ed-Fi-ODS:Application/EdFi.Ods.Api/Middleware/RequestCorrelationMiddleware.cs:25-39`), the numeric API client id (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Middleware/EdFiApiAuthenticationMiddleware.cs:123`; `Ed-Fi-ODS:Application/EdFi.Common/Security/ApiClientDetails.cs:79`), level, URL with the raw query string (`mw.cs:224`, 255 characters), method (10 characters), the raw profile content-type header (`Ed-Fi-ODS:Application/EdFi.Ods.Features/Profiles/ProfileContentTypeContextMiddleware.cs:84`, `:106`), full uncapped bodies, the status name or problem details as the message (`mw.cs:138-172`), and the exception (2000 characters) (`ODS docs:26-40`, `:95-187`) | Every column is classed server-generated or client-derived ([D4](#d4-credential-exclusion)). Adds the status code, duration, tenant, data store and grant. Stores the validated client key, the profile name DMS resolved, and the method as a server value. Stored bodies are capped and flagged ([D6](#d6-capture-store-and-retention)) | R6 and R12: BI tooling needs the status code and the tenant, and a capped body bounds one row |
| Not captured | Headers other than the profile header, the status code, the tenant, the duration | See the row above | |
| Correlation ID | Always stored (`NOT NULL` in the DDL) | Always stored. A client-supplied ID is withheld with the other client-derived fields when the D4 rule fires; an ID DMS generated is kept ([D4](#d4-credential-exclusion)) | R4: a client-supplied ID is client text. A departure from R12 for that case, recorded for the gate |
| Exception detail | log4net `ExceptionLayout`: the exception text, truncated to 2000 characters | The exception type plus a server-chosen description, never the message chain ([D4](#d4-credential-exclusion)) | R4: DMS exception chains can quote a connection string. A departure from R12's exception detail, recorded for the gate |
| Redaction | None. Bodies and the query string are stored as sent | Credential withholding ([D4](#d4-credential-exclusion)) and the payload policy ([D5](#d5-payload-policy)) | R4, and the ticket's redaction question |
| Retention | None. The table grows until an operator deletes rows | A hosted purge after `RetentionDays` ([D6](#d6-capture-store-and-retention)) | R11 |
| Write path | Synchronous: `bufferSize` 0 inserts on the request thread (`ODS docs:92`). While enabled, every in-scope request is buffered and its response copied through a `MemoryStream` (`mw.cs:57-61`) | A bounded asynchronous writer that fails open, and body retention only for an eligible grant ([D6](#d6-capture-store-and-retention)) | R5 |
| Audit of the control | None: a configuration file edit | Grant rows are never overwritten, and a structured CMS log event is emitted on grant and on end ([D2](#d2-control-surface)) | R8 |

**Code-read defects** in the precedent, found by reading the code and not confirmed by running it.
They are recorded so parity is not claimed for them.

- **Prefixed routes are never captured.**
  Under multi-tenancy, or with an `OdsContextRouteTemplate`, the route root template is prepended ahead of `data/` (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Conventions/OdsRouteRootTemplateProvider.cs:30-46`).
  The request path then starts with that prefix, so the `StartsWithSegments("/data")` test (`OdsStartupBase.cs:451-454`) is false and nothing is captured.
- **Rows can cross ODS databases.**
  The appender is one shared instance.
  Each request sets its connection string to the current request's ODS database and reactivates it, with no synchronization (`mw.cs:104-114`).
  Concurrent requests for different ODS databases can write a row into another request's database.
- **A 500 without a recorded exception is not captured.**
  The 500 arm logs content only when `context.Items["Exception"]` holds an exception (`mw.cs:72-75`).

## D1: Relationship to DMS-62

**Decision: keep DMS-62 separate, linked to DMS-1418 as `relates to`.**

DMS-62 is a user story about tracing and non-repudiation, not payload capture.
Its four acceptance criteria and its child tickets stand as follows.

| DMS-62 criterion | State | Relationship to this design |
| --- | --- | --- |
| Trace ID in log files can come from an upstream proxy or gateway | Met in code: `AppSettings:CorrelationIdHeader` (`docs/CONFIGURATION.md:199`). Its story, [DMS-66](https://edfi.atlassian.net/browse/DMS-66), was closed as a duplicate | Capture rows carry the same correlation ID, subject to [D4](#d4-credential-exclusion) |
| Enrich saved documents with the trace ID | Met: [DMS-65](https://edfi.atlassian.net/browse/DMS-65) Done | None |
| Incorporate OpenTelemetry | Partly met: OTLP log export, [DMS-1335](https://edfi.atlassian.net/browse/DMS-1335), is Done. [DMS-61](https://edfi.atlassian.net/browse/DMS-61) was closed as a duplicate and cloned into [DMS-63](https://edfi.atlassian.net/browse/DMS-63), auto-instrumentation, which is Open | None. OTLP export is why the logging pipeline is not the sink |
| Logs establish non-repudiation per the Ed-Fi API Guidelines | [DMS-64](https://edfi.atlassian.net/browse/DMS-64) Open | Out of scope: DMS-1419 excludes long-term audit and compliance retention |

**Why not merge.**
DMS-62's open work is OpenTelemetry auto-instrumentation and non-repudiation, which is audit.
Merging would pull audit retention into a capability the ticket defines as a time-boxed troubleshooting tool, contradicting its out-of-scope list.

**Why not supersede.**
This design delivers neither DMS-63 nor DMS-64, so superseding DMS-62 would orphan both.

**What links them.**
The correlation ID is the shared surface: a captured row joins to the log events of the same request through it.
The `relates to` link records that adjacency, and it answers the epic's own note to reconcile the two before refining.

## D2: Control Surface

**Decision: capture grants in CMS, polled by every DMS replica.**
An operator enables capture for one API client by creating a grant in CMS.
Each DMS replica polls CMS for the active grants and decides capture from that local snapshot, with no CMS call on the request path.

### The Grant

A grant is a new sub-resource of the API client, following the `reset-credential` precedent (`ApiClientModule.cs:159`).

| Endpoint | Authorization | Effect |
| --- | --- | --- |
| `POST /v3/apiClients/{id}/requestCapture` | `MapSecuredPost` (admin scope) | Creates a grant: a mode, `Failures` by default or `All`, and a duration. Refused with 409 if the client already has an active grant |
| `DELETE /v3/apiClients/{id}/requestCapture` | `MapSecuredDelete` (admin scope) | Ends the client's active grant early. 404 if there is none |
| `GET /v3/apiClients/{id}/requestCapture` | `MapSecuredGet` | The client's grant history, newest first |
| `GET /v3/requestCaptureGrants/active` | `MapLimitedAccess` | The active-grants read DMS polls, scoped by the `Tenant` header like every other DMS read |

It is backed by `dmscs.RequestCaptureGrant`, created by DbUp scripts for both engines.

| Column | Value |
| --- | --- |
| Id | The grant id |
| ApiClientId | The CMS API client, with a foreign key that cascades on the client's deletion |
| Mode | `Failures` or `All` |
| StartsAt | Set by CMS at creation. A grant starts when it is created; there is no scheduled start, because the ticket asks only for on, off, and timed expiry |
| ExpiresAt | `StartsAt` plus the requested duration |
| CreatedBy | The caller's identity, through `IAuditContext` |
| EndedAt, EndedBy | Set only when an operator ends the grant early |

**The duration is bounded.**
The request must give a duration, and CMS refuses one above a configured maximum, proposed as 24 hours.
So capture cannot be left on by accident (R3): a grant that is never ended expires on its own.

**Rows are never overwritten.**
Ending a grant sets `EndedAt` and `EndedBy` on a row that has none, and nothing else ever changes a row.
Changing the mode means ending the grant and creating another.
So the table is the history R8 asks for: who enabled capture, for which client, when, and who ended it.
CMS also emits a structured log event when a grant is created and when it is ended, carrying the grant id, the client, the mode, the window, and the caller.
A client's deletion removes its grants with it; long-term audit retention is out of scope, and the log events remain.

**The active-grants read contract.**
For each grant that is active now (not ended, and `ExpiresAt` in the future), it returns:

- the grant id
- the client key: the CMS `ApiClient.ClientId` that DMS's `JwtAuthentication` validates, not the CMS row id in the sub-resource path
- the tenant
- the mode
- `StartsAt` and `ExpiresAt`

Story 1 builds this read and story 4 consumes it.
If [D5](#d5-payload-policy) chooses a masking mode per grant, the grant gains that field and this read returns it.

### How Every Replica Learns of a Change

Each DMS replica runs a background poller, on the precedent of the backend's hosted services (`DocumentCacheProjectionSupervisor`).
It calls the active-grants read for each tenant the replica has loaded (`ConfigurationServiceDataStoreProvider.GetLoadedTenantKeys`, `:350`), through the existing CMS client and token handler.
The result replaces an immutable snapshot keyed by tenant and client key.
A request's capture decision is a lookup in that snapshot, so the per-request cost is a dictionary read and no CMS call.

### Freshness

Three values bound how current the snapshot is:

- `I`, the poll interval, proposed as 30 seconds as an operational target, not a ticket criterion
- `T`, the poll timeout
- `A`, the maximum snapshot age, about `2I + T`, which tolerates one failed poll

**The rules.**
- A snapshot's age is counted from the start of the poll that produced it, on the monotonic clock, so a slow response cannot make old data look new.
- A response from an older poll that arrives after a newer one is discarded.
- A snapshot older than `A` is stale, and a stale snapshot captures nothing: the control fails closed.
- `ExpiresAt` is also enforced locally between polls. It assumes DMS and CMS clocks are synchronized, as JWT validation already does.

**What an operator can rely on, which the documentation states:**

| Condition | Enable | Disable |
| --- | --- | --- |
| CMS reachable | Takes effect on every replica within `A` | Takes effect on every replica within `A` |
| CMS unreachable | Waits for the next successful poll | Still takes effect within `A`, because a stale snapshot captures nothing |

### The In-Flight Boundary

A request's capture decision is made once, at **authenticated selection**.

- When the frontend reads a request, it checks whether the client named in the not-yet-validated token has a grant, only to decide whether to keep a raw copy of the body ([D6](#placement-in-the-request-path)). That check is provisional: the client is not authenticated yet.
- The selection step runs after `JwtAuthentication`. It looks up the validated client key in the current snapshot and freezes the decision for that request.
- A disable that lands before selection prevents capture.
- A disable that lands after selection does not cancel the request: it is captured, and rows already queued for writing are written.

### Rejected Alternatives

- **Carrying the grant on `GET v3/apiClients/{clientId}`.** That read is cached for 600 seconds per replica, and DMS fetches it only for POST, ownership-based authorization, and profile resolution, so most GETs would never see a grant change.
- **A control table in DMS.** DMS has no database of its own, only a `dms` schema per data store.
- **Configuration reload.** DMS disables it (`Program.cs:26-29`), it is per process rather than per deployment, and an edited file records no one who changed it.

## D3: Readiness

**Decision: no readiness flag.**
The capture store is configured at deployment, by giving DMS its connection string ([D6](#d6-capture-store-and-retention)).

- With a store configured, the control path is always registered: the poller, and the selection and finalization steps. With no grants, which is the default, nothing is captured.
- With no store configured, nothing is registered, and the request pipeline is the one DMS runs today.

So an operator enables capture for a client without a restart (R2), and a deployment that never configures a store pays nothing (R5).

**The idle benchmark is an implementation gate.**
R5 asks for no measurable overhead when capture is off.
The capture-pipeline story must show that a deployment with a store configured and no grants adds no measurable latency over one with no store, and the story is not done until it does.
Its added allocations are held to a fixed budget: the per-request cost of the two capture steps, and nothing that grows with the request, such as a body copy or a token decode.
Zero added allocation is not the bar, because every registered step costs a closure, a delegate, and an async state machine per request whether or not it acts (`Pipeline/PipelineProvider.cs:23-28`).

**The fallback, if the benchmark fails.**
A `RequestResponseCapture:Enabled` flag would register the control path only when set, so its first enablement needs a one-time restart.
That gives up part of R2, so adopting it needs the renewed approval of the DMS-1419 owner.
It is not designed further here.

**Rejected alternative.**
A pre-filing prototype to measure the overhead before choosing was considered and not chosen: the benchmark gates the implementation instead, which costs nothing if it passes.

## D4: Credential Exclusion

**Decision: withhold, don't edit.**
When a request shows any sign of carrying a credential, every client-derived field of its row is withheld, and the server-generated fields and the reason are kept.
This is mandatory, and independent of the payload policy in [D5](#d5-payload-policy).

### Capture Scope

Capture covers only the core data-plane pipelines: resources, descriptors, change queries, partitions, and the 405 answer on a data route.
It never covers the token, token-info, identity, discovery, metadata, health, or management routes, and never CMS.

Every in-scope request body is JSON.
The only route that reads a form body is token-info (`AspNetCoreFrontend.cs:974-987`), which is out of scope.

No request header is stored except the correlation ID, as DMS normalized it.
The profile is stored as the profile name DMS resolved, not the raw content-type header ODS stores.

### Column Classes

Every stored column is either *server-generated* or *client-derived*.

**The provenance rule.**
A column is server-generated only when DMS computes its value, or draws it from a closed, server-controlled set after validation.
Any text copied from the request is client-derived.
The raw verb of an unsupported-method request shows why the rule is needed: it is client text, any verb reaches the authenticated 405 pipeline, and the 405 body echoes it (see [Current State](#dms-logging-and-the-request-pipeline)).
It is therefore not stored, and the method column holds the `RequestMethod` value, `UNSUPPORTED` for any other verb.

The column list and its classes form one **capture-row contract**, owned by the credential-exclusion story.
The store builds its table from that contract, and a test fails on any column the contract does not classify.
The outline below is that contract's starting point; the story fixes the names and types.

| Column | Class | Value |
| --- | --- | --- |
| Capture id, grant id, mode | Server-generated | Assigned by DMS, or copied from the grant |
| Received at, duration | Server-generated | UTC timestamp and elapsed time, measured by DMS |
| Method | Server-generated | The `RequestMethod` value; `UNSUPPORTED` for any other verb |
| Status code | Server-generated | The status of the response DMS sent |
| Client key | Server-generated | The `ClientId` that `JwtAuthentication` validated |
| Tenant, data store | Server-generated | The validated tenant, and the data store DMS resolved |
| Profile | Server-generated | The resolved profile name, or empty |
| Body state, truncation flags | Server-generated | `NoBody`, `Unread`, `Retained` or `NotRetained` ([D6](#placement-in-the-request-path)), and whether stored output was capped |
| Withheld, withholding reason | Server-generated | Whether the rule fired, and which trigger, from a fixed set |
| Exception type, exception description | Server-generated | The exception's type, and a description DMS chooses from a fixed set by type, never the message chain |
| Path, query | Client-derived | The request path, and the raw query string with every repeated value |
| Trace ID | Server-generated | The correlation ID when DMS generated it: `CorrelationIdHeader` is not set, or the client sent no usable value. Empty otherwise |
| Correlation ID | Client-derived | The normalized correlation ID when the client supplied it through `CorrelationIdHeader`. Empty otherwise |
| Request body, response body | Client-derived | The JSON as sent and as answered; a response can echo request content |

### The Withholding Rule

The rule examines every client-derived field before anything is stored.
If any trigger below holds, every client-derived column is stored empty, `Withheld` is set, and the first trigger found is recorded as the reason.

1. **A credential name.**
   A listed name appears as a query parameter name, or as a JSON property name at any depth of the request body or the response body.
2. **The bearer token.**
   The request's bearer token appears anywhere in a client-derived field, compared in three forms: as stored, JSON-unescaped, and percent-decoded.
   The decoders are lenient and never fail: an invalid escape or percent sequence is left as it is.
3. **Incomplete discovery.**
   - A request body fails to parse as JSON, and its text contains a listed name in any spelling the comparison below accepts.
   - A request body fails to parse as JSON, and it contains a NUL byte. UTF-16 or UTF-32 text, and compressed content such as a gzip body, which DMS does not decompress, hide listed names from the text comparison. JSON text never carries a raw NUL byte, so one marks a body the rule cannot read. This also keeps U+0000 out of PostgreSQL `text` columns, which reject it.
   - A body was sent to a route that does not read one (GET, DELETE, the change-query routes, and the 405 answer). DMS never reads that body, so the rule cannot examine it, and unexamined input fails closed. Such bodies are rare, and DMS ignores them.
   - The examination stopped before finishing, for example at a depth or size bound.

**The name comparison.**
Names are compared after JSON unescaping, or percent-decoding for query names.
Both sides are then lowercased with ordinal case folding and stripped of `_` and `-`.
The comparison is whole-name equality, not a substring test, so a name that only contains a listed word, such as `customizationKey`, never triggers.

**The credential-name list.**
In normalized form:

`clientsecret`, `clientassertion`, `password`, `passwd`, `secret`, `apikey`, `apisecret`, `accesstoken`, `refreshtoken`, `idtoken`, `token`, `bearer`, `authorization`, `privatekey`, `connectionstring`.

These cover the OAuth client-credentials and token fields DMS and CMS use, and the common names for keys, passwords, and connection strings.
The list is checked against every property name and query field name in the seven ApiSchema packages this repository references, all at `1.0.335`: DS 5.2 and DS 6.1, each with its Homograph and Sample variants, and DS 5.2 TPDM.
None of the 3,370 distinct names matches a listed entry.
Two candidates were dropped because they collide: `credential` and `credentials` are Ed-Fi resource and property names.
`key` is not listed: it is generic, and in ODS client credentials the key is the public identifier, not the secret.
The credential-exclusion story repeats the collision check as a test, so a new Data Standard cannot add a colliding name unnoticed.

**Ordinary bearer authentication never triggers it.**
The `Authorization` header is never stored, and trigger 2 looks only in client-derived fields.
A request triggers only when the token appears somewhere the client put it on purpose or by mistake.

**Precedence.**
Withholding is decided over the full content, before anything is truncated.
Truncation applies only to output that is stored, so a triggered body is withheld, not truncated.

### Trade-off and Departures

**The trade-off.**
A request that legitimately carries a credential-named field loses its client content.
Such requests are rare in Ed-Fi traffic: no Data Standard property bears a listed name.
The server-generated columns still record that the request happened, its client, its status, and why its content was withheld.

**Departures from R12, for gate approval.**
- A withheld row loses a client-supplied correlation ID, so it can be matched to the logs only by time, client, and status. A correlation ID DMS generated is kept, in the server-generated trace ID column, because the frontend already records which source a request's ID came from (`AspNetCoreFrontend.cs:473-480`). With `CorrelationIdHeader` unset, the default, no withheld row loses its join key. No third column class is added for the client-supplied case, because a class that is client text but exempt from the rule is the gap the rule exists to close.
- Exception detail is the type plus a server-chosen description, not the ODS exception text, because DMS exception chains can quote a connection string.

Both are recorded in the [ODS Precedent](#ods-precedent) table.

### The R4 Scope Statement

For gate approval:
captured content excludes the request's bearer token, and any credential recognized by name, in every client-derived field.
It does not exclude a secret placed in a field whose name is not on the list, such as a password typed into a free-text comment.
The credential-exclusion story's generative test, against an independent oracle, is strong evidence that the implementation meets this contract.
No test can prove the absolute claim that no secret is ever captured.

### Rejected Alternatives

- **Rewriting the offending value in place.** Masking only the field or substring that matched keeps more content, but every new place a credential can hide becomes a new case to mask correctly. Whole-row withholding is one rule with one test.
- **Storing the `Authorization` header masked.** It adds nothing a troubleshooter needs; the client key already identifies the caller.
- **Substring matching on names.** It would trigger on Data Standard names such as `customizationKey`, `keyValues`, and `credentialStatusDescriptor`.

## D5: Payload Policy

Pending. Records the accountable policy owner's decision on payload redaction (AC2), including the NFR-OBS-3 interpretation or exception.

## D6: Capture Store and Retention

**Decision: a dedicated relational capture database.**
DMS writes captured rows to one table in a database used for nothing else, in the same dialect as its data stores (`AppSettings:Datastore`).
BI and workflow tooling read it with SQL (R6).
Nothing captured passes through `ILogger`, so no log sink, OTLP exporter, or plugin `ILoggerProvider` can receive it (R10).

### Independence from the Payload Policy

This decision holds under every answer [D5](#d5-payload-policy) can give.
Payload redaction, if the policy requires any, is a transform applied to a row's bodies before the row reaches the writer, at the same point as the [D4](#d4-credential-exclusion) withholding rule.
The table's shape, the writer, the caps, and the purge do not change.
If the policy is a masking mode per grant, the row's grant id already identifies the grant and its mode, so the table gains no column.

### Configuration Surface

The store, and the D2 polling values, are DMS settings in a new `RequestResponseCapture` section.

| Setting | Default | Meaning |
| --- | --- | --- |
| `ConnectionString` | Empty | The capture database. Empty means the feature is absent: nothing is registered ([D3](#d3-readiness)) |
| `RetentionDays` | 7 | Rows older than this are purged |
| `MaxStoredBodyBytes` | 1,048,576 | The cap on each stored body; a longer body is truncated and flagged |
| `WriterQueueCapacity` | 1,000 | Rows waiting to be written; beyond it, rows are dropped |
| `PollIntervalSeconds` | 30 | `I` in [D2](#freshness) |
| `PollTimeoutSeconds` | 10 | `T` in [D2](#freshness); the maximum snapshot age `A` follows as `2I + T` |

The defaults are proposals for the story to confirm.
The connection string is secret-bearing.
It is supplied by environment injection, or by a Phase A configuration plugin ([DMS-1551](https://edfi.atlassian.net/browse/DMS-1551)), never by CMS: the secret references of [DMS-1554](https://edfi.atlassian.net/browse/DMS-1554) resolve only CMS-stored strings.
The maximum grant duration is a CMS setting ([D2](#the-grant)).

### Provisioning

**Decision: a SchemaTools subcommand creates the table; DMS runs no DDL.**
The command provisions the capture table and its indexes in a named database, for either dialect, on the pattern of `ddl provision` (`src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md`).
DMS's runtime credential then needs only to insert, read, and delete rows in that table, never to change a schema.
DMS has no deploy-on-startup setting, unlike CMS, and provisioning stays an explicit operator step, as it already is for data stores.

The command is idempotent: it creates what is missing and leaves an existing table alone.
Captured rows are disposable troubleshooting data with a short retention, so a later release that changes the table can recreate it rather than migrate it.

**Rejected:** DDL at DMS startup.
It removes one operator step, but it gives the DMS runtime schema-changing rights, and it is the only place DMS would run DDL.

### Placement in the Request Path

Capture touches the request at three points.

**1. Extraction, in the frontend.**
`FromRequest` attaches an immutable **capture context** to every in-scope request.
It holds the receive time, the raw query string with every repeated value, and the request body's state:

| Body state | Meaning |
| --- | --- |
| `NoBody` | The route reads no body, and none was sent; or an empty body was sent |
| `Unread` | A body was sent to a route that does not read one. [D4](#the-withholding-rule) trigger 3 withholds the row |
| `Retained` | The body was read, and a raw copy of its bytes was kept |
| `NotRetained` | The body was read, but no copy was kept, because no grant could apply when it was read |

The raw copy is taken before the frontend clears its buffer, and only when a grant could apply.
It is the bytes as sent, so a malformed body, a duplicate property, or a value that coercion later changes is stored as the client sent it.
The raw query string is kept because `FromRequest` keeps only the last value of a repeated parameter (`AspNetCoreFrontend.cs:704`).

**Narrowing the raw copy: decided yes.**
The frontend reads the `client_id` claim from the bearer token without validating it, and keeps a raw copy only when that client has an active grant for the tenant.
This is safe in both directions.
For a valid token, the unvalidated claim equals the validated one, provided the frontend takes the first `client_id` claim as `JwtAuthentication` does (`Security/JwtValidationService.cs:303-306`), so no request that selection would capture loses its body.
For an invalid token, authentication fails and the request is never captured, so the claim's value cannot add a capture.
The decode runs only when the tenant has an active grant, and without it every request of a granted tenant would pay for a copy.

**2. Selection, after `JwtAuthentication`.**
A new step looks up the validated client key in the current snapshot ([D2](#the-in-flight-boundary)) and freezes the decision on the request.
It is added only to the in-scope pipelines of [D4](#capture-scope).
An in-scope request with no capture context is not captured, and a warning is logged, so a route added later without one fails safe.

**3. Finalization, outside `CoreExceptionLogging`.**
A new step sits between `RequestResponseLogging` and `CoreExceptionLogging`, so it sees the response after `CoreExceptionLogging` has translated an exception into 403, 500 or 503 (`ApiService.cs:234-246`).
For a selected request whose status the grant's mode accepts, it builds the row, applies the D4 rule, and queues the row.
It serializes the response body only for such a request, so no other request pays for it.
It never throws into the request: a failure inside it is logged with metadata only.

### Semantics

**What counts as a failure.**
Under the `Failures` mode, a request is captured when its status is 400 or above, or when an exception escaped `CoreExceptionLogging`.
Under `All`, every selected request is captured.

**What is never captured.**
- A request that fails authentication: there is no validated client to match a grant.
- A response produced entirely by the frontend or the host, such as a request rejected before core runs: core never sees it.
- A request the client cancelled: no response was sent.

**The rethrow case.**
`RequestResponseLogging` rethrows a client cancellation as it is, and wraps any other exception that escaped `CoreExceptionLogging` (`RequestResponseLoggingMiddleware.cs:115-138`).
Since `CoreExceptionLogging` catches everything else, the second case is reached only by a defect in that step.
**Decided:** finalization records such a request as a failure, with status 500, the exception type and a server-chosen description, and no response body, and then rethrows the exception unchanged.
A cancellation is not captured.

**Caps.**
Each stored body is capped at `MaxStoredBodyBytes`; a longer body is truncated and its flag set.
The D4 rule is applied to the full body first, so a body that triggers it is withheld, not truncated.

**The writer.**
Rows go to a bounded in-memory queue, and a background writer inserts them in batches.
When the queue is full, the row is dropped and a warning is logged with the grant id and status only.
When the store fails, the rows are dropped and a warning is logged with metadata only.
The request is never delayed or failed by capture: the writer fails open.
On shutdown, the writer drains what it can within the host's shutdown timeout.

### Retention

A hosted purge in DMS deletes rows older than `RetentionDays`, in batches, on a fixed schedule (R11).
It runs under a database mutex so that one replica purges at a time, on the precedent of the DocumentCache administrative mutex: `pg_advisory_lock` on PostgreSQL and `sp_getapplock` on SQL Server (`Backend.Postgresql/PostgresqlDocumentCacheAdministrativeMutex.cs`, `Backend.Mssql/MssqlDocumentCacheAdministrativeMutex.cs`).
The table is indexed on the receive time, for the purge and for time-range queries, and on the client key with the receive time, and on the grant id, for the queries a troubleshooter runs.

### Recorded Consequences

- **The departure from ODS confinement.** ODS confines content to the database that handled the call ([ODS Precedent](#ods-precedent)). One capture database holds rows from every data store and tenant. It goes to the gate.
- **Rows carry the tenant.** Tenancy in DMS is routing, not a security boundary ([DMS-1477](https://edfi.atlassian.net/browse/DMS-1477)), so the tenant column is for filtering, and access to the database is the operator's control. The documentation story states that the capture database holds student data and must be secured like a data store.

### Rejected Alternatives

- **A `dms` table in each data store, which is ODS parity.** Data-store provisioning is create-only, so existing stores would need reprovisioning. Rows would split across stores, and GETs routed to read replicas could not write there.
- **The logging pipeline.** Every Serilog event can reach OTLP and plugin providers, which breaks R10.
- **Files.** Not consumable by workflow or BI tooling without another loader (R6), and they multiply per replica.
- **The CMS database.** It holds credentials and security configuration. Adding student payloads to it widens what its compromise exposes, and DMS would need write access to it.

## Stories

Six stories, each 2 SP or less, cover the scope; a seventh exists only if the payload policy requires redaction.
The drafts are in this folder, and [README.md](./README.md) holds their dependency and blocker tables.

| # | Story | Covers | SP | Depends on |
| --- | --- | --- | --- | --- |
| 01 | [CMS request-capture grants](./01-add-cms-request-capture-grants.md) | Configuration: [D2](#the-grant) | 2 | None |
| 02 | [Credential exclusion](./02-add-dms-credential-exclusion.md) | Redaction of credentials: [D4](#d4-credential-exclusion), and the stored-body cap | 2 | None |
| 03 | [Capture store and retention](./03-add-dms-capture-store-and-retention.md) | Capture storage: [D6](#d6-capture-store-and-retention) | 2 | 02 |
| 04 | [Grant polling and freshness](./04-add-dms-grant-polling-and-freshness.md) | Configuration reaching every replica: [D2](#freshness) | 2 | 01 |
| 05 | [Capture pipeline](./05-add-dms-capture-pipeline.md) | Capture in the request path, and the [D3](#d3-readiness) idle gate | 2 | 02, 03, 04 |
| 06 | Payload redaction | Only if [D5](#d5-payload-policy) requires redaction; estimated then | - | - |
| 07 | [Documentation](./07-document-request-response-capture.md) | Documentation (R7) | 1 | 01 to 05 |

**Total.**
11 SP for the six stories, plus story 06's estimate if it is filed.
The epic's Confluence estimate is 10 SP; the difference is grant polling, split out of the capture pipeline so that no story exceeds 2 SP.

**Boundaries.**
- Credential exclusion owns the capture-row contract, so the store builds its table from it and depends on it.
- The stored-body cap lives in credential exclusion rather than in the store. "Withhold before truncate" is then a property of one pure component, proven by its generative test, instead of an ordering rule between two stories.
- No story depends on the payload policy. If D5 requires redaction, story 06 is filed and blocks 05 and 07, so capture cannot be activated without it.

## Disposition

Pending. Who approved the decisions and stories, and when; then the filed tickets (AC5).
