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
`RequestResponseDetailsLoggerMiddleware` (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Middleware/RequestResponseDetailsLoggerMiddleware.cs`, cited below as `mw.cs`) writes through a log4net `AdoNetAppender` to a `RequestResponseContentLog` table.
It is registered as a singleton (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Container/Modules/ApplicationModule.cs:505-516`).
It runs only for paths that start with `/data` or `/composites` (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Startup/OdsStartupBase.cs:451-454`), after correlation, tenant identification, authentication, and ODS instance identification (`OdsStartupBase.cs:400-440`).

**Nothing ships it.**
None of the nine log4net configurations in `Impl:` at `v7.3.2` defines the `RequestResponseContentLogger` logger or an `AdoNetAppender`.
The web API's own `Impl:Application/EdFi.Ods.WebApi/log4net.config` defines only `NHibernate` and the separate file-based `RequestResponseDetailsLogger`.
Neither repository creates the table: operators run the DDL from the docs by hand (`ODS docs:14-62`).
This rests on GitHub code search for `RequestResponseContentLog`, which indexes only each repository's default branch and finds the name only in `mw.cs`, plus no file in either `v7.3.2` tree named for it.

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
| Fields | Date (log4net `RawTimeStampLayout`, local time, no zone), thread, correlation ID (client header or generated, `Ed-Fi-ODS:Application/EdFi.Ods.Api/Middleware/RequestCorrelationMiddleware.cs:25`), the numeric API client id (`Ed-Fi-ODS:Application/EdFi.Ods.Api/Middleware/EdFiApiAuthenticationMiddleware.cs:123`; `Ed-Fi-ODS:Application/EdFi.Common/Security/ApiClientDetails.cs:79`), level, URL with the raw query string (`mw.cs:224`, 255 characters), method (10 characters), the raw profile content-type header (`Ed-Fi-ODS:Application/EdFi.Ods.Features/Profiles/ProfileContentTypeContextMiddleware.cs:84`, `:106`), full uncapped bodies, the status name or problem details as the message (`mw.cs:138-172`), and the exception (2000 characters) (`ODS docs:26-40`, `:95-187`) | Every column is classed server-generated or client-derived ([D4](#d4-credential-exclusion)). Adds the status code, duration, tenant, data store and grant. Stores the validated client key, the profile name DMS resolved, and the method as a server value. Stored bodies are capped and flagged ([D6](#d6-capture-store-and-retention)) | R6 and R12: BI tooling needs the status code and the tenant, and a capped body bounds one row |
| Not captured | Headers other than the profile header, the status code, the tenant, the duration | See the row above | |
| Correlation ID | Always stored (`NOT NULL` in the DDL) | Stored, but withheld with the other client-derived fields when the D4 rule fires ([D4](#d4-credential-exclusion)) | R4: it can come from a client header. A departure from R12, recorded for the gate |
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
| Trace ID in log files can come from an upstream proxy or gateway | Met: [DMS-66](https://edfi.atlassian.net/browse/DMS-66) Done; `AppSettings:CorrelationIdHeader` (`docs/CONFIGURATION.md:199`) | Capture rows carry the same correlation ID, subject to [D4](#d4-credential-exclusion) |
| Enrich saved documents with the trace ID | Met: [DMS-65](https://edfi.atlassian.net/browse/DMS-65) Done | None |
| Incorporate OpenTelemetry | [DMS-61](https://edfi.atlassian.net/browse/DMS-61) Done; [DMS-63](https://edfi.atlassian.net/browse/DMS-63) auto-instrumentation Open | None. OTLP export is why the logging pipeline is not the sink |
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

Pending. Decides the operator control for R1, R2, R3, R8 and R9 (AC3): the CMS grant model, how every replica learns of a change, its freshness bound, the in-flight boundary, and the rejected alternatives.

## D3: Readiness

Pending. Decides how the capture path is present at runtime when no grant exists (R2, R5), and the idle benchmark that proves R5.

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
| Body state, truncation flags | Server-generated | Whether a body was absent, retained, or not retained, and whether stored output was capped |
| Withheld, withholding reason | Server-generated | Whether the rule fired, and which trigger, from a fixed set |
| Exception type, exception description | Server-generated | The exception's type, and a description DMS chooses from a fixed set by type, never the message chain |
| Path, query | Client-derived | The request path, and the raw query string with every repeated value |
| Correlation ID | Client-derived | The normalized correlation ID, which comes from a client header when `CorrelationIdHeader` is set |
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
- A withheld row loses its correlation ID, which is client-derived, so it can be matched to the logs only by time, client, and status. No third column class is added to avoid this, because a class that is client text but exempt from the rule is the gap the rule exists to close.
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

Pending. Records the accountable policy owner's decision on payload redaction (AC2), including the NFR-OBS-3 interpretation.

## D6: Capture Store and Retention

Pending. Decides the sink (AC3; R6, R10, R11): the dedicated capture database, how it is supplied and provisioned, where capture sits in the pipeline, failure and capping semantics, and the purge.

## Stories

Pending. The estimated story set (AC4), with dependencies.

## Disposition

Pending. Who approved the decisions and stories, and when; then the filed tickets (AC5).
