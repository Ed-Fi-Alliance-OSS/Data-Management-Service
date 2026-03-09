# Backend Redesign: Abstract Endpoints and Link Injection

## Status

Draft.

This document is a design deep dive for adding abstract resource endpoints and link injection to GET responses, enabling consumers to disambiguate abstract references (e.g., `educationOrganizationReference`) without multi-endpoint lookups. It targets the planned relational backend.

- Overview: [overview.md](overview.md)
- Data model (abstract identity tables, union views): [data-model.md](data-model.md)
- Flattening & reconstitution: [flattening-reconstitution.md](flattening-reconstitution.md)
- Compiled mapping set: [compiled-mapping-set.md](compiled-mapping-set.md)
- Extensions: [extensions.md](extensions.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- Authorization: [auth.md](auth.md)
- Jira: [DMS-622](https://edfi.atlassian.net/browse/DMS-622)
- GitHub Discussion: [Ed-Fi-Technology-Roadmap #13](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Technology-Roadmap/discussions/13)

## Table of Contents

- [Goals and Non-Goals](#goals-and-non-goals)
- [Problem Statement](#problem-statement)
- [ODS/API Link Format (Reference)](#odsapi-link-format-reference)
- [Abstract Endpoint Design](#abstract-endpoint-design)
- [Link Injection Design](#link-injection-design)
- [Implementation Design](#implementation-design)
- [Extension Considerations](#extension-considerations)
- [Risks / Open Questions](#risks--open-questions)
- [Level of Effort](#level-of-effort)

---

## Goals and Non-Goals

### Goals

1. **Abstract type disambiguation**: Enable API consumers to resolve abstract references (e.g., `educationOrganizationReference`) to their concrete type and full resource body without multi-endpoint lookups.
2. **Zero-cost link injection**: Inject `link` objects into abstract reference elements in GET responses using only values already present at response time — no database lookups, no UUID resolution.
3. **Schema-driven, no codegen**: Derive abstract endpoint paths, identity fields, and reference locations from `ApiSchema.json` metadata at startup. No per-resource handwritten code.
4. **Cross-engine parity**: PostgreSQL and SQL Server implementations use the same query patterns with engine-specific syntax only where unavoidable (type casts, string functions).
5. **Consistent with existing architecture**: Follow established DMS patterns — `IPipelineStep` middleware, discriminated union results, `IEffectiveApiSchemaProvider` singletons, `IDocumentStoreRepository`-style backend interfaces.

### Non-Goals

- Link enrichment for concrete references (e.g., `schoolReference`) — the design focuses on abstract type disambiguation only.
- Links in streamed/CDC data — API-side concern only.
- Write operations (POST/PUT/DELETE) on abstract endpoints.
- Abstract endpoint discoverability in the base API discovery response.
- Profile filtering interaction with link injection — deferred until profiles affect reference identity fields.
- OpenSearch support — OpenSearch is deprecated in DMS.

---

## Problem Statement

When a GET response includes an abstract reference (e.g., `educationOrganizationReference`), consumers cannot determine the concrete type or navigate to the referenced resource without enumerating all possible concrete endpoints (`/ed-fi/schools`, `/ed-fi/localEducationAgencies`, etc.). Field users such as EDU depend on knowing the concrete type behind abstract EducationOrganization references — without it, disambiguation requires expensive multi-endpoint lookups across all nine concrete subclass endpoints.

The proposed solution has two parts:

1. **Read-only abstract endpoints** (e.g., `GET /ed-fi/educationOrganizations/{educationOrganizationId}`) that resolve a natural key to the full concrete resource with a `_type` discriminator.
2. **Link injection** into abstract reference elements in GET response bodies — an `href`-only `link` object pointing to the abstract endpoint, constructed from natural key values already present in the response with zero runtime cost.

---

## ODS/API Link Format (Reference)

ODS/API 7.x includes `link` objects within entity reference elements in GET responses. Each link contains two fields:

```json
"schoolReference": {
  "schoolId": 255901,
  "link": {
    "rel": "School",
    "href": "/ed-fi/schools/2af36358c7824afe8b3b88aea077c172"
  }
}
```

**`rel`** contains the concrete resource type name. For abstract references such as `educationOrganizationReference`, `rel` resolves to the actual concrete subclass name at response time (e.g., `"School"` or `"LocalEducationAgency"`), providing in-place type disambiguation without a follow-up request.

**`href`** contains a relative URL path in the form `/{namespace}/{resourceEndpoint}/{resourceId}`, where the resource ID is the ODS/API's internal identifier (a GUID).

**Scope in ODS/API**: Links appear in entity references only — descriptor references do not include `link` objects. Both abstract and concrete references include links.

**TAG deprecation status**: The Ed-Fi Technical Advisory Group deprecated `link` elements in the Ed-Fi API Guidelines, citing: (1) implementation burden for document stores that must enrich responses at read time, (2) low adoption by client applications, and (3) redundancy with natural key queries. Despite this, ODS/API 7.x continues to include links for backward compatibility. The deprecation discussion is tracked in [Ed-Fi-Technology-Roadmap #13](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Technology-Roadmap/discussions/13).

**DMS divergences from ODS/API**: The DMS link format drops the `rel` field (concrete type disambiguation is provided by the abstract endpoint's `_type` discriminator instead, avoiding the DB lookup that `rel` would require). Hrefs use natural key values instead of internal identifiers (the natural key is already present in the reference object — no UUID computation or DB lookup needed). Scope is narrowed to abstract references only, since abstract type disambiguation is the actual need from the GitHub discussion. These divergences are detailed in subsequent sections.

---

## Abstract Endpoint Design

Abstract endpoints are purely additive — they don't change existing GET response behavior and don't require a feature toggle. The feature is always available once deployed. Natural keys are already exposed in GET responses, so abstract endpoints don't create new information leakage; standard DMS authorization enforcement applies.

Implementation targets the planned relational backend (abstract identity tables and abstract union views from the backend redesign), not the current JSONB document-store architecture. Conceptual query patterns are described against the relational model.

### EducationOrganization

EducationOrganization is the primary abstract type and the one driving the disambiguation use case. The abstract endpoint exposes a single lookup mechanism: GET-by-id using the natural key value as a path parameter.

**Endpoint**: `GET /ed-fi/educationOrganizations/{educationOrganizationId}`

The path parameter `{educationOrganizationId}` is the integer natural key value (e.g., `255901`), not a UUID. Following Stephen Fuqua's proposal, abstract endpoint hrefs use the natural key value directly — no UUID computation or DB lookup needed.

This endpoint searches across all nine concrete subclasses — School, LocalEducationAgency, StateEducationAgency, EducationServiceCenter, CommunityOrganization, CommunityProvider, PostSecondaryInstitution, EducationOrganizationNetwork, and OrganizationDepartment — via the abstract identity table in the relational backend. Because `educationOrganizationId` is unique across all concrete subclasses (enforced by the abstract identity table), a lookup returns at most one resource.

The endpoint is **read-only** — no POST, PUT, or DELETE operations. Consumers continue to use existing concrete endpoints for write operations.

GET-by-id is the **sole lookup mechanism** for EducationOrganization. There is no general-purpose list or pagination endpoint. The abstract endpoint exists to resolve a specific natural key to its concrete resource, not to provide a browsable collection.

If no education organization with the given `educationOrganizationId` exists, the endpoint returns 404. Standard DMS error handling applies (404 for not found, 403 for unauthorized). Existing DMS authorization filters the result by the client's concrete-type grants — a client authorized only for Schools will receive 403 if the matched resource is a LocalEducationAgency.

### GeneralStudentProgramAssociation

GeneralStudentProgramAssociation (GSPA) is the second abstract type in the Ed-Fi model. Its identity is a 6-field composite key (`studentUniqueId`, `programName`, `programTypeDescriptor`, `programEducationOrganizationId`, `educationOrganizationId`, `beginDate`), which cannot be expressed as a single path parameter. The abstract endpoint uses query-parameter-based lookup:

`GET /ed-fi/generalStudentProgramAssociations?studentUniqueId=604822&programName=Art&programTypeDescriptor=...&programEducationOrganizationId=...&educationOrganizationId=...&beginDate=...`

All six identity fields must be provided. The query returns a single matching resource, not a paginated list. Query parameters are limited to the abstract type's own identity fields — concrete-subclass-specific fields are not queryable on the abstract endpoint.

Links pointing to GSPA resources use query-parameter-style hrefs: `/ed-fi/generalStudentProgramAssociations?studentUniqueId=604822&programName=Art&...`. The same read-only constraint, error handling, and authorization behavior described for EducationOrganization apply.

### Response Format

Abstract endpoint responses use a flat JSON format with a `_type` discriminator field at the top level, alongside the resource fields. The `_type` field contains the simple resource name of the concrete subclass (e.g., `"School"`, `"StudentArtProgramAssociation"`), not the fully qualified internal discriminator format (e.g., `"Ed-Fi:School"`). The backend maps from internal format to consumer-facing format by stripping the `ProjectName:` prefix. Simple resource names are assumed unique across projects for a given abstract type — if two extension projects register concrete subclasses with the same `ResourceName` under the same abstract type, the implementation fails fast at startup (see R4 in Risks).

The response body contains the **full concrete resource** as stored — all fields of the concrete subclass, not just the fields defined on the abstract superclass. The discriminator (`_type`) identifies the concrete type.

**EducationOrganization single-item response example:**

```json
{
  "_type": "School",
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "schoolId": 255901,
  "nameOfInstitution": "Grand Bend Elementary School",
  "educationOrganizationCategories": ["..."],
  "..."
}
```

**GeneralStudentProgramAssociation single-item response example:**

```json
{
  "_type": "StudentArtProgramAssociation",
  "id": "...",
  "studentUniqueId": "604822",
  "programName": "Art",
  "..."
}
```

List responses (applicable only to GSPA, since EducationOrganization has no list endpoint) use the same per-item format: a standard JSON array of objects, each with its own `_type` discriminator. Items are not grouped by concrete type.

---

## Link Injection Design

### Scope

Link injection targets **abstract reference elements only** — references whose declared type in the Ed-Fi data model is abstract. The two abstract reference types in the current model are:

- `educationOrganizationReference` (abstract type: EducationOrganization)
- `generalStudentProgramAssociationReference` (abstract type: GeneralStudentProgramAssociation)

Concrete-typed references such as `schoolReference` or `localEducationAgencyReference` do **not** receive injected links, even though their concrete types are subclasses of an abstract type. These references already identify a single concrete endpoint unambiguously, so link injection adds no disambiguation value.

Whether a reference receives a link is determined by the reference's declared type in the resource schema, not by the runtime data.

### Link Object Format

Each injected link object contains a single field, `href`:

```json
"link": {
  "href": "/ed-fi/educationOrganizations/255901"
}
```

There is no `rel` field and no `_type` field in the link object. Including `rel` or `_type` would require a DB lookup to resolve the concrete type at response time, which defeats the zero-cost goal. Concrete type information is provided by the abstract endpoint response itself (via the `_type` discriminator), not by the link object. Consumers who need the concrete type follow the `href`.

Adding this optional `link` field to existing reference objects in GET responses is a non-breaking change per standard API evolution practices. Consumers doing strict schema validation may need to allow additional properties.

### Href Construction: Single-Key Abstracts (EducationOrganization)

For EducationOrganization references, the `href` uses a path-style relative URL with the natural key value as a path segment:

```json
"educationOrganizationReference": {
  "educationOrganizationId": 255901,
  "link": {
    "href": "/ed-fi/educationOrganizations/255901"
  }
}
```

Construction rule: `/{namespace}/{abstractEndpointPath}/{naturalKeyValue}`

Hrefs use relative paths (not absolute URLs), matching ODS/API convention and avoiding coupling to deployment URLs. The natural key value is read directly from the reference object in the response body. No database lookup or UUID resolution is involved.

### Href Construction: Composite-Key Abstracts (GeneralStudentProgramAssociation)

For GeneralStudentProgramAssociation references, the identity is a 6-field composite key. The `href` uses a query-parameter-style format:

```json
"generalStudentProgramAssociationReference": {
  "studentUniqueId": "604822",
  "programName": "Art",
  "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Art",
  "programEducationOrganizationId": 255901,
  "educationOrganizationId": 255901,
  "beginDate": "2023-08-15",
  "link": {
    "href": "/ed-fi/generalStudentProgramAssociations?studentUniqueId=604822&programName=Art&programTypeDescriptor=uri://ed-fi.org/ProgramTypeDescriptor%23Art&programEducationOrganizationId=255901&educationOrganizationId=255901&beginDate=2023-08-15"
  }
}
```

Construction rule: `/{namespace}/{abstractEndpointPath}?{key1}={value1}&{key2}={value2}&...`

All identity fields of the abstract type are included as query parameters. Values are URL-encoded where necessary (e.g., `#` becomes `%23`). Parameter order follows the canonical identity field order defined in the schema.

### Zero Performance Overhead (Link Injection)

Link injection into GET response bodies has zero performance overhead. This applies to the href construction performed by the link injection middleware — not to abstract endpoint queries themselves, which involve standard database lookups. Every value needed to construct the `href` — the abstract endpoint path and the natural key values — is already present at response time:

- The **abstract endpoint path** is derived from precomputed schema metadata (known at startup).
- The **natural key values** are fields already present in the reference object in the response body.

Href construction is pure string concatenation. No database query, no UUID resolution, no additional I/O.

### Applicable GET Contexts

Link injection applies uniformly to all GET response contexts:

- **GET-by-id** (single-item responses): Abstract references in the returned resource receive links.
- **GET-by-query / list responses**: Each resource in the response array has its abstract references enriched with links independently.

### Exclusions

- **Streamed / CDC data**: Link injection is an API-side concern only. Resources emitted through Kafka/Debezium CDC do not include injected links.
- **OpenSearch**: Deprecated in DMS and not in scope.

---

## Implementation Design

This section describes the component-level implementation targeting the planned relational backend. Each component is scoped to support follow-up Jira ticket creation. The design assumes the relational backend artifacts — abstract identity tables (`{schema}.{AbstractResource}Identity`) and abstract union views (`{schema}.{AbstractResource}_View`) — are in place per [data-model.md](data-model.md).

### 1. Abstract Endpoint Handler

**Location**: New class in `src/dms/core/EdFi.DataManagementService.Core/Handler/`

A new handler class implements `IPipelineStep` following the same pattern as `GetByIdHandler`. This handler is the terminal step in a new abstract-resource GET pipeline defined in `ApiService`. It accepts abstract resource GET requests and delegates to the abstract resource repository (component 2) for backend lookup.

#### C# shape: AbstractGetHandler

```csharp
internal class AbstractGetHandler(
    IServiceProvider _serviceProvider,
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    IAuthorizationServiceFactory authorizationServiceFactory
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        var repository = _serviceProvider.GetRequiredService<IAbstractResourceRepository>();

        // Extract natural key values from RequestInfo
        // (single path param for EdOrg, six query params for GSPA)
        Dictionary<string, string> naturalKeyValues = ExtractNaturalKeyValues(requestInfo);

        var result = await ExecuteWithRetryLogging(
            _resiliencePipeline,
            _logger,
            "abstractGet",
            requestInfo.FrontendRequest.TraceId,
            r => IsRetryableResult(r),
            r => r is AbstractGetSuccess,
            async ct =>
                await repository.GetByNaturalKey(
                    new AbstractGetRequest(
                        AbstractResourceName: requestInfo.ResourceInfo.ResourceName,
                        ProjectName: requestInfo.ResourceInfo.ProjectName,
                        NaturalKeyValues: naturalKeyValues,
                        ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                            requestInfo.AuthorizationStrategyEvaluators,
                            requestInfo.AuthorizationSecurableInfo,
                            authorizationServiceFactory,
                            _logger
                        ),
                        TraceId: requestInfo.FrontendRequest.TraceId
                    )
                ),
            requestInfo
        );

        requestInfo.FrontendResponse = result switch
        {
            AbstractGetSuccess success =>
                new FrontendResponse(StatusCode: 200, Body: InjectType(success), Headers: []),
            AbstractGetFailureNotExists =>
                new FrontendResponse(StatusCode: 404, Body: FailureResponse.ForNotFound(...), Headers: []),
            AbstractGetFailureNotAuthorized notAuth =>
                new FrontendResponse(StatusCode: 403, Body: FailureResponse.ForForbidden(...), Headers: []),
            AbstractGetFailureBadRequest bad =>
                new FrontendResponse(StatusCode: 400, Body: FailureResponse.ForDataValidation(...), Headers: []),
            UnknownFailure failure =>
                new FrontendResponse(StatusCode: 500, Body: ToJsonError(...), Headers: []),
            _ => new(StatusCode: 500, Body: ToJsonError("Unknown AbstractGetResult", ...), Headers: []),
        };
    }
}
```

For EducationOrganization requests, the handler extracts the natural key value from the path parameter and passes it to the repository as a single-field lookup. For GeneralStudentProgramAssociation requests, the handler extracts all six identity field values from query parameters and passes them as a composite-key lookup. The handler validates that all required identity fields are present — for GSPA, if any of the six fields are missing, it returns 400 with a message listing the missing required parameters.

The handler maps the repository result to the appropriate `FrontendResponse`: 200 with the concrete document body (including the `_type` discriminator injected at the top level) on success, 404 when no matching resource exists, 403 when authorization denies access, 400 when required identity params are missing, and 500 on unexpected failures.

#### Pipeline steps

The abstract GET pipeline in `ApiService` follows the established pattern. The steps in order:

1. `RequestResponseLoggingMiddleware`
2. `CoreExceptionLoggingMiddleware`
3. `TenantValidationMiddleware`
4. `JwtAuthenticationMiddleware`
5. `ResolveDmsInstanceMiddleware`
6. `ApiSchemaValidationMiddleware`
7. `ProvideApiSchemaMiddleware`
8. `ParsePathMiddleware` (or variant for abstract routes — see routing, component 6)
9. `ValidateEndpointMiddleware` (adapted for abstract endpoints)
10. `BuildResourceInfoMiddleware`
11. `ResourceActionAuthorizationMiddleware`
12. `ProvideAuthorizationFiltersMiddleware`
13. `ProvideAuthorizationSecurableInfoMiddleware`
14. **Link injection middleware** (component 3)
15. `ProfileFilteringMiddleware`
16. **AbstractGetHandler** (terminal step)

**Pipeline execution order note**: The DMS pipeline uses a recursive `await next()` model — middleware registered *earlier* in the list has its post-processing run *later* (after inner middleware completes). So the execution order for post-processing is: (1) the terminal handler produces the response, (2) `ProfileFilteringMiddleware` post-processing filters the response body, (3) link injection middleware post-processing injects links into the already-filtered response. This ensures link injection operates on the final filtered response body.

#### Routing dispatch in ApiService.Get()

The existing `Get()` method dispatches between `_getByIdSteps` (UUID present) and `_querySteps` (no UUID). A third branch is added for abstract endpoints:

```csharp
public async Task<IFrontendResponse> Get(FrontendRequest frontendRequest)
{
    RequestInfo requestInfo = new(frontendRequest, RequestMethod.GET);
    Match match = UtilityService.PathExpressionRegex().Match(frontendRequest.Path);
    string documentUuid = match.Success ? match.Groups["documentUuid"].Value : string.Empty;

    // NEW: check abstract endpoint before UUID check — abstract paths
    // like /ed-fi/educationOrganizations/255901 contain a numeric ID,
    // not a UUID, so there is no conflict with the UUID regex.
    // Note: frontendRequest.Path contains the path after the /data/ prefix
    // has been stripped (e.g., "/ed-fi/educationOrganizations/255901").
    // IsAbstractEndpointPath must match against this stripped form.
    if (_abstractTypeMetadata.IsAbstractEndpointPath(frontendRequest.Path))
    {
        await _abstractGetSteps.Value.Run(requestInfo);
    }
    else if (documentUuid != string.Empty)
    {
        await _getByIdSteps.Value.Run(requestInfo);
    }
    else
    {
        await _querySteps.Value.Run(requestInfo);
    }

    return requestInfo.FrontendResponse;
}
```

The abstract path check must come BEFORE the UUID check because numeric natural key values (e.g., `255901`) do not match the UUID regex, but the path structure must still be discriminated before falling through to the query pipeline.

### 2. Abstract Resource Repository

**Location**: New interface in `src/dms/core/EdFi.DataManagementService.Core.External/Interface/`; PostgreSQL implementation in `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/`; SQL Server implementation in `src/dms/backend/EdFi.DataManagementService.Backend.Mssql/` (when available).

#### C# shapes

**Request contract** (`IAbstractGetRequest`):

```csharp
public interface IAbstractGetRequest
{
    ResourceName AbstractResourceName { get; }
    ProjectName ProjectName { get; }
    Dictionary<string, string> NaturalKeyValues { get; }
    IResourceAuthorizationHandler ResourceAuthorizationHandler { get; }
    TraceId TraceId { get; }
}
```

**Result type** (`AbstractGetResult`) — discriminated union following the `GetResult` pattern:

```csharp
public record AbstractGetResult
{
    /// <summary>
    /// Full concrete resource JSON body with concrete type discriminator.
    /// </summary>
    public record AbstractGetSuccess(
        string ConcreteResourceType,
        JsonNode EdfiDoc
    ) : AbstractGetResult();

    public record AbstractGetFailureNotExists() : AbstractGetResult();

    public record AbstractGetFailureNotAuthorized(
        string[] ErrorMessages,
        string[]? Hints = null
    ) : AbstractGetResult();

    public record AbstractGetFailureBadRequest(
        string ErrorMessage
    ) : AbstractGetResult();

    public record UnknownFailure(string FailureMessage) : AbstractGetResult();

    private AbstractGetResult() { }
}
```

**Repository interface**:

```csharp
public interface IAbstractResourceRepository
{
    Task<AbstractGetResult> GetByNaturalKey(IAbstractGetRequest request);
}
```

#### DI registration

Register `IAbstractResourceRepository` in `PostgresqlServiceExtensions` as scoped, matching the `IDocumentStoreRepository` pattern:

```csharp
services.AddScoped<IAbstractResourceRepository, PostgresqlAbstractResourceRepository>();
```

#### Query patterns

The PostgreSQL implementation queries the backend redesign artifacts. The query path is: identity table (to find the `DocumentId` and `Discriminator`) → document body retrieval via `dms.DocumentCache` (preferred) or reconstitution fallback.

**EducationOrganization GET-by-id** — identity table lookup + DocumentCache join:

**PostgreSQL:**

```sql
SELECT
    eid.Discriminator,
    dc.DocumentUuid,
    dc.DocumentJson
FROM edfi.EducationOrganizationIdentity eid
JOIN dms.DocumentCache dc ON dc.DocumentId = eid.DocumentId
WHERE eid.EducationOrganizationId = @educationOrganizationId;
```

**SQL Server:**

```sql
SELECT
    eid.Discriminator,
    dc.DocumentUuid,
    dc.DocumentJson
FROM edfi.EducationOrganizationIdentity eid
INNER JOIN dms.DocumentCache dc ON dc.DocumentId = eid.DocumentId
WHERE eid.EducationOrganizationId = @educationOrganizationId;
```

The `Discriminator` column provides the concrete type in `ProjectName:ResourceName` format (e.g., `Ed-Fi:School`). The implementation maps it to the simple resource name (e.g., `School`) by stripping the `ProjectName:` prefix. The query is identical across engines — only type annotations differ (PostgreSQL uses `$1` parameter placeholders with typed parameters; SQL Server uses `@param` named parameters).

**GeneralStudentProgramAssociation query** — composite identity lookup:

**PostgreSQL:**

```sql
SELECT
    gid.Discriminator,
    dc.DocumentUuid,
    dc.DocumentJson
FROM edfi.GeneralStudentProgramAssociationIdentity gid
JOIN dms.DocumentCache dc ON dc.DocumentId = gid.DocumentId
WHERE gid.StudentUniqueId = @studentUniqueId
  AND gid.ProgramName = @programName
  AND gid.ProgramTypeDescriptorId = @programTypeDescriptorId
  AND gid.ProgramEducationOrganizationId = @programEducationOrganizationId
  AND gid.EducationOrganizationId = @educationOrganizationId
  AND gid.BeginDate = @beginDate;
```

**Descriptor resolution**: The `ProgramTypeDescriptor` query parameter value is a descriptor URI string (e.g., `uri://ed-fi.org/ProgramTypeDescriptor#Art`). Before querying the identity table, the implementation must resolve the URI to a `DocumentId` (used as `ProgramTypeDescriptorId`) via the standard DMS descriptor resolution path:

```sql
-- Resolve descriptor URI to DocumentId via ReferentialId
SELECT DocumentId
FROM dms.ReferentialIdentity
WHERE ReferentialId = @descriptorReferentialId;
```

Where `@descriptorReferentialId` is the UUIDv5 hash of the *descriptor's own* `(ProjectName, ResourceName, DocumentIdentity)` — i.e., `("Ed-Fi", "ProgramTypeDescriptor", ...)`, not the GSPA's project/resource. This adds one database lookup specific to GSPA queries.

#### DocumentCache miss fallback

`dms.DocumentCache` is an optional, eventually consistent projection — the abstract endpoint must handle cache misses gracefully. When `DocumentCache` is not available (row missing or feature disabled), the repository falls back to reconstitution from relational per-resource tables via the standard reconstitution pipeline (see [flattening-reconstitution.md](flattening-reconstitution.md)).

The fallback path:
1. Use `Discriminator` from the identity table to determine the concrete resource type (e.g., `Ed-Fi:School` → `edfi.School` root table).
2. Invoke the standard reconstitution pipeline for that resource type using the `DocumentId`.
3. The reconstitution pipeline reads root + child tables in dependency order, assembles JSON via `Utf8JsonWriter` using the compiled mapping set plans for that resource (see [compiled-mapping-set.md](compiled-mapping-set.md) and [flattening-reconstitution.md](flattening-reconstitution.md)).

The identity table is preferred over the union view for single-row lookups because it is a physical table with indexes on the identity columns.

#### Authorization

Post-query authorization, matching the `GetByIdHandler` pattern. The repository retrieves the document and its `DocumentSecurityElements`, then invokes `ResourceAuthorizationHandler.Authorize()` within the repository method. If authorization fails, the repository returns `AbstractGetFailureNotAuthorized`. This ensures the concrete type discriminator is available for authorization decisions — a client authorized only for Schools receives 403 if the matched resource is a LocalEducationAgency.

### 3. Link Injection Middleware

**Location**: New class in `src/dms/core/EdFi.DataManagementService.Core/Middleware/`

#### C# shape

```csharp
internal class LinkInjectionMiddleware(
    IAbstractTypeMetadata _abstractTypeMetadata,
    ILogger<LinkInjectionMiddleware> _logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        await next();

        if (requestInfo.FrontendResponse.StatusCode != 200)
            return;

        var locations = _abstractTypeMetadata.GetAbstractReferenceLocations(
            requestInfo.ResourceInfo.ProjectName,
            requestInfo.ResourceInfo.ResourceName
        );

        if (locations.Count == 0)
            return;

        JsonNode? body = requestInfo.FrontendResponse.Body;
        if (body is JsonObject obj)
            InjectLinksIntoDocument(obj, locations);
        else if (body is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonObject itemObj)
                    InjectLinksIntoDocument(itemObj, locations);
    }
}
```

**Pipeline placement**: In the GET-by-id, query, and abstract GET pipelines, this middleware is positioned before `ProfileFilteringMiddleware` in the steps list (further from the terminal handler). Under the DMS pipeline's recursive `await next()` execution, `ProfileFilteringMiddleware`'s post-processing runs first (applying profile filtering), then this middleware's post-processing runs second (injecting links into the already-filtered response). This ensures link injection operates on the final filtered response body.

#### Schema-guided walk algorithm

The walk algorithm navigates to known abstract reference locations using precomputed metadata from `IAbstractTypeMetadata`. No blind recursion — only visits paths the schema says contain abstract references.

For each `AbstractReferenceLocation` returned by `GetAbstractReferenceLocations(projectName, resourceName)`:

1. **Navigate the JSON path prefix**. The path prefix is a sequence of property names and array wildcards derived from the `DocumentPath.ReferenceJsonPathsElements`. For example, `["addresses", "[*]", "educationOrganizationReference"]` means: navigate to `addresses` → iterate each array element → navigate to `educationOrganizationReference`.

2. **At each path segment**:
   - If the segment is a property name, navigate to that property on the current `JsonObject`. If the property is missing, stop (skip this path).
   - If the segment is `[*]`, the current node must be a `JsonArray`. Iterate each element and continue the walk for each. If the node is not an array or is empty, stop.

3. **At the reference object** (terminal node): extract identity field values using the ordered field names from the metadata.
   - If any identity field is missing or `null`, skip this reference silently — no link injection, no error, no log.
   - If all identity fields are present, construct the href:

4. **Href construction**:
   - Single-key (e.g., EducationOrganization): `/{projectEndpoint}/{abstractEndpoint}/{value}`
   - Composite-key (e.g., GSPA): `/{projectEndpoint}/{abstractEndpoint}?{field1}={urlEncode(value1)}&{field2}={urlEncode(value2)}&...`
   - URL-encode values in query parameters using `Uri.EscapeDataString()`.

5. **Inject the link**: Add `"link": { "href": "<constructed-href>" }` to the reference `JsonObject`.

Example walk for a resource with an abstract reference nested in an array:

```
Input path: ["staffEducationOrganizationAssignmentAssociations", "[*]", "educationOrganizationReference"]

Document:
{
  "staffEducationOrganizationAssignmentAssociations": [
    {
      "educationOrganizationReference": {
        "educationOrganizationId": 255901
      },
      "staffClassificationDescriptor": "..."
    },
    {
      "educationOrganizationReference": {
        "educationOrganizationId": 255902
      },
      "staffClassificationDescriptor": "..."
    }
  ]
}

Walk:
  1. Navigate to "staffEducationOrganizationAssignmentAssociations" → JsonArray
  2. [*] → iterate elements [0] and [1]
  3. For [0]: navigate to "educationOrganizationReference" → JsonObject
     - Extract "educationOrganizationId" = 255901
     - Construct href: "/ed-fi/educationOrganizations/255901"
     - Inject: "link": { "href": "/ed-fi/educationOrganizations/255901" }
  4. For [1]: same pattern with value 255902
```

Non-200 responses pass through unmodified.

### 4. Abstract Type Metadata

**Location**: Enhancement to existing schema infrastructure in `src/dms/core/EdFi.DataManagementService.Core/ApiSchema/`

#### C# shapes

**Interface**:

```csharp
internal interface IAbstractTypeMetadata
{
    /// <summary>
    /// Returns abstract reference locations for a concrete resource.
    /// O(1) lookup by (ProjectName, ResourceName).
    /// </summary>
    IReadOnlyList<AbstractReferenceLocation> GetAbstractReferenceLocations(
        ProjectName projectName,
        ResourceName resourceName
    );

    /// <summary>
    /// Checks if a URL path matches an abstract endpoint.
    /// Used by ApiService.Get() for routing dispatch.
    /// </summary>
    bool IsAbstractEndpointPath(string path);
}
```

**Data model**:

```csharp
/// <summary>
/// Describes where in a concrete resource's JSON document an abstract
/// reference lives and how to construct the link href.
/// </summary>
internal record AbstractReferenceLocation(
    /// <summary>
    /// JSON path segments to navigate to the reference object.
    /// Property names and "[*]" array wildcards.
    /// e.g., ["addresses", "[*]", "educationOrganizationReference"]
    /// </summary>
    IReadOnlyList<string> JsonPathSegments,

    /// <summary>
    /// Abstract endpoint path (e.g., "/ed-fi/educationOrganizations").
    /// </summary>
    string AbstractEndpointPath,

    /// <summary>
    /// Ordered identity field names within the reference object.
    /// e.g., ["educationOrganizationId"]
    /// </summary>
    IReadOnlyList<string> IdentityFieldNames,

    /// <summary>
    /// True for single-key abstracts (path-style href),
    /// false for composite-key (query-param-style href).
    /// </summary>
    bool IsSingleKey
);
```

#### Construction algorithm

Built once at startup from `IEffectiveApiSchemaProvider.Documents`, registered as a singleton in `DmsCoreServiceExtensions`.

**Step 1 — Build abstract type set**:

Iterate `ProjectSchema.AbstractResources` across all projects. For each abstract resource:
- Record `(ProjectName, ResourceName)` as an abstract type key.
- Compute endpoint path: `/{ProjectEndpointName}/{pluralizedResourceName}` using `ProjectSchema.GetEndpointNameFromResourceName()` or equivalent.
- Extract identity field names from `IdentityJsonPaths` (strip leading `$.` and extract the terminal field name).
- Determine single-key vs. composite-key: `identityFieldNames.Count == 1`.

**Step 2 — Build abstract endpoint path set**:

Collect all computed endpoint paths into a `HashSet<string>` for O(1) `IsAbstractEndpointPath()` checks. The stored paths use the prefix-stripped form (e.g., `/ed-fi/educationOrganizations`) since `frontendRequest.Path` arrives with the `/data/` prefix already stripped.

**Step 3 — Build per-resource reference locations**:

For each concrete resource in the schema, iterate its `ResourceSchema.DocumentPaths`. For each `DocumentPath` where `IsReference == true && !IsDescriptor`:
- Check if `(DocumentPath.ProjectName, DocumentPath.ResourceName)` is in the abstract type set.
- If yes, extract the JSON path prefix from `DocumentPath.ReferenceJsonPathsElements` (the path leading to the reference object, including `[*]` wildcards from array nesting).
- Record an `AbstractReferenceLocation` with the path segments, abstract endpoint path, identity field names, and single-key flag.

Store results keyed by the concrete resource's `(ProjectName, ResourceName)` in a `Dictionary<(ProjectName, ResourceName), IReadOnlyList<AbstractReferenceLocation>>`.

**Step 4 — Discriminator collision validation**:

For each abstract type, collect all concrete subclasses by iterating all `ResourceSchema` entries where `IsSubclass == true` and `(SuperclassProjectName, SuperclassResourceName)` matches the abstract type. Strip the `ProjectName:` prefix from each subclass name and check for duplicates. If a collision is found, throw `InvalidOperationException` with a clear message naming the abstract type and colliding subclass names. This fails fast at startup.

#### Singleton registration

```csharp
// In DmsCoreServiceExtensions.AddDmsDefaultConfiguration()
.AddSingleton<IAbstractTypeMetadata, AbstractTypeMetadata>()
```

Construction happens after the effective API schema is loaded. Options:
- **Startup task**: A new `IDmsStartupTask` (e.g., `BuildAbstractTypeMetadataTask`) that runs after `LoadAndBuildEffectiveSchemaTask`.
- **Lazy initialization**: `AbstractTypeMetadata` constructor accepts `IEffectiveApiSchemaProvider` and builds on first access via `Lazy<T>`.

Either approach ensures the metadata is available before the first request.

### 5. OpenAPI Schema Generation

**Location**: Existing component in `src/dms/core/EdFi.DataManagementService.Core/OpenApi/`

The `OpenApiDocument` class is modified to include abstract endpoint paths in the generated OpenAPI specification. For each abstract resource defined in `ProjectSchema.AbstractResources`, the generator adds:

- A path entry for the abstract endpoint (e.g., `/ed-fi/educationOrganizations/{educationOrganizationId}` for EducationOrganization, `/ed-fi/generalStudentProgramAssociations` with query parameters for GSPA). Only GET operations are defined.
- A response schema that includes the `_type` discriminator field (string) alongside the standard resource response fields. If `AbstractResource` in the schema provides an `OpenApiFragment`, it is used as the base; otherwise, the generator constructs the response schema from the abstract type's identity fields plus the `_type` discriminator.
- Path parameters (single-key) or query parameters (composite-key) derived from `IdentityJsonPaths`.

Additionally, existing concrete resource reference schemas that target abstract types receive an optional `link` property:

```json
"link": {
  "type": "object",
  "readOnly": true,
  "properties": {
    "href": { "type": "string", "readOnly": true }
  }
}
```

Only abstract reference schemas (identified via `IAbstractTypeMetadata`) receive the `link` property. Concrete reference schemas (e.g., `schoolReference`) remain unchanged.

### 6. Routing

**Location**: Existing routing infrastructure in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/` and `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`

Abstract endpoint routes coexist with the existing catch-all `{**dmsPath}` route in `CoreEndpointModule`.

**Preferred approach: schema-driven discrimination within the existing catch-all**. The catch-all route continues to capture all requests. `ApiService.Get()` inspects the parsed path against the precomputed set of abstract resource endpoint paths from `IAbstractTypeMetadata.IsAbstractEndpointPath()`. If the path matches an abstract endpoint, the request is dispatched to the `_abstractGetSteps` pipeline; otherwise, it proceeds through the existing concrete GetById/Query pipelines. This approach is consistent with the metadata-driven routing philosophy of DMS and avoids route-priority conflicts.

**Path parsing for abstract endpoints**: `ParsePathMiddleware` (or a variant) must extract the project endpoint name, abstract resource endpoint name, and natural key value(s) from the path. For EducationOrganization, the path parameter is the trailing segment after the endpoint name (e.g., `/ed-fi/educationOrganizations/255901` → `educationOrganizationId = 255901`). For GSPA, identity values come from query parameters.

Route registration is driven by the abstract resource definitions in `ProjectSchema.AbstractResources` at startup — if the effective schema includes additional abstract resources from extensions, they are automatically registered.

---

## Extension Considerations

Abstract type metadata and link injection are schema-driven, so extensions that introduce new abstract types or new concrete subclasses of existing abstract types are handled automatically. However, several edge cases warrant explicit design attention.

### New concrete subclasses from extensions

An extension project can add a new concrete subclass of an existing abstract type (e.g., a TPDM `MagnetSchool` subclass of `EducationOrganization`). This affects:

- **DDL**: The extension's concrete root table needs a maintenance trigger that upserts rows into the existing `edfi.EducationOrganizationIdentity` table. The DDL generator already handles this — extension concrete member triggers target the core abstract identity table per [data-model.md](data-model.md).
- **Union view**: The `edfi.EducationOrganization_View` must include a new `UNION ALL` arm for the extension subclass. The DDL generator emits the view from the full effective schema, so extension subclasses are included automatically.
- **Abstract type metadata**: `AbstractTypeMetadata` construction iterates all `ResourceSchema` entries with `IsSubclass == true`, regardless of project. Extension subclasses are discovered and included in discriminator collision validation.
- **`_type` discriminator collision**: If an extension subclass has the same simple name as an existing subclass (e.g., two projects both define `School`), the startup validation (step 4 of construction algorithm) detects and fails fast. Ed-Fi naming conventions make this unlikely in practice.
- **Link injection**: No change required. Link injection operates on the abstract reference's identity fields, which are defined by the abstract type, not the concrete subclass.

### New abstract types from extensions

An extension project could define a new abstract resource with its own identity fields and concrete subclasses. This is fully supported by the schema-driven design:

- `ProjectSchema.AbstractResources` includes extension-defined abstract types.
- `AbstractTypeMetadata` construction discovers them alongside core abstract types.
- The DDL generator provisions `{extSchema}.{AbstractResource}Identity` tables and union views.
- Link injection discovers abstract references targeting extension abstract types via `DocumentPath.ProjectName` and `DocumentPath.ResourceName`.
- Abstract endpoint routing registers the new endpoint path automatically.

### Extension fields on concrete subclass resources

Extension fields stored under `_ext` on a concrete subclass (e.g., `_ext.sample` on School) are part of the full concrete resource body. Since abstract endpoints return the full concrete resource, extension fields are included in the response. No special handling is required — the reconstitution pipeline already handles `_ext` tables per [extensions.md](extensions.md).

---

## Risks / Open Questions

### R1: Relational backend dependency

Implementation depends on the completion of abstract identity tables (`{schema}.{AbstractResource}Identity`), abstract union views, and the reconstitution pipeline from the backend redesign. Sequencing implementation after the relational backend redesign is complete avoids throwaway work against the current JSONB document-store architecture. If the backend redesign is delayed, this feature is blocked.

**Startup behavior**: If the relational backend tables are not present, the `AbstractTypeMetadata` singleton still builds successfully from the API schema (it only reads schema metadata, not database state). Abstract endpoint requests will fail at the repository layer with a database error. No special graceful-degradation path is designed — the feature is simply not deployed until the backend is ready.

### R2: DocumentCache miss latency

When `dms.DocumentCache` does not contain the requested document (cache miss, feature disabled, or eventual consistency lag), the fallback to full reconstitution from relational tables is significantly slower — multiple result sets, multi-table joins, and JSON assembly. For abstract endpoints, this latency is visible to the consumer on every request during cache-miss windows. Mitigation: the DocumentCache is expected to be populated by triggers on write, so misses should be rare in steady state.

### R3: GSPA descriptor resolution adds a query

GSPA abstract endpoint queries require resolving the `ProgramTypeDescriptor` URI to a `DocumentId` before querying the identity table. This adds one database lookup that EducationOrganization queries do not have. For high-volume GSPA queries, consider caching descriptor resolution results in the per-request or L1 cache (see [transactions-and-concurrency.md](transactions-and-concurrency.md), "Caching" section).

### R4: Discriminator collision across extension projects

The `_type` discriminator design assumes simple resource names are unique per abstract type. If two extension projects define concrete subclasses with the same `ResourceName` under the same abstract type, the startup validation fails fast. This is a hard failure — the DMS instance cannot start. The operator must resolve the collision (remove one extension, rename). This is considered acceptable because Ed-Fi naming conventions make collisions unlikely, and the fail-fast behavior is better than ambiguous `_type` discriminators at runtime.

### R5: Abstract endpoint path conflicts with future resource names

Abstract endpoint paths (e.g., `/ed-fi/educationOrganizations`) occupy URL space that could theoretically conflict with a future concrete resource endpoint. This is mitigated by the fact that abstract resource names are defined in the Ed-Fi data standard and are unlikely to collide with concrete resource names. The schema-driven routing check happens before the UUID/query dispatch, so abstract paths always take priority.

### R6: Profile filtering and link injection ordering

The current design places link injection after profile filtering (in execution order), meaning links are injected into the already-filtered response. If a future profile strips identity fields from abstract references, the link injection middleware would find missing identity fields and silently skip the link. This is the correct behavior — if a profile removes the identity field, the link cannot be constructed. However, this interaction should be documented and tested if profiles ever affect reference identity fields.

### R7: JSON walk performance on large list responses

Link injection walks the JSON response body for each abstract reference location. For list responses with hundreds of items and multiple abstract references per item, the walk cost is O(items × locations × path_depth). In practice, this is fast (pure in-memory `JsonNode` navigation, no allocation-heavy operations), but should be profiled under load. The schema-guided walk (no blind recursion) keeps the constant factor low.

### R8: Compiled plan integration

Abstract endpoint queries are simple single-row lookups (identity table + DocumentCache join) that do not benefit from the full compiled plan machinery used by the reconstitution pipeline. However, the reconstitution fallback path (DocumentCache miss) does use compiled plans. The abstract resource repository should obtain the compiled plan for the concrete resource type (determined by the `Discriminator`) when falling back to reconstitution. This is the same plan lookup used by standard GET-by-id.

---

## Level of Effort

Per-component T-shirt sizing:

| Component | Size | Notes |
|-----------|------|-------|
| Abstract endpoint handler + routing | M | New handler class, route registration for both abstract types, path-parameter and query-parameter parsing, new abstract GET pipeline in `ApiService` |
| Abstract resource repository (backend) | M | New interface in `Core.External` + PostgreSQL implementation querying abstract identity tables (with `DocumentCache` or reconstitution fallback) for both EdOrg and GSPA lookups; SQL Server implementation follows same query patterns |
| Link injection middleware | S-M | Post-processing JSON walk on 200 responses, no database lookup, href construction via string concatenation using precomputed abstract type metadata |
| OpenAPI schema generation | S-M | Add abstract endpoint paths with GET-only response schemas and `_type` discriminator; add optional `link` property to abstract reference schemas in existing concrete resource endpoints |
| Abstract type metadata (precomputed lookup) | S | Derive reference-to-abstract-type mapping from existing `ProjectSchema.AbstractResources` and `ResourceSchema.DocumentPaths` data, built once at startup, with discriminator collision validation |

Notes:

- OpenAPI T-shirt sizing includes both abstract endpoint paths/schemas and the optional `link` property on existing concrete reference schemas.
- Estimates assume the relational backend redesign (abstract identity tables, abstract union views, reconstitution pipeline) is complete. If the backend redesign is not yet in place, add L for the backend schema changes required to support abstract lookups.
- The largest effort items are the abstract endpoint handler and the abstract resource repository, since they introduce new API surface area spanning routing, core pipeline, and backend layers.
- **Dependency: relational backend redesign.** Implementation depends on the completion of abstract identity tables (`{schema}.{AbstractResource}Identity`) and abstract union views from the backend redesign ([data-model.md](data-model.md)). Sequencing implementation after the relational backend redesign is complete avoids throwaway work against the current JSONB document-store architecture.
