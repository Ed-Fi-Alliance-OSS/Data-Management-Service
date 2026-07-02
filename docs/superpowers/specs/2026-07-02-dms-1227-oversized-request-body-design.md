# DMS-1227: Oversized Request Body Handling

## Problem
The DMS frontend currently turns Kestrel's request-body-too-large rejection into a generic 500 response through `LoggingMiddleware`. That behavior hides the real transport-level failure, logs it as an application exception, and makes the OWASP oversized-body E2E path unreliable because the client can lose the connection before it captures a response.

## Goals
- Return HTTP 413 for request bodies that exceed the configured max request body size.
- Keep oversized-body rejections out of the generic 500 / `InvalidOperationException` path.
- Make one relational E2E scenario reliably observe the 413 response.
- Add unit coverage for the middleware mapping.

## Non-goals
- Do not change the request behavior for all POST/PUT helpers.
- Do not redesign the global exception pipeline.
- Do not change the configured request body limit semantics beyond making the value explicitly configurable.

## Design
### Middleware behavior
`LoggingMiddleware` will special-case Kestrel's `BadHttpRequestException` when it represents a request-body-too-large rejection. For that case, the middleware will:
- log the failure as an expected oversized request rejection,
- set the response status to `413 Payload Too Large` if the response has not started,
- avoid writing the current generic 500 response body,
- avoid wrapping or rethrowing the exception as `InvalidOperationException`.

All other exceptions continue to follow the current generic failure path so unrelated errors still produce the existing 500 response and logging behavior.

The max request body size itself should be represented as a configuration value with a single shared source of truth, not as an inlined byte-count expression in the middleware or tests.

### E2E reliability
Add a dedicated oversized-request helper in the E2E step definitions that is used only by the new OWASP scenario. That helper will:
- send a JSON body larger than the configured limit,
- include `Expect: 100-continue` on that single request,
- preserve the existing shared write helpers for every other scenario,
- compute the oversized payload size from the same configurable limit rather than relying on a hardcoded byte count.

The helper will remain isolated so the change does not alter normal request flow or affect other tests.

### OWASP scenario
Add a new relational-backend scenario to `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature` that:
- uses the existing `schools` seed from the background,
- posts the oversized JSON payload to `/ed-fi/schools`,
- asserts `413`,
- carries `@relational-backend` and `@relational-ci-shard-3`.

## Data flow
1. The oversized E2E helper sends the request with `Expect: 100-continue`.
2. Kestrel rejects the body before normal request processing completes.
3. `LoggingMiddleware` recognizes the request-body-too-large exception and maps it to `413`.
4. The E2E scenario captures the 413 response and verifies the behavior end to end.

## Error handling
- Oversized request bodies: return 413, log as an expected rejection, no generic application-failure wrapper.
- Other exceptions: keep the existing 500 behavior and logging so unrelated regressions remain visible.

## Testing
### Unit
- Add a focused middleware test that proves a Kestrel request-body-too-large exception produces 413 and does not take the generic 500 path.

### E2E
- Add the relational OWASP oversized-body scenario.
- Verify the scenario succeeds through the relational E2E path with the targeted shard filter.

## Implementation boundary
The code changes should stay inside:
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/LoggingMiddleware.cs`
- the E2E step definition file that owns POST request helpers
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature`
- the corresponding middleware unit test project



