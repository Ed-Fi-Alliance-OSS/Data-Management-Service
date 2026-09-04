---
jira: DMS-1413
jira_url: https://edfi.atlassian.net/browse/DMS-1413
parent: DMS-1412
---

# Identity Management Design

## Status

Draft design output of spike `DMS-1413`.
This document is design-only: it defines the target shape and the story breakdown.
It does not implement the API, create the contract package, register plugin contracts, or edit Jira.

The design intentionally uses the plugin architecture from `reference/design/plugins-DMS-1462/`.
That architecture is designed but not implemented at the time of this spike, so deploy-time plugin replacement is assigned only to a downstream story that depends on the plugin registry and loader stories.

## Goals and Non-Goals

### Goals

1. Expose the five Ed-Fi Identities API operations under `/identity/v2` when the feature is enabled.
2. Keep the feature off by default and omit both routes and identity metadata when disabled.
3. Let an implementer-provided plugin service the identity operations through a public, versioned contract.
4. Let DMS broker requests and responses without interpreting identity semantics, match scores, or custom person fields.
5. Support synchronous `200` responses and asynchronous `202` responses with a `Location` header and later results polling.
6. Publish OpenAPI and Discovery entries only when the feature is enabled.
7. Break the work into stories that respect the current plugin-foundation dependency graph.

### Non-Goals

- No concrete identity-system integration and no DMS-shipped identity backend.
- No person matching, scoring, or UniqueId issuance logic in DMS.
- No validation of UniqueIds on person-resource writes; that belongs to DMS-1414.
- No Model 2 work. DMS already supports clients supplying their own UniqueIds without validation.
- No plugin-contributed HTTP routes for identity. DMS owns the HTTP surface.
- No ApiSchema-generated OpenAPI for this surface.
- No profile scoping and no request-body validation below duplicate-property rejection and top-level shape checks.
- No response-payload schema validation beyond required-value presence.
- No datastore authorization, datastore resolution, or route-qualifier matching for identity calls.

## Evidence Base

The target surface is shaped by the DMS-1413 Jira ticket, ODS/API 7.3 identity behavior, existing DMS fixed-route and Core-pipeline patterns, and the DMS-1462 plugin design.

Important DMS source facts:

- `ApiService.GetCommonInitialSteps()` composes request logging, exception logging, tenant syntax validation, JWT authentication, and datastore resolution. Identity reuses the first four and deliberately omits datastore resolution.
- `TenantValidationMiddleware` checks tenant presence, length, and `^[a-zA-Z0-9_-]+$`; it does not check tenant existence.
- `TenantValidator.ValidateTenantAsync` checks tenant existence by consulting `IDataStoreProvider.TenantExists` and reloading from Configuration Service on cache miss.
- Existing resource authorization obtains claim sets by calling `IClaimSetProvider.GetAllClaimSets(requestInfo.FrontendRequest.Tenant)`, and the CMS-backed claim-set provider sends that tenant as a Configuration Service `Tenant` header. Identity service-claim authorization therefore must not run before DMS has established that the tenant exists.
- `AspNetCoreFrontend.ExtractJsonBodyFrom` already parses request bodies and records duplicate-property paths before `JsonNode` collapses duplicates; `DuplicatePropertiesMiddleware` consumes that path.
- `ValidateContentTypeMiddleware` currently accepts baseline JSON and Ed-Fi profile media types. Identity needs baseline JSON only.
- `CoreExceptionLoggingMiddleware` rethrows `OperationCanceledException` only when `RequestInfo.RequestCancellationToken` is cancelled. The identity provider-call boundary must rethrow cancelled provider calls and map un-cancelled provider exceptions to `502`.
- `src/dms/Dockerfile` has explicit project copy lists and uses `dotnet restore --locked-mode`; any new project referenced by DMS must be added to both copy lists and carry a lock file.

Important DMS-1462 plugin facts:

- A plugin is delivered as a directory under the configured plugin root and allowlisted by name.
- Plugins contribute services through `EdFiApiPlugin.ContributeServices`.
- Extension contracts are declared in a host registry.
- The identity service is a replace-cardinality contract: the host default plus one plugin replacement is allowed; two replacing plugins are fatal.
- Contract assembly names are derived from the registry as assembly names, not package IDs.
- Plugin-contributed HTTP routes are deferred; DMS owns identity routes.

## API Surface

The base route is `/identity/v2`.
The same fixed-route prefix rules used by `changeQueries` apply, so multitenancy and configured route qualifiers appear before `/identity/v2` when enabled.

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/identity/v2/identities` | Request creation of a new unique id |
| `GET` | `/identity/v2/identities/{id}` | Retrieve one person record by unique id |
| `POST` | `/identity/v2/identities/find` | Retrieve multiple person records by unique id |
| `POST` | `/identity/v2/identities/search` | Search for existing unique ids or candidate matches |
| `GET` | `/identity/v2/identities/results/{id}` | Poll an asynchronous find/search result |

Routing must assert two adjacent cases:

- `GET /identity/v2/identities/results` is `GetByIdAsync("results")`.
- `GET /identity/v2/identities/results/{token}` is `ResultsAsync(token)`.

The design makes no claim about unique ids containing `/`.
They are single route-segment values here, as in ODS.

## Contract Package

Story 01 creates `src/dms/core/EdFi.DataManagementService.Identity/`, packaged as `EdFi.Api.Identity`.
The namespace is `EdFi.DataManagementService.Identity`, following the custom-validation contract precedent.
The package is public API and is additive-only after publication.
Because this is a plugin-facing contract assembly, the project declares its own `Version`, `AssemblyVersion`, and `FileVersion`, initially `1.0.0`, independent of the DMS release version.
Story 01 owns the package assertion proving the nupkg's contained assembly version equals the identity contract package version.
The plugin-registration story owns the runtime image assertion after DMS-1499, proving the DMS image also carries `EdFi.DataManagementService.Identity.dll` at that same contract assembly version.
The deploy-time replacement story cannot land until the DMS-1462 Docker-lane removal of global `/p:AssemblyVersion` and `/p:FileVersion` stamping is present, because command-line global properties override a contract project's declared version.

The contract project declares its own DTOs and result types so the public package does not depend on DMS internal Core models.
It carries XML documentation because third-party implementers must be able to implement against the package without reading DMS source.

Recommended public surface:

```csharp
[Flags]
public enum IdentityCapabilities
{
    None = 0,
    Create = 1,
    GetById = 2,
    Find = 4,
    Search = 8,
    Results = 16,
}

public enum IdentityResultStatus
{
    Success,
    Incomplete,
    InvalidProperties,
    NotFound,
}

public sealed record IdentityError
{
    public required string Message { get; init; }
    public string? Path { get; init; }
}

public sealed record IdentityRequestContext
{
    public string? Tenant { get; init; }
    public IReadOnlyDictionary<string, string> RouteQualifiers { get; init; } = new Dictionary<string, string>();
    public required string TraceId { get; init; }
}

public sealed record IdentityResult
{
    public required IdentityResultStatus Status { get; init; }
    public JsonNode? Payload { get; init; }
    public IReadOnlyList<IdentityError> Errors { get; init; } = [];
}

public sealed record IdentityAsyncResult
{
    public required IdentityResultStatus Status { get; init; }
    public JsonNode? Payload { get; init; }
    public string? RequestToken { get; init; }
    public IReadOnlyList<IdentityError> Errors { get; init; } = [];
}

public interface IIdentityService
{
    IdentityCapabilities Capabilities { get; }

    Task<IdentityResult> CreateAsync(
        JsonObject request,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    Task<IdentityResult> GetByIdAsync(
        string uniqueId,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    Task<IdentityAsyncResult> FindAsync(
        IReadOnlyList<string> uniqueIds,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    Task<IdentityAsyncResult> SearchAsync(
        IReadOnlyList<JsonObject> requests,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );

    Task<IdentityResult> ResultsAsync(
        string requestToken,
        IdentityRequestContext context,
        CancellationToken cancellationToken
    );
}
```

`IdentityResult` has no request-token member.
Only find and search can return `IdentityAsyncResult`, so tokens are impossible from create, get-by-id, and results.
`FindAsync` and `SearchAsync` may return `Success`, `InvalidProperties`, or `NotFound`; `Incomplete` is valid only from `ResultsAsync`.
If a provider returns `Incomplete` from any operation other than `ResultsAsync`, DMS treats it as provider contract misuse.
A `RequestToken` is meaningful only on `Success`; DMS ignores any token returned alongside `InvalidProperties` or `NotFound`.
`IdentityError` entries are projected only for `InvalidProperties`.
Upstream provider failures are signaled by throwing; DMS logs the exception and returns a sanitized identity-upstream-failure problem without provider error details in the client response.

`IdentityCapabilities` is deployment-wide in the first contract.
Per-tenant or per-route-qualifier restrictions are enforced by provider methods returning `NotFound` or `InvalidProperties`.
A later context-aware capability method must be added either as a default interface member or in a new interface/package versioning path; adding a required member to the published plugin-implemented interface is a breaking change.

## Request Obligations

Create and search request bodies are JSON objects with the same standard identifying fields, plus any custom fields an implementer supports.
DMS validates duplicate property names and the structural request shape needed to call the provider: object for create, array of strings for find, and array of objects for search.
DMS does not require standard fields, validate their values, or reject custom fields.
The identity provider decides which standard or custom request fields are required for its integration and returns `InvalidProperties` when request data is insufficient.

`IdentityCreateRequest` and `IdentitySearchRequest` have these standard wire properties:

| Property | Requirement |
| --- | --- |
| `LastSurname` | standard nullable string |
| `FirstName` | standard nullable string |
| `MiddleName` | standard nullable string |
| `GenerationCodeSuffix` | standard nullable string |
| `SexType` | standard nullable string |
| `BirthDate` | standard nullable `date-time` string |
| `BirthOrder` | standard nullable integer |
| `BirthLocation` | standard nullable object with `City`, `StateAbbreviation`, `InternationalProvince`, and `Country` nullable string children |

Clients use `null` or omission for unknown request values.
Providers should treat both as unknown unless their own validation requires the field.
Create and search requests do not include `UniqueId` or `Score`.
The find request body remains an array of unique-id strings.
DMS rejects malformed array entries before provider invocation: find arrays may contain only JSON strings, and search arrays may contain only JSON objects.

## Response Obligations

DMS treats provider response payloads as opaque JSON.
That means DMS does not inspect or validate the response shape at runtime; it does not mean the shape is undefined.
The contract and OpenAPI document must describe what the provider must return on each successful operation.

| Operation/status | Required provider payload |
| --- | --- |
| Create `Success` | A JSON string containing the new unique id |
| GetById `Success` | An `IdentityResponse` object |
| Find/Search synchronous `Success` | An `IdentitySearchResponse` object |
| Results `Success` | An `IdentitySearchResponse` object with wire `Status` complete |
| Results `Incomplete` | An `IdentitySearchResponse` object with wire `Status` incomplete. The object itself is still required, so the missing-payload rule below still applies; it simply carries no result data while the request is pending |

`IdentityResponse` is a JSON object with these standard wire properties:

| Property | Requirement |
| --- | --- |
| `UniqueId` | required non-empty string for every returned identity |
| `LastSurname` | required, nullable string |
| `FirstName` | required, nullable string |
| `MiddleName` | required, nullable string |
| `GenerationCodeSuffix` | required, nullable string |
| `SexType` | required, nullable string |
| `BirthDate` | required, nullable `date-time` string |
| `BirthOrder` | required, nullable integer |
| `BirthLocation` | required object with `City`, `StateAbbreviation`, `InternationalProvince`, and `Country` properties, each nullable string |
| `Score` | required, nullable number with `double` format as the confidence indicator; search matches must provide a numeric value from 0 through 100 |

Providers represent unsupported standard attributes as `null`.
If birth-location values are unsupported, `BirthLocation` is still present with its standard child properties set to `null`.
Providers may add custom properties to `IdentityResponse`, `BirthLocation`, and request objects; DMS passes those properties through without inspecting them.

`IdentitySearchResponse` is a JSON object with:

- `Status`: required string, either `Complete` or `Incomplete`;
- `SearchResponses` when `Status` is `Complete`: a required array with one entry per submitted UniqueId or search request, in request order;
- `SearchResponses` when `Status` is `Incomplete`: optional, because no result data exists yet. An incomplete poll may omit the property or send an empty array, matching ODS. Schemas declare `SearchResponses` required only for the complete shape;
- each `SearchResponses` entry has a required `Responses` array;
- find entries contain zero or one `IdentityResponse`;
- search entries contain zero or more `IdentityResponse` values and every returned match has a numeric `Score`.

A find or search request with no matching identity is represented as `Success` with an empty `Responses` array in the corresponding response group, not as provider `NotFound`.
DMS does not inspect that runtime shape, but the contract documentation, OpenAPI examples, and fixture tests must model no-match responses this way.

DMS does not read `Score`, does not enforce a score threshold, and does not inspect person data.
The API-surface and fixture stories prove the schema and examples include the standard identifying attributes, unsupported-as-null semantics, ordered search-response groups, and required search confidence scores.

Provider success with a missing payload is contract misuse and maps to provider-contract-violation `502`.
Provider success with a payload whose shape does not match the documented schema is still served verbatim; that is a provider bug, not a host validation failure.
Provider integration failures are represented by exceptions, not by an `IdentityResultStatus` value.
Non-cancelled provider exceptions map to identity-upstream-failure `502`; provider-supplied diagnostic detail belongs in logs, not in the client response.

## Feature Toggle

Add `AppSettings:EnableIdentityManagement`, default `false`.

When disabled:

- none of the five identity routes are mapped;
- `/metadata/identity/v2/swagger.json` is absent;
- the metadata listing does not include `Other: Identity`;
- the Discovery response has no `identity` URL;
- provider registrations and plugin loading are not controlled by this toggle.

When enabled with only the DMS host default `NoIdentityService`, the process starts cleanly and every operation answers operation-unsupported `404`.

This feature toggle does not replace `Plugins:Allowed`.
An operator still uses the plugin architecture allowlist to load the implementer plugin.

## OpenAPI and Discovery

The identity document is a fixed OpenAPI document served from `/metadata/identity/v2/swagger.json`.
It is listed under `Other: Identity`, matching the ODS grouping.
The Discovery response includes:

```json
{
  "urls": {
    "identity": "{root}{routeQualifierPrefix}/identity/v2/"
  }
}
```

The OpenAPI document is emitted only when `EnableIdentityManagement` is true.
It declares:

- request media types `application/json` and `text/json` for all three POST operations;
- top-level request shapes of object for create, array of string for find, and array of object for search, with the standard identifying fields documented on create/search object schemas;
- response body schemas per operation, with `additionalProperties: true` on request and response objects so custom fields remain legal;
- schemas remain property-for-property and type-for-type compatible with the pinned ODS 7.3.2 identity OpenAPI document, whose identity schemas declare no schema-level `required` arrays and no `nullable` keywords; requiredness and nullability are therefore DMS additions rather than matched dimensions, and every such addition is named in the divergence ledger, as is any other difference;
- client-visible error responses for validation `400`, provider `InvalidProperties` `400`, unsupported-media-type `415` on POST operations, operation-unsupported `404`, identity-not-found `404`, provider-contract-violation `502`, and identity-upstream-failure `502`, with problem-detail schemas or documented problem-detail `type` values where DMS emits problem details;
- a `servers` array injected with the actual route-qualified runtime base ending in `/identity/v2`;
- `paths` keys relative to that base, exactly `/identities`, `/identities/{id}`, `/identities/find`, `/identities/search`, and `/identities/results/{id}`, so the `/identity/v2` base appears only in `servers`;
- the same OpenAPI 3 `oauth2_client_credentials` security scheme and root `security` requirement DMS injects into other authenticated metadata documents;
- `202` `Location` headers for async find and search;
- `200` `Location` for incomplete results, where the value points back to the current poll URL;
- no create `Location`, because the chosen DMS create behavior is `200` with the unique-id string body.

## Authorization

The service claim already exists in shipped claim documents:

```text
http://ed-fi.org/identity/claims/services/identity
```

DMS does not need a CMS migration for this ticket.
The identity authorization middleware should use the existing claim-set graph and map operations to actions:

| Operation | Required action |
| --- | --- |
| `POST /identities` | `Create` |
| all other identity operations | `Read` |

Authorization runs before the capability gate, media-type gate, body parsing, duplicate-property rejection, body-shape validation, and provider call.
This prevents unsupported deployments from revealing whether a request would otherwise have failed as malformed or unsupported.

Tenant existence is different because current claim-set retrieval is tenant-keyed.
The sequence is:

1. tenant syntax validation;
2. JWT authentication;
3. tenant existence validation;
4. service-claim authorization.

That ordering is deliberate.
Unauthenticated callers still receive `401` and do not reach tenant existence.
Authenticated callers with a valid token can receive tenant `404` before service-claim `403`; that is accepted because any middleware that calls `IClaimSetProvider.GetAllClaimSets(tenant)` must not run before DMS knows the tenant exists.
A tenant-independent pre-authorization mechanism would be a new design, not part of DMS-1413.

## Tenant and Route-Qualifier Boundary

`TenantValidationMiddleware` remains responsible for tenant presence and syntax.
Add a Core identity-only `ValidateTenantExistsMiddleware` that uses the same cache-then-reload behavior as the frontend `TenantValidator`.
When multitenancy is enabled and the tenant does not exist, it returns `404` without calling the claim-set provider or identity provider.
When multitenancy is disabled, it is a pass-through.

`ResolveDataStoreMiddleware` stays omitted.
Identity does not select a datastore and does not require `ClientAuthorizations.DataStoreIds`.
An identity-only client with no authorized datastore can call identity endpoints if its claim set authorizes the identity service claim.

Configured route qualifiers are still extracted by the fixed route pattern and passed to the provider as `IdentityRequestContext.RouteQualifiers`.
DMS does not match them against authorized datastore instances.
The provider owns contextual refusal for an unknown tenant/qualifier combination and may return `NotFound`.

## Pipeline

Identity is a fixed-service pipeline.
It does not use ApiSchema resolution, resource mapping, backend mapping, profile resolution, fingerprint validation, or datastore resolution.

JSON-body operations (`POST identities`, `POST identities/find`, `POST identities/search`) use:

1. `RequestResponseLoggingMiddleware`
2. `CoreExceptionLoggingMiddleware`
3. `TenantValidationMiddleware`
4. `JwtAuthenticationMiddleware`
5. `ValidateTenantExistsMiddleware`
6. `ServiceClaimAuthorizationMiddleware`
7. `IdentityOperationCapabilityMiddleware`
8. `ValidateContentTypeMiddleware`, parameterized to baseline JSON only
9. `ParseBodyMiddleware`
10. `DuplicatePropertiesMiddleware`
11. `IdentityHandler`

Body-less operations (`GET identities/{id}`, `GET identities/results/{id}`) use steps 1 through 7 and then `IdentityHandler`.
They do not perform content-type or body parsing.

`IdentityOperationCapabilityMiddleware` maps the route to the provider capability required for that operation and returns operation-unsupported `404` before any POST body validation when the capability is absent.
This preserves the "enabled with no plugin" behavior: all five operations return not-implemented semantics even for malformed POST bodies.

`IdentityHandler` is responsible for:

- rejecting blank route values that are present but empty;
- validating request body shape;
- building `IdentityRequestContext`;
- calling the provider inside the provider-only exception boundary;
- mapping result status to HTTP;
- enforcing required-value invariants and request-token usability.

The four inbound protocol checks are:

1. media type;
2. well-formed JSON;
3. no duplicate property name anywhere in the body;
4. expected JSON shape: object for create, array of strings for find, and array of objects for search.

The two outbound protocol checks are:

1. required values are present for the status returned;
2. an async request token can be carried in one URL path segment.

## Async Token Rule

Find and search may return asynchronous `Success` with a request token instead of a payload.
Results polling uses `GET /identity/v2/identities/results/{token}`.

A usable request token is:

- not null;
- not blank or whitespace;
- contains no `/`;
- contains no `\`;
- contains no control character;
- is not exactly `.`;
- is not exactly `..`;
- compares ordinally equal to `Uri.UnescapeDataString(Uri.EscapeDataString(token))`.

An unusable token is provider contract misuse.
DMS returns `502` with no `Location` header.

DMS composes the `202 Location` by applying `Uri.EscapeDataString(token)` and appending the escaped value as the final route segment.
On the return leg, DMS passes the framework-decoded route value to `ResultsAsync` character-for-character and performs no second unescape.
This pins tokens such as `50%25`, which should arrive at the provider as `50%25`, not `50%`.

The exact dot segments `.` and `..` are excluded even though they escape to themselves and round-trip through `Uri`.
As path segments, they are relative-path navigation tokens rather than opaque data, so the poll URL is not guaranteed to return as issued through clients and proxies.
Longer dotted values such as `...`, `a.b`, and `.hidden` are ordinary data and remain valid if they satisfy the other rules.

## Error and Response Mapping

| Case | Provider called | DMS response |
| --- | --- | --- |
| Feature disabled | no | route `404` through fallback |
| Missing/invalid token | no | `401` |
| Invalid tenant syntax | no | `400` |
| Nonexistent tenant with valid token | no | `404` before claim-set lookup |
| No service claim or wrong action | no | `403` |
| Unsupported content type on POST | no | `415` |
| Malformed JSON or empty body | no | `400` |
| Duplicate property name | no | `400` data-validation problem |
| Wrong top-level body shape | no | `400` |
| Capability absent | no | `404` with `urn:ed-fi:api:identities:operation-not-supported` |
| Route value present but blank | no | `400` |
| Create `Success` | yes | `200`, body is unique-id JSON string |
| GetById `Success` | yes | `200`, body is provider payload |
| Find/Search synchronous `Success` | yes | `200`, body is provider payload |
| Find/Search async `Success` | yes | `202`, no body, `Location` points to results route |
| `Incomplete` from any operation except results | yes | `502` with `urn:ed-fi:api:identities:provider-contract-violation` |
| Results `Success` | yes | `200`, body is provider payload |
| Results `Incomplete` | yes | `200`, body is provider payload, `Location` points to current poll URL |
| Any operation `InvalidProperties` | yes | `400`, provider errors projected, payload ignored |
| Provider `NotFound` for get-by-id subject miss, results token miss, or provider-owned context refusal | yes | `404` with `urn:ed-fi:api:identities:not-found` |
| `Success` or `Incomplete` missing required payload | yes | `502` with `urn:ed-fi:api:identities:provider-contract-violation` |
| Find/Search `Success` with both payload and token, or neither | yes | `502` with `urn:ed-fi:api:identities:provider-contract-violation` |
| Find/Search `Success` with unusable token | yes | `502` with `urn:ed-fi:api:identities:provider-contract-violation`, no `Location` |
| `RequestToken` returned while `Results` capability is absent | yes | `502` with `urn:ed-fi:api:identities:provider-contract-violation` |
| Provider throws while request is live | yes | `502` with `urn:ed-fi:api:identities:upstream-failure`, exception logged and not returned |
| Provider throws `OperationCanceledException` after request cancellation | yes | rethrow so the host abandons the aborted response |

The unsupported-operation `404`, tenant-not-found `404`, identity-not-found `404`, and feature-off `404` must be distinguishable by route presence or problem-detail `type` in tests.
Provider-contract-violation `502` and identity-upstream-failure `502` must also be distinguishable by problem-detail `type`.
The problem-detail namespace is `urn:ed-fi:api:identities:*` rather than `urn:ed-fi:api:identity:*` because DMS already uses `urn:ed-fi:api:identity-conflict` for a document's natural-key identity, an unrelated concept, and `identities` matches this API's route and name.
The upstream-failure problem title identifies Identity Management as the failing subsystem and omits provider exception details.
`IdentityError` values are not returned for upstream failures; provider details remain in structured logs.

## Plugin Architecture

The identity backend is a DMS-1462 replace-cardinality plugin contract.
DMS registers a host default `NoIdentityService` implementing `IIdentityService` with `Capabilities = None`.
That host default is enough for DMS to boot with the feature enabled and no plugin loaded.

The plugin-registry story declares `IIdentityService` in `DmsPluginContracts.Registry`.
The registry entry is replace-cardinality:

- host default plus no plugin is valid;
- host default plus one plugin replacement is valid;
- two plugin replacements are fatal and the startup error names both plugins.

Plugins must register the identity service with `Add`, not `TryAdd`.
The DMS-1462 recording wrapper cannot observe a candidate descriptor that `TryAdd` declined.
This is documented as an implementer obligation.

No story before the registry/loading prerequisites may claim deploy-time plugin replacement.
The DMS-owned API story can use the host default and test doubles.

## Story Breakdown

| # | Story | Depends on | Scope |
| --- | --- | --- | --- |
| 01 | Add the Identity Contract Package and Host Default | this design | `EdFi.Api.Identity`, public contract types, XML docs, `NoIdentityService`, solution entry, lock file, Dockerfile copy-list entries |
| 02 | Add the Identity API Surface, Pipeline, Toggle, OpenAPI, and Discovery | 01 | Core identity pipeline, service-claim auth, tenant-existence middleware, request/response mapping, frontend endpoint module, `EnableIdentityManagement`, compose/env entries, fixed OpenAPI document, metadata listing, Discovery `identity` URL, toggle gating |
| 03 | Register the Identity Plugin Contract and Prove a Fixture Plugin | 02, DMS-1498, DMS-1499 | `Replace` registry entry, assembly-name derivation, runtime image assembly-version assertion, replacement cardinality tests, fixture plugin, sync and async flows, custom property pass-through, enabled-without-plugin, duplicate/body/token/error cases |
| 04 | Document and Publish `EdFi.Api.Identity` | 03, DMS-1500, DMS-1501 | operator toggle docs, plugin implementer chapter, contract readme, divergence ledger, publish-when-absent, skip-when-unchanged, fail-when-changed package lane |

## Test Strategy

Unit tests:

- capability matrix for all five operations and unsupported-operation `404`;
- identity-not-found `404` uses `urn:ed-fi:api:identities:not-found`, distinct from operation-unsupported, tenant-not-found, and feature-off `404`;
- find/search no-match returns `200` with an empty `Responses` array in the corresponding response group;
- operation-to-action authorization, capability checks, and ordering;
- tenant syntax, tenant existence, and claim-set ordering with a tenant-keyed or failing `IClaimSetProvider`;
- valid token plus nonexistent tenant returns tenant `404` without calling claim-set provider or identity provider;
- valid token plus existing tenant and missing identity claim returns `403`;
- no token plus nonexistent tenant returns `401`;
- enabled with no plugin returns operation-unsupported `404` before POST content-type or body validation;
- client with no authorized datastore still reaches identity provider;
- route qualifiers pass through in `IdentityRequestContext`;
- content-type acceptance and rejection, including Ed-Fi profile media types rejected for identity POSTs;
- malformed, empty, duplicate-bearing, and wrong-top-level-shape bodies;
- provider exception, upstream-failure problem type, and cancellation behavior;
- token round trip, including `50%25`, reserved characters that survive the round trip, `/`, `\`, control characters, `.`, and `..`;
- ordinary dotted tokens `...`, `a.b`, and `.hidden` accepted;
- result invariants such as missing payload, both payload and token, neither payload nor token, and token without results capability;
- `Incomplete` from any operation except results is provider contract misuse;
- a pending results poll whose payload carries wire `Status` incomplete and no result data returns `200` verbatim, not `502`;
- response-payload non-validation, proving a wrong-shaped success payload is served verbatim;
- request and response schemas for standard identifying attributes, unsupported-as-null semantics, ordered search-response groups, `BirthDate` as `date-time`, and `Score` as `number`/`double`;
- OpenAPI response metadata for provider `InvalidProperties` `400`, unsupported-media-type `415`, operation-unsupported `404`, identity-not-found `404`, provider-contract-violation `502`, and identity-upstream-failure `502`;
- identity contract package version and assembly version remain independent of the DMS release version.

Integration tests:

- disabled routes and disabled metadata absent;
- enabled without plugin starts and returns operation-unsupported `404`;
- real HTTP routing for `identities/results` and `identities/results/{token}`;
- async `Location` followed by polling returns the original token to provider;
- served OpenAPI includes route-qualified `servers` and `oauth2_client_credentials` security metadata;
- content-type/body/duplicate/token/error cases through the real frontend pipeline;
- multitenant route context with tenant existence and route qualifiers;
- provider-contract-violation and identity-upstream-failure `502` cases have distinct problem-detail types.

E2E tests:

- fixture plugin loaded through the plugin infrastructure once DMS-1499 exists;
- full create, get-by-id, find, search, and results flows;
- custom properties pass through both directions;
- standard identifying attributes and search confidence scores appear in fixture success responses;
- two replacing plugins abort startup;
- enabled-without-plugin deployment starts cleanly and returns operation-unsupported `404`;
- Discovery and metadata list include identity only when enabled.

No tests are run by this spike because it changes design documents only.

## Divergence Ledger

| # | Subject | ODS/API behavior | DMS behavior | Reason |
| --- | --- | --- | --- | --- |
| D-1 | Capability miss | `501` on all operations | `404` with identity-specific problem type | Jira requires unsupported operations to return `404` |
| D-2 | Create success | code returns `201` with `Location`; document declares `200` | `200`, unique-id string body, no `Location` | Jira and published document agree on `200` |
| D-3 | GetById score | filters on `Score == 100` | no score inspection | matching/scoring is plugin-owned |
| D-4 | `502` body | may return exception object | problem+json without exception detail | avoid leaking implementation details |
| D-5 | Error body shape | ODS-specific error response shapes | DMS problem+json | one host failure shape |
| D-6 | Authorization granularity | claim presence only | `Create` for create, `Read` for the rest | avoid granting writes from read-only claim |
| D-7 | Feature disabled | mapped route can answer `403` | route and metadata absent | Jira requires API/OpenAPI absence |
| D-8 | Results capability | composite bit in ODS | independent `Results` bit | async polling should be explicit |
| D-9 | Async surface | separate sync/async interfaces | one interface with operation-specific result types | keeps invalid token states unrepresentable |
| D-10 | `202 Location` OpenAPI | described but not declared | declared | document the polling contract |
| D-11 | Unsupported media type | MVC binding behavior | explicit `415` | host-owned protocol concern |
| D-12 | `InvalidProperties` | `400` only on create | `400` on all operations | Jira names it as a standard delegated status |
| D-13 | Success missing value | can become empty `200` | `502` contract misuse | client cannot use an empty success |
| D-14 | OpenAPI response required lists | identity response schemas declare no `required` arrays | `IdentityResponse` declares the standard identifying attributes, `UniqueId`, and `Score` required, `IdentitySearchResponse` declares `Status` required and `SearchResponses` required only in the `Complete` shape, and each `SearchResponses` entry declares `Responses` required | providers need a documented success payload contract; the `Complete`-only scoping of `SearchResponses` keeps a pending poll legal |
| D-15 | OpenAPI nullability | identity schemas declare no `nullable` keywords, so the document forbids `null` by OpenAPI 3.0 default | standard identifying attributes are declared `nullable` in both request and response schemas, including `BirthLocation` children, and `Score` is declared `nullable` on response schemas | requests use `null` or omission for unknown values and providers represent unsupported response attributes as `null`, which the pinned document's own omission would make illegal; `Score` is nullable because an identity returned outside a search carries no confidence value, while search matches must still supply a number from 0 through 100 |

## Risks and Open Questions

Risks:

- The full plugin path cannot be proven until DMS-1499 exists.
- `404` has several meanings: feature off, tenant not found, unsupported operation, and subject not found. Tests must distinguish by route presence or problem-detail type.
- The create response differs from running ODS code by omitting `201 Location`.
- A published contract is permanent and additive-only.
- Feature-toggle rot is a real risk; absence tests must cover routes, metadata, listing, and Discovery.
- `IApiService` grows five methods. Interface fakes absorb that automatically, but the `ApiService` constructor gains a dependency and 13 test sites construct that class directly.
- Plugins run fully trusted in process.
- Capabilities are deployment-wide in v1.
- Parameterizing content-type validation touches existing resource write pipelines.
- Payload shape is documented but not enforced at runtime.
- The Dockerfile explicit copy lists are easy to miss for a new project.
- Tenant existence will have two implementations until a follow-up extracts a shared helper.
- Route qualifiers are provider context for identity, so provider documentation must explain its own qualifier refusal behavior.
- The tenant-existence check runs before service-claim authorization for authenticated callers because the claim-set provider is tenant-keyed.

Open questions with recommended defaults:

- Product sign-off on ODS divergences before publishing the contract: proceed with the ledger visible.
- Per-tenant capabilities in v1: proceed deployment-wide.
- Token introspection advertising identity availability: proceed unchanged and use Discovery as the signal.
- Missing `Content-Type` on POST: accept, matching existing DMS write behavior.
- Malformed and duplicate-body problem shapes: keep existing DMS middleware shapes.
- Blank `{id}` segment: reject `400` when present but blank; let routing produce `404` when absent.
- Runtime response-payload validation: do not add it in v1.
- Tokens containing path separators: keep rejected unless a future story measures deployment behavior and changes the contract.

## Follow-Up Items Outside Identity Scope

- `AppSettings:EnableManagementEndpoints` appears to be configured but not read by production code.
- `JwtRoleAuthenticationMiddleware` is registered but not composed into a pipeline.
- Existing fixed-service facade paths pass no request cancellation token.
- Tenant existence should eventually be unified behind one Core-side helper used by both frontend fixed routes and the identity Core middleware.
- The Configuration Service claim-set provider is a singleton that mutates one shared `HttpClient`'s `Tenant` and `Authorization` default headers per call, while the claim-set cache's stampede lock is keyed per tenant. Concurrent first-time misses for different tenants can therefore race on those headers. Identity service-claim authorization adds another caller to that path.
