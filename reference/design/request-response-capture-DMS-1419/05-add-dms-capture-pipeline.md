---
jira:
epic: DMS-1418
source_spike: DMS-1419
---

# Story: Capture Request and Response Content in the DMS Pipeline

## TL;DR

Turns capture on in the request path: when a client has an active grant, DMS records its failed requests, or all of them, with the request and response content.
When no grant is active, DMS behaves as it does today, with no measurable added cost.

## Description

The other stories build the parts: the grant, the snapshot, the row and its safeguard, and the store.
This story connects them in the request path, at three points: the frontend keeps what the request carried, a step after authentication decides whether to capture, and a step outside exception translation builds and queues the row.
It is also where R5 is proven: the idle benchmark gates this story.

Per `reference/design/request-response-capture-DMS-1419/design.md` ("### The In-Flight Boundary" under D2, "## D3: Readiness", and "### Placement in the Request Path" and "### Semantics" under D6).

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Core/` names `src/dms/core/EdFi.DataManagementService.Core/`.
`Frontend/` names `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/`.

## Acceptance Criteria

**Extraction, in the frontend**

- `FromRequest` (`Frontend/AspNetCoreFrontend.cs`) attaches an immutable capture context to every request of an in-scope route, holding the receive time, the raw query string, whether the client supplied the correlation ID, and the body state: `NoBody`, `Unread`, `Retained`, or `NotRetained`.
- The correlation ID counts as client-supplied when the frontend's correlation-ID ingestion reports a client value that did not fall back to the server identifier (`ClientSuppliedAValue` and not `FellBackToServerIdentifier`, `Frontend/AspNetCoreFrontend.cs`). Core receives only the normalized ID, so the row's trace ID and correlation ID columns are filled from this flag.
- A raw copy of the body bytes is taken before the buffer is cleared, only when the client named by the bearer token's `client_id` claim, read without validation, has an active grant for the tenant. The read takes the first `client_id` claim, as `JwtValidationService` does (`Core/Security/JwtValidationService.cs`).
- The claim is read only when the tenant has an active grant. A token that cannot be decoded yields no copy and never fails the request.

**Selection, after `JwtAuthentication`**

- A new step after `JwtAuthentication` looks up the validated client key in the snapshot and freezes the decision on the request.
- It is added only to the in-scope pipelines: resources, descriptors, change queries, partitions, and the 405 answer on a data route.
- An in-scope request with no capture context is not captured, and a warning is logged.

**Finalization, outside `CoreExceptionLogging`**

- A new step runs between `RequestResponseLogging` and `CoreExceptionLogging` (`Core/ApiService.cs`, `GetCommonInitialSteps`).
- For a selected request, under `Failures` it captures a status of 400 or above, or an exception; under `All`, every request.
- It builds the row, passes it through the credential-exclusion component, and queues it to the writer.
- It serializes the response body only for a request it captures.
- When `RequestInfo.CaughtException` is set, the row records its type and the description the credential-exclusion component maps it to (`Core/Middleware/CoreExceptionLoggingMiddleware.cs`).
- An exception that escapes `CoreExceptionLogging` is captured as a 500 with its type and no response body, then rethrown unchanged.
- A client cancellation, and a request that fails authentication, are not captured.
- The step never throws into the request: a failure inside it logs a warning with metadata only.

**Readiness**

- With `RequestResponseCapture:ConnectionString` empty, no capture step, context, or service is registered, and the pipeline composition equals today's.

**Performance gate**

- An idle benchmark compares a store configured with no grants against no store. The story is not done until it shows:
    - no measurable difference in p50 and p95 latency, measured on a host with pooled connections, such as the Docker stack. The in-process API host is not used for latency, because it opens a new PostgreSQL backend for every request.
    - added allocations per request within the fixed cost of the two capture steps (`Core/Pipeline/PipelineProvider.cs`) and of the capture context, and none that grows with the request. They are counted in-process, over the in-scope pipelines with a fake backend.
- With an active grant, the same measurements, plus peak memory, are documented in the pull request.
- If the idle gate fails, the restart-dependent flag fallback of design.md ("## D3: Readiness") is not adopted without the DMS-1419 owner's renewed approval.

**Tests**

- Units use FakeItEasy, fake time, and explicit signals, never delays, and cover:
    - selection matches the granted client and not another
    - a grant removed between extraction and selection: the request is not captured
    - a grant published between extraction and selection, for a POST whose malformed body is `{"password":"alpha",` and whose query is `?note=alpha`: the request is captured with body state `NotRetained`, `Withheld` is set with the incomplete-discovery reason, and `alpha` appears in no field. Extraction runs against a snapshot without the grant, the test then publishes the grant, and selection runs after that, with no delays
    - a disable after selection still captures that request, and queued rows are written
    - a bodyless route is captured with `NoBody`
    - a client-supplied correlation ID fills only the correlation ID column, and a generated one only the trace ID column
    - every in-scope route creates a context; a missing one is not captured and warns
    - status boundaries 399, 400, 499, and 500, and the exception path, under both modes
    - the rethrow case and the cancellation case
    - an unhandled exception translated to 500 records its type, and the circuit-open 503 records none
    - the empty connection string registers nothing
- Fidelity: malformed, duplicate-property, and coerced bodies are stored as sent.
- Translated exceptions: captured 403, Snapshot Not Found 404, 500, and 503 responses equal the HTTP response.
- API integration on both engines: a failed POST and a GET-many under `All` each produce exactly one row, and `/oauth/token` produces none. Rows are located by a per-test grant id, and the row count is asserted before content.
- One end-to-end scenario, tagged `@DMS-<key>`, enables a grant through CMS, sends a failing request, and finds its row. It and the latency benchmark run on the Docker environment's capture opt-in from the capture-store story.

## Dependencies

- Credential exclusion (02): every row passes through it.
- Capture store and retention (03): the writer, and the Docker capture opt-in.
- Grant polling and freshness (04): the snapshot.

## Estimate

2 SP.
