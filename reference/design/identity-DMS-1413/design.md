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
7. Confine each request to the tenant in its own URL, without reintroducing datastore authorization.
8. Break the work into stories that respect the current plugin-foundation dependency graph.

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
- `AspNetCoreFrontend.ExtractJsonBodyFrom` already parses request bodies and records duplicate-property paths before `JsonNode` collapses duplicates, and it does so before Core is entered; `ParseBodyMiddleware` and `DuplicatePropertiesMiddleware` report what it already computed rather than performing the parse themselves.
- `JwtValidationService.ValidateAndExtractClientAuthorizationsAsync` takes no tenant, and `JwtAuthenticationOptions` declares a single `Authority` and `Audience`, so one issuer serves every tenant and a token carries no tenant binding.
- `ResolveDataStoreMiddleware` resolves each token-supplied `DataStoreIds` entry through the tenant-scoped `IDataStoreProvider.GetById(id, tenant)`, which is what incidentally prevents cross-tenant use of a token on resource routes. Identity omits this middleware, so it must bind the client to the tenant explicitly.
- `ConfigurationServiceApplicationProvider.GetApplicationByClientIdAsync` sends the tenant as a per-request header and returns `NotFound` for a client absent from that tenant and `Unavailable` for a Configuration Service failure; `ApplicationContextRequirementMiddleware` maps those to `401` and `503`.
- `ResourceActionAuthorizationMiddleware` validates the matched action's authorization-strategy list and fails closed on an empty one, so action membership alone is not the authorization decision elsewhere in DMS.
- `IApiService` is registered as a singleton, and existing pipeline steps resolve per-request dependencies through `RequestInfo.ScopedServiceProvider` rather than through the `ApiService` constructor.
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
    public required string ClientId { get; init; }
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

`ClientId` is the authenticated client's `client_id`, taken from `ClientAuthorizations.ClientId`.
It is required rather than nullable because every identity operation is authenticated, so there is no request shape in which it is absent.
It is stable across token refresh, unlike the token's `jti`, which makes it usable as a job-ownership key.
`TraceId` is a per-request correlation value and is not an ownership key, a cache key, or an idempotency key.

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

### UniqueId Issuance Constraints

DMS does not validate UniqueIds on person-resource writes; that is DMS-1414's scope.
This design still has to state what makes an issued id usable in those writes later, because the contract is published and additive-only, and a constraint omitted now cannot be added afterwards without breaking implementers.

The constraint that is not obvious from the wire type is equality.
An issued UniqueId becomes part of a person resource's natural key, and the backend redesign gives the two providers different string-identity equality: SQL Server applies DMS's case-insensitive identity collation to every column storing an identity value, with `OrdinalIgnoreCase` runtime comparers, while PostgreSQL stays case-sensitive with `Ordinal` comparers.
A provider that treats `ABC` and `abc` as two identities would therefore create two identities that collapse onto one person natural key on SQL Server and remain two on PostgreSQL.

A provider must issue UniqueIds that are:

- unique within the deployment across every tenant and route-qualifier context it serves, since DMS applies no tenant prefix and does not rewrite the value;
- distinct under case-insensitive comparison, not merely under exact comparison, so the value means the same identity on both providers;
- non-empty and free of leading or trailing whitespace, which DMS does not trim;
- a single URL path segment, because get-by-id carries the value in the route;
- stable for the life of the identity, because person documents already written reference the value as natural-key data.

These remain provider responsibilities.
DMS neither generates nor rewrites the value, and the identity pipeline adds no ApiSchema or relational dependency to enforce them.
The end-to-end proof that an issued id survives a person-resource write on each backend belongs to DMS-1414, which owns that validation path; this design contributes the constraint that story asserts against.

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

The shipped claim also declares an `Update` action.
No identity operation maps to `Update`, and the middleware never evaluates it.

Authorization runs before the capability gate, media-type gate, the reporting of body parse and duplicate-property errors, body-shape validation, and provider call.
Parsing itself is not ordered by this pipeline: `AspNetCoreFrontend.ExtractJsonBodyFrom` buffers the body, parses it, and records any duplicate-property path before Core is entered, and `ParseBodyMiddleware` only reports the result the frontend already computed.
Ordering the reporting is what prevents unsupported deployments from revealing whether a request would otherwise have failed as malformed or unsupported.
Moving parsing itself after authorization would require changing frontend extraction, which this design does not do.

### Authorization Strategies

The shipped identity claim declares `NoFurtherAuthorizationRequired` on every action, and identity has no relational authorization context to evaluate any other strategy against.
CMS nonetheless permits per-claim-set `authorizationStrategyOverrides` on this claim, so the middleware must state what it does with one.

The v1 policy is:

- an action whose effective strategy list is exactly `NoFurtherAuthorizationRequired` is authorized;
- an empty strategy list, an unrecognized strategy name, and any recognized strategy other than `NoFurtherAuthorizationRequired` all fail closed as invalid security configuration, not as ordinary permission denial.

This follows `ResourceActionAuthorizationMiddleware`, which validates the strategy list rather than treating action membership as the whole decision, and the backend redesign's separation of permission denial from invalid security configuration.
Failing closed is what keeps a restriction configured in CMS from being silently ignored while DMS appears to honor the claim-set graph.

### Client-to-Tenant Binding

Verifying that the URL tenant exists does not establish that the authenticated client belongs to it.
JWT validation uses one configured issuer and audience for every tenant and takes no tenant argument, so a token minted for tenant A is cryptographically valid on tenant B's route.
Claim sets resolve by name, and claim-set names are not unique across tenants, so if B has an identically named claim set granting identity access then tenant existence plus service-claim evaluation authorizes the request.

For resource requests the binding is incidental rather than absent: `ResolveDataStoreMiddleware` resolves each `ClientAuthorizations.DataStoreIds` entry through a tenant-scoped `IDataStoreProvider.GetById(id, tenant)` lookup, so a cross-tenant token fails to resolve a datastore.
Identity omits that middleware by design, which removes the only check currently binding a client to the URL tenant.
Identity must therefore add the binding explicitly rather than inherit it.

The check is an application-context lookup for the authenticated client in the URL tenant, using `IApplicationContextProvider.GetApplicationByClientIdAsync(clientId, tenant)`.
That provider already sends the tenant as a per-request header and already distinguishes a client absent from the tenant from a Configuration Service outage, so it supplies both the binding and the typed outcomes the tenant-existence check needs.

The lookup is client-scoped, not datastore-scoped.
It resolves the API client record itself, and `ApplicationContext.DataStoreIds` may legitimately be empty.
Identity reads nothing from the resolved context except the fact that it resolved, so an identity-only client with no authorized datastore still passes.
The middleware must not reject an empty `DataStoreIds`; doing so would reintroduce datastore authorization through the back door and contradict the boundary below.

Outcome mapping follows the existing `ApplicationContextRequirementMiddleware`: a client not resolvable in the tenant is `401`, and an unavailable Configuration Service is `503`.
Reusing that provider also means identity inherits its request-scoped memoization and cache, so the binding check does not add a Configuration Service round trip per identity request in steady state.

The sequence is:

1. tenant syntax validation;
2. JWT authentication;
3. tenant existence validation;
4. client-to-tenant binding;
5. service-claim authorization.

That ordering is deliberate.
Unauthenticated callers still receive `401` and do not reach tenant existence.
Authenticated callers with a valid token can receive tenant `404` before service-claim `403`; that is accepted because any middleware that calls `IClaimSetProvider.GetAllClaimSets(tenant)` must not run before DMS knows the tenant exists.
A tenant-independent pre-authorization mechanism would be a new design, not part of DMS-1413.

Because a tenant `404` is observable to any authenticated client of any tenant, the tenant-existence response discloses tenant existence.
That is inherited from the existing frontend fixed-route behavior and is not changed here; the binding check above is what prevents the disclosure from becoming cross-tenant access.

Service-claim authorization depends on `IClaimSetProvider.GetAllClaimSets(tenant)`, whose CMS-backed implementation is not tenant-safe under concurrent cold cache misses.
That defect is pre-existing and is not identity's to carry, but identity adds a caller to the affected path, so its correction is a named prerequisite rather than an open follow-up.
See [Prerequisites](#prerequisites).

## Tenant and Route-Qualifier Boundary

`TenantValidationMiddleware` remains responsible for tenant presence and syntax.
Add a Core identity-only `ValidateTenantExistsMiddleware` that uses the same cache-then-reload behavior as the frontend `TenantValidator`.
When multitenancy is enabled and the tenant does not exist, it returns `404` without calling the claim-set provider or identity provider.
When multitenancy is disabled, it is a pass-through.

It reuses the frontend validator's caching behavior but not its result type.
`TenantValidator.ValidateTenantAsync` returns a bare `bool` and reaches it through a general exception catch, and its datastore provider wraps transport failures in `InvalidOperationException`, so a Configuration Service outage on a cache miss is indistinguishable from a nonexistent tenant and a cancellation is swallowed on the same path.
Copying that shape would collapse an upstream failure into a client-facing tenant `404` and would undo this design's distinction between client mistakes and upstream failures.

The identity check therefore returns three typed outcomes:

| Outcome | Meaning | DMS response |
| --- | --- | --- |
| exists | the tenant is confirmed present | continue the pipeline |
| absent | Configuration Service confirmed the tenant does not exist | tenant `404` |
| unavailable | the existence question could not be answered | `503` service-unavailable problem |

Only confirmed absence becomes a `404`.
`OperationCanceledException` for the request's own cancellation token propagates and is never converted into any of the three outcomes.
`ApplicationContextResult` is the existing precedent for this shape, and the client-to-tenant binding check uses the same provider, so both checks report absence and unavailability through one mechanism.

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
6. `ValidateClientTenantBindingMiddleware`
7. `ServiceClaimAuthorizationMiddleware`
8. `IdentityOperationCapabilityMiddleware`
9. `ValidateContentTypeMiddleware`, parameterized to baseline JSON only
10. `ParseBodyMiddleware`
11. `DuplicatePropertiesMiddleware`
12. `IdentityHandler`

Body-less operations (`GET identities/{id}`, `GET identities/results/{id}`) use steps 1 through 8 and then `IdentityHandler`.
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

## Provider Lifetime and Resolution

The plugin architecture permits a contributed registration to use any lifetime, including scoped, so the identity contract must state how the host resolves the provider rather than leaving it to the implementer.

`IApiService` is registered as a singleton and caches its pipelines, so `ApiService` must not take `IIdentityService` as a constructor dependency.
Constructor-injecting a scoped registration into a singleton either fails scope validation or captures one request's instance for the process lifetime, and capturing a transient has the same effect.
Because ASP.NET Core enables scope validation only in the Development environment by default, a captured-scoped provider would not fail startup in a released deployment; it would silently share one tenant's instance across every request.
A startup guard is therefore not a substitute for specifying the boundary.

The rules are:

- the provider is resolved from the Core per-request scope through `RequestInfo.ScopedServiceProvider`, following `UpsertHandler`, `ApplicationContextRequirementMiddleware`, and `ResolveDataStoreMiddleware`;
- it is resolved once per request and the same instance serves the capability check and the invocation, so a provider cannot observe a capability set that differs from the one its call was gated on;
- all three lifetimes are supported, and an implementer may choose whichever its integration needs;
- DMS never disposes the instance itself; a scoped or transient registration is disposed with the request scope and a singleton lives for the process;
- a provider must be safe for concurrent calls across requests, because a singleton or transient registration gives DMS no per-request isolation. A scoped registration is called at most once per request but its captured dependencies may still be shared.

Tests must include a provider whose registration is scoped and whose own dependency is scoped.
A singleton test fixture cannot detect a captured-scope defect.

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

### Async Job Obligations

The token rules above establish only that a token survives a URL round trip.
They establish nothing about ownership, context binding, retention, or durability, and a provider could satisfy them with a process-global token-to-result dictionary.
That would let a token submitted under one tenant and qualifier set be redeemed under another, and would make a poll return `404` after a restart or when it reaches a second replica.
DMS holds no job state and cannot enforce any of this, so these are provider obligations that the contract documentation must state and the implementer guide must repeat.

A provider must:

- bind each accepted job to the `Tenant`, `RouteQualifiers`, and `ClientId` of the request that created it, and treat a poll whose context does not match as `NotFound` rather than returning the job;
- keep results retrievable for a documented retention period, and answer a poll after expiry as `NotFound`;
- answer repeated polls of an unexpired complete job with the same result, because polling is a `GET` and clients may retry it;
- represent a job that failed terminally as its own answer rather than as an indefinite `Incomplete`, either by returning the failure through `InvalidProperties` or by throwing so DMS reports identity-upstream-failure `502`;
- make results retrievable from any replica that serves the same deployment, or document that the integration is single-replica only;
- document whether accepted jobs survive a provider restart.

Results are scoped to the issuing client, not shared across the tenant.
This is the v1 decision because it can be loosened later without breaking a client, whereas a shared default could not be tightened later without breaking one.
A provider whose upstream system genuinely shares results within a tenant must still gate the poll on `ClientId`, or document the sharing as a deliberate deviation.

Request cancellation does not cancel an accepted job.
Once find or search has returned an async `Success` with a token, DMS has already answered `202` and the client's connection is irrelevant to the job.
Cancellation propagation applies to the provider call in flight, not to work the provider accepted on a call that already returned.

### Timeout, Retry, and Idempotency

The identity pipeline wraps no resilience pipeline, so DMS never retries a provider call and imposes no timeout of its own on one.
A provider owns its own upstream timeouts, and a timeout it does not handle surfaces as a thrown exception and therefore as identity-upstream-failure `502`.

The consequence for create is that a lost response is a client-visible hazard the host cannot close.
If the upstream system issues a UniqueId and the response to DMS is lost, the client sees `502` and a retry may issue a second id for the same person.
`TraceId` is per-request and is not an idempotency key, so it cannot be used to deduplicate the retry.

The contract does not add an idempotency key in v1.
Instead, a provider must document how its integration behaves on a repeated create for the same identifying data: either that creation is idempotent on some upstream key, or that duplicate issuance is possible and how the duplicates are reconciled.
An implementer whose upstream system offers no such guarantee must say so, because a client cannot otherwise know whether retrying a failed create is safe.

## Error and Response Mapping

| Case | Provider called | DMS response |
| --- | --- | --- |
| Feature disabled | no | route `404` through fallback |
| Missing/invalid token | no | `401` |
| Invalid tenant syntax | no | `400` |
| Nonexistent tenant with valid token | no | `404` before claim-set lookup |
| Tenant existence unanswerable because Configuration Service is unavailable | no | `503` service-unavailable problem, not tenant `404` |
| Valid token whose client does not belong to the URL tenant | no | `401` before claim-set lookup |
| Client-to-tenant binding unanswerable because Configuration Service is unavailable | no | `503` service-unavailable problem |
| No service claim or wrong action | no | `403` |
| Matched identity action with an empty, unknown, or non-`NoFurtherAuthorizationRequired` strategy list | no | `500` security-configuration problem, not `403` |
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

## Prerequisites

One pre-existing defect must be corrected before the identity API surface story lands, because that story adds a caller to the affected path and the defect is a cross-tenant authorization failure.

**The CMS-backed claim-set provider is not tenant-safe under concurrent cold cache misses.**
`ConfigurationServiceClaimSetProvider` mutates the shared `HttpClient`'s `Tenant` and `Authorization` default request headers per call, while `CachedClaimSetProvider`'s stampede lock is keyed per tenant, so two cold misses for different tenants are mutually unsynchronized.
One tenant's authorization metadata can therefore be fetched and cached under another tenant's key.
The window is not the startup path: `CacheClaimSetsTask` warms each tenant sequentially at boot, so concurrent cold misses occur after cache expiry or an explicit invalidation, which recurs for the life of the process.

The correction is to send the tenant on the individual `HttpRequestMessage` rather than mutating shared default headers, and to add a controlled concurrent-miss test over two tenants.
This is a one-file change onto an established pattern: `ConfigurationServiceApplicationProvider`, `ConfigurationServiceDataStoreProvider`, and `ConfigurationServiceProfileProvider` already use per-request headers, the last of them with an explicit comment saying why, and the backend redesign already states the rule for application-context retrieval.
The claim-set provider is the remaining outlier.

It is filed as its own story rather than folded into the identity API surface story, because it is a security defect on the existing resource authorization path and belongs where it can be reviewed and released as one.
The identity API surface story declares it as a dependency.

## Story Breakdown

| # | Story | Depends on | Scope |
| --- | --- | --- | --- |
| 01 | Add the Identity Contract Package and Host Default | this design | `EdFi.Api.Identity`, public contract types, XML docs, `NoIdentityService`, solution entry, lock file, Dockerfile copy-list entries |
| 00 | Send the Tenant Header Per Request in the CMS Claim-Set Provider | this design | per-request `Tenant` and `Authorization` headers in `ConfigurationServiceClaimSetProvider`, concurrent two-tenant cold-miss test. See [Prerequisites](#prerequisites) |
| 02 | Add the Identity API Surface, Pipeline, Toggle, OpenAPI, and Discovery | 01, 00 | Core identity pipeline, service-claim auth, tenant-existence middleware, client-to-tenant binding, authorization-strategy policy, provider resolution from the request scope, request/response mapping, frontend endpoint module, `EnableIdentityManagement`, compose/env entries, fixed OpenAPI document, metadata listing, Discovery `identity` URL, toggle gating |
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
- two existing tenants with identically named claim sets both granting identity access: a token issued for tenant A cannot reach tenant B's identity endpoints, and the identity provider is not called;
- the client-to-tenant binding runs before service-claim authorization and its unavailable outcome returns `503` rather than `401`;
- tenant existence returns `503` and not `404` when the existence question cannot be answered, and propagates request cancellation instead of converting it to an outcome;
- a matched identity action whose strategy list is empty, unknown, or a recognized strategy other than `NoFurtherAuthorizationRequired` fails closed as security configuration rather than `403`;
- a provider registered as scoped, with its own scoped dependency, is resolved once per request from the request scope and the capability check and invocation observe the same instance;
- enabled with no plugin returns operation-unsupported `404` before POST content-type or body validation;
- client with no authorized datastore still reaches identity provider, including through the client-to-tenant binding check with an empty `ApplicationContext.DataStoreIds`;
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
- provider-contract-violation and identity-upstream-failure `502` cases have distinct problem-detail types;
- a two-tenant deployment with identically named claim sets rejects tenant A's token on tenant B's identity routes through the real pipeline;
- a Configuration Service outage during tenant existence and during client-to-tenant binding is classified as `503`, distinct from tenant `404`.

E2E tests:

- fixture plugin loaded through the plugin infrastructure once DMS-1499 exists;
- full create, get-by-id, find, search, and results flows;
- custom properties pass through both directions;
- standard identifying attributes and search confidence scores appear in fixture success responses;
- two replacing plugins abort startup;
- enabled-without-plugin deployment starts cleanly and returns operation-unsupported `404`;
- Discovery and metadata list include identity only when enabled;
- a results token issued under one tenant, route-qualifier set, or client cannot be redeemed under another, proving the fixture plugin binds jobs to their issuing context;
- a fixture-plugin async job remains pollable after the DMS container restarts, or the fixture documents itself as in-memory only and the test asserts the documented `404`.

No tests are run by this spike because it changes design documents only.

## Divergence Ledger

| # | Subject | ODS/API behavior | DMS behavior | Reason |
| --- | --- | --- | --- | --- |
| D-1 | Capability miss | `501` on all operations | `404` with identity-specific problem type | Jira requires unsupported operations to return `404` |
| D-2 | Create success | code returns `201` with `Location`; document declares `200` | `200`, unique-id string body, no `Location` | Jira and published document agree on `200` |
| D-3 | GetById score | filters on `Score == 100` | no score inspection | matching/scoring is plugin-owned |
| D-4 | `502` body | may return exception object | problem+json without exception detail | avoid leaking implementation details |
| D-5 | Error body shape | ODS-specific error response shapes | DMS problem+json | one host failure shape |
| D-6 | Authorization granularity | claim presence only | `Create` for create, `Read` for the rest, and the matched action's strategy list must be exactly `NoFurtherAuthorizationRequired` or the request fails closed as invalid security configuration | avoid granting writes from a read-only claim, and avoid silently ignoring a strategy an operator configured in CMS on a surface that has no relational authorization context to evaluate it |
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
- Tenant existence will have two implementations until a follow-up extracts a shared helper, and the identity one returns typed outcomes while the frontend one returns a bare `bool`.
- Route qualifiers are provider context for identity, so provider documentation must explain its own qualifier refusal behavior.
- The tenant-existence check runs before service-claim authorization for authenticated callers because the claim-set provider is tenant-keyed.
- Identity omits `ResolveDataStoreMiddleware`, which is what incidentally binds a client to the URL tenant on resource routes, so the explicit binding check is the only thing preventing cross-tenant identity access.
- The binding check adds a Configuration Service dependency to every identity request, which the application-context cache mitigates but does not remove.
- The binding check must ignore `ApplicationContext.DataStoreIds`; an implementation that rejects an empty list would silently break identity-only clients, which is the one client shape this API is expected to serve.
- Async job ownership, retention, and durability are provider obligations DMS cannot enforce, so a non-conforming provider is a correctness risk the host cannot detect.
- A lost create response can cause duplicate UniqueId issuance, and v1 ships no idempotency key to prevent it.
- UniqueId case-equality differs between the two providers, so a provider issuing case-variant ids produces different person-resource outcomes on SQL Server than on PostgreSQL.
- A plugin may register the identity provider as scoped, so the provider must be resolved from the request scope rather than injected into the singleton `ApiService`; scope validation would not catch the mistake outside Development.

Open questions with recommended defaults:

- Product sign-off on ODS divergences before publishing the contract: proceed with the ledger visible.
- Per-tenant capabilities in v1: proceed deployment-wide.
- Token introspection advertising identity availability: proceed unchanged and use Discovery as the signal.
- Missing `Content-Type` on POST: accept, matching existing DMS write behavior.
- Malformed and duplicate-body problem shapes: keep existing DMS middleware shapes.
- Blank `{id}` segment: reject `400` when present but blank; let routing produce `404` when absent.
- Runtime response-payload validation: do not add it in v1.
- Tokens containing path separators: keep rejected unless a future story measures deployment behavior and changes the contract.
- Async results shared across a tenant versus scoped to the issuing client: scope to the issuing client, because loosening later is compatible and tightening later is not.
- An idempotency key on create: not in v1; require the provider to document its own repeated-create behavior instead.
- A published conformance test suite for implementers: not in this epic; the served OpenAPI document plus story 04's documented obligations are the v1 answer.

## Follow-Up Items Outside Identity Scope

- `AppSettings:EnableManagementEndpoints` appears to be configured but not read by production code.
- `JwtRoleAuthenticationMiddleware` is registered but not composed into a pipeline.
- Existing fixed-service facade paths pass no request cancellation token.
- Tenant existence should eventually be unified behind one Core-side helper used by both frontend fixed routes and the identity Core middleware. The frontend validator's bare-`bool` result and general exception catch should be replaced with the typed outcomes this design specifies, so a Configuration Service outage stops reading as a tenant `404` on the existing fixed routes too.
- JWT validation is not tenant-aware at all: one issuer and audience serve every tenant, and `ValidateAndExtractClientAuthorizationsAsync` takes no tenant. Identity closes the resulting gap with an application-context binding check, but a tenant-scoped token or issuer would close it at the source for every route.
- A published conformance test package for `IIdentityService` implementers was considered and deferred. Story 04's implementer chapter documents the payload obligations and the served OpenAPI document is the artifact an implementer validates against; a shipped conformance suite would be a new public artifact with its own version, publication lane, and additive-only policy, and belongs to its own story if it is wanted.

The claim-set provider's cross-tenant header race is no longer listed here.
It is now a named [prerequisite](#prerequisites) with its own story.
