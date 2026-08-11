---
status: proposed
date: 2026-07-29
jira: DMS-1058
related:
  - DMS-1060
  - DMS-1410
---

# Decision Record: Ownership Token Maintenance and Delivery

> **Review gate:** This record is proposed. It does not authorize implementation or Jira changes.
> Approval is required before the proposed story drafts are created as Jira issues or
> implementation begins.

## Decision

CMS will be the source of truth for API-client ownership tokens. DMS will retrieve the values
through the existing limited-access API-client endpoint and cache them in the existing application
context cache.

The proposed implementation will:

1. use the ODS ownership model as the semantic baseline;
2. store a tenant-scoped ownership-token catalog in CMS;
3. store one nullable creator token and a unique collection of read/modify tokens per API client;
4. maintain an API client's complete ownership configuration with one atomic replacement endpoint;
5. deliver ownership values directly from CMS to DMS through `ApplicationContext`;
6. retain the existing configurable application-context cache lifetime, which defaults to 600
   seconds;
7. make CMS lookups and cache keys tenant-aware; and
8. fail closed when DMS needs application context and cannot resolve it.

## Document Ownership and Handoffs

This record owns the proposed CMS persistence and HTTP contracts for ownership-token maintenance
and the DMS retrieval, caching, tenant, and failure contracts for consuming those values.

| Artifact | Responsibility |
| --- | --- |
| This decision record | Ownership-token maintenance, delivery, and cache contract |
| [Authorization design](auth.md) | Overall relational authorization design; must defer to this record for ownership-token delivery after approval |
| [DMS-1060 story mirror](../epics/14-authorization/11-ownership-auth-strategy.md) | POST stamping and single-record ownership authorization that consume this contract |
| [DMS-1410 story mirror](../epics/14-authorization/11b-ownership-auth-get-many.md) | GET-many ownership filtering that consumes this contract |
| [DMS-1058 spike mirror](../epics/14-authorization/09-design-ownership-token-maintenance.md) | Spike scope, acceptance criteria, and approval gate |
| [CMS story draft](../epics/14-authorization/23-store-api-client-ownership-tokens-in-cms.md) and [DMS story draft](../epics/14-authorization/24-load-and-cache-api-client-ownership-tokens-from-cms.md) | Proposed delivery scope and acceptance evidence; after approval they become Jira stories and must not redefine this contract |
| [Operational-lifecycle spike draft](../epics/14-authorization/25-ownership-token-operational-lifecycle-spike.md) | Optional follow-up investigation; reviewers decide whether to create, defer, or reject it |

The evidence and sources supporting this proposal are retained in
[Evidence Baseline](#evidence-baseline).

## Evidence Baseline

This record was evaluated against DMS
[`26c282b76194b5c77940a7ef639e89dce898049e`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/commit/26c282b76194b5c77940a7ef639e89dce898049e)
and the immutable ODS source revisions cited below. Jira status, assignee, sprint, and other
transient planning fields are intentionally not design evidence.

### Spike and downstream contracts

- The checked-in [DMS-1058 spike mirror](../epics/14-authorization/09-design-ownership-token-maintenance.md)
  requires a proposed CMS storage model, maintenance endpoints, a DMS read/cache design,
  post-approval implementation stories that block DMS-1060 and DMS-1410, and any necessary
  updates to both descriptions.
- The [DMS-1059 story mirror](../epics/14-authorization/10-emit-ownership-column-and-index.md)
  and [DDL emitter](../../../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs)
  establish the downstream storage type: `dms.Document.CreatedByOwnershipTokenId` is a nullable
  `SMALLINT` with an index in both supported providers.
- The [DMS-1060 story mirror](../epics/14-authorization/11-ownership-auth-strategy.md) establishes
  one nullable creator token for POST stamping, many tokens for single-record authorization,
  unchanged ownership on PUT, PostgreSQL and SQL Server parity, and a defensive failure at 2,000
  or more SQL Server scalar token parameters. The [DMS-1410 story mirror](../epics/14-authorization/11b-ownership-auth-get-many.md)
  establishes GET-many ownership filtering using `OwnershipTokenIds`, including the same
  PostgreSQL and SQL Server parameter-limit boundary.

These contracts constrain DMS-1058's output without preselecting how CMS stores or delivers the
two inputs.

### Immutable ODS compatibility baseline

The Admin DB schema was reviewed at Ed-Fi-ODS commit
[`24fe66cfc04459ad6d6cac09d635d3c149b24669`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/24fe66cfc04459ad6d6cac09d635d3c149b24669):

- [`dbo.OwnershipTokens`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Artifacts/PgSql/Structure/Admin/0060-Add-OwnershipTokens.sql)
  uses a `SMALLSERIAL` primary key and a nullable `VARCHAR(50)` description.
- [`dbo.ApiClientOwnershipTokens`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Artifacts/PgSql/Structure/Admin/0061-Add-ApiClientsOwnershipTokens.sql)
  stores the API client's read/modify tokens. It has required API-client and `SMALLINT` token
  foreign keys but no unique constraint on the assignment pair.
- [`dbo.ApiClients.CreatorOwnershipTokenId`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Artifacts/PgSql/Structure/Admin/0062-Add-CreatorOwnershipTokenId-To-ApiClients.sql)
  is a separate nullable `SMALLINT` foreign key and is not unique across API clients.

ODS runtime behavior was reviewed at the same Admin DB revision and Ed-Fi-ODS-Implementation
commit
[`37ff595c171b73e524d96b13103ef9ae01712beb`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/tree/37ff595c171b73e524d96b13103ef9ae01712beb):

- The
  [`GetClientForToken` projection](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Artifacts/PgSql/Structure/Admin/0063-Update-GetClientForToken-For-Record-Level-Ownership.sql)
  returns the creator token and one read/modify token per joined row. The
  [`ApiClientDetailsProvider`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Application/EdFi.Ods.Api/Security/Authentication/ApiClientDetailsProvider.cs)
  reconstitutes and deduplicates the collection.
- The
  [create decorator](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/blob/37ff595c171b73e524d96b13103ef9ae01712beb/Application/EdFi.Ods.Features.OwnershipBasedAuthorization/Security/OwnershipInitializationCreateEntityDecorator.cs)
  copies the API client's nullable creator token to every new aggregate. The
  [NHibernate configuration](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/blob/37ff595c171b73e524d96b13103ef9ae01712beb/Application/EdFi.Ods.Features.OwnershipBasedAuthorization/NHibernate/OwnershipBasedAuthorizationNHibernateConfigurationActivity.cs)
  makes that value insertable but not updatable.
- The
  [ODS 7.3 operational guide](https://github.com/Ed-Fi-Alliance-OSS/ed-fi-alliance-oss.github.io/blob/8eede7611072be0518526ccb16b6f62d9d729724/odsApi_versioned_docs/version-7.3/platform-dev-guide/features/ownership-based-authorization.md)
  configures the creator token and read/modify assignment independently. A creator assignment does
  not itself grant read/modify access.

The compatibility requirements are therefore the identifier width, one creator token, many
read/modify tokens, independent assignments, shared tokens across API clients, create-only
stamping, and deduplicated runtime reads. This proposal deliberately improves on the ODS schema by
requiring a description and preventing duplicate assignment rows.

### Current CMS and DMS baseline

At the pinned DMS revision:

- The PostgreSQL and SQL Server
  [`dmscs.ApiClient`](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0003_Create_ApiClient_Table.sql)
  [schemas](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/Deploy/Scripts/0003_Create_ApiClient_Table.sql)
  attach each API client to one application and contain no ownership columns. The
  [API-client response](../../../../src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/ApiClient/ApiClientResponse.cs)
  likewise contains no ownership values.
- CMS stores nullable `TenantId` on tenant-owned root aggregates when multitenancy is enabled, as
  shown in the PostgreSQL
  [tenant DDL](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0025_Add_TenantId_To_Tables.sql).
  Both API-client repositories scope a client through `ApiClient -> Application -> Vendor`; see the
  [PostgreSQL](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Repositories/ApiClientRepository.cs)
  and
  [SQL Server](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/Repositories/ApiClientRepository.cs)
  implementations.
- [`ApiClientModule`](../../../../src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ApiClientModule.cs)
  already exposes the limited-access `GET /v3/apiClients/{clientId}` consumed by DMS. Its secured
  create/update routes are coordinated with the configured identity provider.
- The OpenIddict
  [client repository](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Repositories/OpenIddictClientRepository.cs)
  and
  [Keycloak client repository](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Keycloak/KeycloakClientRepository.cs)
  implement the existing JWT-claim synchronization pattern. Adding ownership to that pattern
  would require two provider implementations and their failure/compensation paths.
- DMS
  [`ApplicationContext`](../../../../src/dms/core/EdFi.DataManagementService.Core/Configuration/ApplicationContext.cs)
  already represents the CMS API-client response, and
  [`ConfigurationServiceApplicationProvider`](../../../../src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceApplicationProvider.cs)
  already retrieves it from `GET /v3/apiClients/{clientId}`.
- [`CachedApplicationContextProvider`](../../../../src/dms/core/EdFi.DataManagementService.Core/Configuration/CachedApplicationContextProvider.cs)
  already provides `HybridCache` stampede protection and explicit per-client reload. Its key is
  currently only `ApplicationContext:{clientId}`, and its configured default lifetime is
  [600 seconds](../../../../src/dms/core/EdFi.DataManagementService.Core/Configuration/CacheSettings.cs).
  The provider currently collapses not-found, dependency failure, and malformed data to null.
- JWT-derived
  [`ClientAuthorizations`](../../../../src/dms/core/EdFi.DataManagementService.Core.External/Model/ClientAuthorizations.cs)
  and
  [`JwtValidationService`](../../../../src/dms/core/EdFi.DataManagementService.Core/Security/JwtValidationService.cs)
  contain EducationOrganization IDs, namespace prefixes, and datastore IDs, but no ownership
  values.
- The relational
  [`RelationalAuthorizationContext`](../../../../src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalQueryRequestContracts.cs)
  has no ownership inputs, while the
  [`RelationshipAuthorizationStrategyClassifier`](../../../../src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationStrategyClassifier.cs)
  explicitly recognizes `OwnershipBased` as known but not enabled. DMS therefore fails closed until
  DMS-1060 and DMS-1410 consume an approved metadata contract.
- The [authorization design](auth.md#authentication) currently says ownership tokens are JWT
  claims with a default 30-minute token lifetime. That statement predates the direct
  application-context cache and conflicts with this proposal's selected delivery path.

### Evidence-to-decision traceability

| Evidence | Contract result |
| --- | --- |
| ODS and `dms.Document` both use `SMALLINT` ownership identifiers | CMS uses durable, positive `SMALLINT` ownership-token IDs |
| ODS stores one creator token and a separate read/modify collection per API client | CMS preserves both independent API-client assignments |
| ODS permits duplicate assignment rows but deduplicates during retrieval | CMS uses a composite assignment key and returns a unique, sorted collection |
| ODS stamps on create and makes the stored token non-updatable | Creator changes affect future creates only; PUT never transfers existing document ownership |
| DMS-1060 and DMS-1410 fail at 2,000 or more SQL Server scalar ownership parameters | CMS rejects assignment collections larger than 1,999; both DMS consumers retain their defensive limit |
| CMS has an existing limited API-client endpoint and DMS already retrieves and caches it | Ownership values extend `ApplicationContext`; no identity-provider or JWT changes are required |
| CMS scopes API clients through their application's vendor, while tenant-owned catalog roots carry nullable `TenantId` | The ownership-token catalog carries `TenantId`, and every assignment/read validates the API client's existing tenant scope |
| The existing cache key is client-only and provider failures collapse to null | The contract adds tenant-qualified keys/requests and typed not-found versus unavailable outcomes |
| API-client create/update currently has no ownership fields and coordinates identity-provider state | A focused atomic ownership subresource is the single mutation path |
| DMS documents retain numeric ownership IDs without a cross-database catalog foreign key | The first version does not hard-delete token identities |

## Non-Goals

The following capabilities are not part of the proposed DMS-1058 solution:

- add ownership claims to access tokens or modify OpenIddict and Keycloak protocol mappers;
- expose ownership tokens through `/oauth/token_info`;
- provide Admin App UI;
- transfer ownership of existing DMS documents when API-client configuration changes; or
- implement the operational enhancements evaluated by
  [DMS-1374](../epics/14-authorization/25-ownership-token-operational-lifecycle-spike.md).

These items are intentionally outside the implementation stories created by DMS-1058. DMS-1374
evaluates which operational enhancements justify additional implementation stories.

## Deliberate Constraints Requiring Approval

Three constraints materially affect lifecycle, capacity, and security behavior and are therefore
contract decisions rather than ordinary non-goals.

### Cache invalidation

The proposed implementation does not push cache invalidation events from CMS to DMS. It accepts a
bounded stale-access window equal to the configured application-context cache lifetime, which
defaults to 600 seconds. Rejecting that window requires revising the initial design before
implementation.

### Token deletion

The proposed implementation does not hard-delete ownership-token identities. Access is revoked by
removing API-client assignments, while the catalog identity remains available to interpret
historical DMS documents. A future retirement/deactivation lifecycle can be evaluated separately.

### Identifier capacity

The proposed global positive `SMALLINT` identity supports 32,767 ownership-token identities over
the lifetime of one CMS database. IDs are never reused, including after a future retirement
mechanism. CMS must fail without wrapping or reusing an ID when the range is exhausted.

Per-tenant ID namespaces and widening `dms.Document.CreatedByOwnershipTokenId` are not part of the
first implementation. Approval explicitly accepts this finite global capacity; otherwise the
identifier design must be revised before implementation.

## Rationale

DMS already retrieves `GET /v3/apiClients/{clientId}` from CMS and caches the response with
`HybridCache`. Extending that contract:

- avoids two identity-provider implementations and their compensation workflows;
- avoids potentially large JWTs;
- provides a shorter default freshness window than the 30-minute access-token lifetime in the
  authorization design;
- reuses existing stampede protection and configuration; and
- keeps ownership maintenance entirely inside CMS.

The checked-in authorization design currently says ownership tokens are encoded in the JWT. That
statement must be updated after this proposal is approved.

## Ownership Semantics

### Creator token

Each API client has zero or one `CreatorOwnershipTokenId`.

- DMS stamps this value into `dms.Document.CreatedByOwnershipTokenId` for every create.
- When it is null, DMS stamps null.
- Changing the configured creator token affects only future creates.
- PUT does not change the ownership token on an existing DMS document.

### Read/modify tokens

Each API client has zero or more `OwnershipTokenIds`.

- They authorize reads, updates, and deletes of documents stamped with a matching token.
- An empty collection grants no access through `OwnershipBased`.
- The collection contains unique values and is returned in ascending order.
- A maximum of 1,999 values may be assigned to one API client.

CMS enforces the 1,999-value maximum so it cannot create a configuration that exceeds DMS-1060's
single-record or DMS-1410's GET-many SQL Server scalar-parameter limit. Both DMS consumers retain
their defensive failure for 2,000 or more values.

### Independent assignments

The creator token and read/modify collection are independent, matching ODS persistence semantics.
Setting a creator token does not implicitly add it to `OwnershipTokenIds`.

Administrators who want a client to read or modify documents it creates must explicitly include
the creator token in the read/modify collection.

### Sharing

An ownership token may be assigned to multiple API clients within the same tenant. Sharing a token
grants those clients access to documents stamped with that token.

## CMS Persistence Contract

CMS will add the following PostgreSQL and SQL Server structures.

### `dmscs.OwnershipToken`

| Column | Type | Rules |
| --- | --- | --- |
| `Id` | `SMALLINT` identity | Primary key; generated by CMS in the global range 1–32,767; never reused |
| `TenantId` | `BIGINT` nullable | Current tenant in multitenant mode; null in single-tenant mode |
| `Description` | `VARCHAR(50)` / `NVARCHAR(50)` | Required, non-blank, not unique |
| `CreatedAt` | Existing provider timestamp type | Required |
| `CreatedBy` | Existing audit-user type | Nullable per existing CMS convention |
| `LastModifiedAt` | Existing provider timestamp type | Nullable |
| `ModifiedBy` | Existing audit-user type | Nullable |

Constraints and indexes:

- primary key on `Id`;
- foreign key from `TenantId` to `dmscs.Tenant.Id`;
- index on `TenantId`; and
- no uniqueness constraint on `Description`.

Ownership-token IDs are globally unique within the CMS database because `Id` is the identity key.
Tenant filtering still applies to every catalog operation. The global identity range is a lifetime
capacity, not a concurrent-row limit: the lack of hard delete and the no-reuse rule mean retired
IDs would continue to consume it.

### `dmscs.ApiClient.CreatorOwnershipTokenId`

Add a nullable `SMALLINT` column to `dmscs.ApiClient`.

Constraints and indexes:

- foreign key to `dmscs.OwnershipToken.Id` with no cascade delete; and
- non-unique index on `CreatorOwnershipTokenId`.

The API and repository must ensure the token and API client belong to the same tenant. The API
client's tenant continues to be inferred through `ApiClient -> Application -> Vendor`.

### `dmscs.ApiClientOwnershipToken`

| Column | Type | Rules |
| --- | --- | --- |
| `ApiClientId` | `INT` | Required |
| `OwnershipTokenId` | `SMALLINT` | Required |
| `CreatedAt` | Existing provider timestamp type | Required |
| `CreatedBy` | Existing audit-user type | Nullable per existing CMS convention |
| `LastModifiedAt` | Existing provider timestamp type | Nullable |
| `ModifiedBy` | Existing audit-user type | Nullable |

Constraints and indexes:

- composite primary key on `(ApiClientId, OwnershipTokenId)`;
- API-client foreign key with cascade delete;
- ownership-token foreign key with no cascade delete; and
- index on `OwnershipTokenId`.

The composite key deliberately improves on the legacy ODS table, which did not prevent duplicate
assignment rows.
`ApiClientId` follows the parent `dmscs.ApiClient.Id` type, which DMS-1337 retyped to `INT`
after this record's evidence baseline; see the
[operational-lifecycle record's erratum](ownership-token-operational-lifecycle.md#errata-to-the-dms-1058-record).

### Tenant rules

- In multitenant mode, token creation stamps the current `TenantId`.
- In single-tenant mode, token creation stores null `TenantId`.
- Catalog queries return only the current tenant's rows.
- Cross-tenant token IDs are treated as unresolved references and are never assignable.
- API-client ownership reads and writes use the API client's existing tenant scope.

### Lifecycle rules

- Ownership-token IDs are durable security identifiers.
- The first version has no hard-delete endpoint.
- A description can be changed without changing authorization semantics.
- Removing a token from an API client's assignment removes future access after cache expiry; it
  does not delete the catalog row.
- Deleting an API client cascades its assignment rows. The token catalog remains.
- Replacing a creator token does not alter existing `dms.Document` rows.
- Assignment replacement is transactional and last-write-wins, consistent with current CMS update
  endpoints; no ETag contract is introduced.

## CMS HTTP Contract

All management endpoints use the existing secured endpoint policy and existing CMS
`application/problem+json` error contract.

### Ownership-token catalog

| Method | Route | Result |
| --- | --- | --- |
| `POST` | `/v3/ownershipTokens/` | Create a token; return 201 with `Location` and `{ "id": n }` |
| `GET` | `/v3/ownershipTokens/` | Return the current tenant's paged token list |
| `GET` | `/v3/ownershipTokens/{id}` | Return one token in the current tenant |
| `PUT` | `/v3/ownershipTokens/{id}` | Change only the description; return 204 |

There is no `DELETE` endpoint.

Create request:

```json
{
  "description": "District A integration"
}
```

Token response:

```json
{
  "id": 17,
  "description": "District A integration"
}
```

Update request:

```json
{
  "id": 17,
  "description": "District A SIS integration"
}
```

The update body `id` must match the route ID; a mismatch is a 400 validation failure. The
collection uses the existing `offset`, `limit`, `orderBy`, and `direction` paging contract.
`orderBy` accepts `id` and `description`; the default is `id` ascending. No description filtering
or search is added in the first version.

### API-client ownership configuration

| Method | Route | Result |
| --- | --- | --- |
| `GET` | `/v3/apiClients/{id}/ownership` | Return the complete ownership configuration |
| `PUT` | `/v3/apiClients/{id}/ownership` | Atomically replace the complete configuration; return 204 |

Response and PUT request:

```json
{
  "creatorOwnershipTokenId": 17,
  "ownershipTokenIds": [17, 23]
}
```

Contract rules:

- `creatorOwnershipTokenId` is nullable.
- `ownershipTokenIds` is required and may be empty.
- The PUT is a full replacement, not a patch.
- Values in `ownershipTokenIds` must be distinct.
- The collection cannot contain more than 1,999 values.
- Every referenced token must exist in the current tenant.
- The creator token is not required to appear in `ownershipTokenIds`.
- The repository validates all references and replaces the creator and collection in one database
  transaction.
- Repeating a successful PUT with the same body returns 204 and leaves the same effective
  configuration.
- The provider implementations must accept the 1,999-token boundary without exceeding SQL
  Server's 2,100-parameter limit. They use a provider-appropriate set input or bounded batches
  inside the one transaction, not one database roundtrip per assignment.
- Existing API-client POST and PUT contracts do not accept ownership fields, avoiding a second
  mutation path and identity-provider workflow changes.
- A newly created API client therefore begins with a null creator token and an empty token
  collection.

### Existing limited-access API-client response

The response from `GET /v3/apiClients/{clientId}` gains:

```json
{
  "creatorOwnershipTokenId": 17,
  "ownershipTokenIds": [17, 23]
}
```

The full response retains all existing fields. When no ownership is configured, the added fields
are:

```json
{
  "creatorOwnershipTokenId": null,
  "ownershipTokenIds": []
}
```

The collection is deduplicated and sorted by CMS. The limited-access API-client collection response
uses the same representation for consistency.

### HTTP failures

| Condition | Status |
| --- | --- |
| Invalid ID, blank/long description, duplicate assignment, or more than 1,999 assigned tokens | 400 |
| Target API client or target catalog token is not visible in the current tenant | 404 |
| Ownership-configuration PUT references a missing or cross-tenant creator/read token | 409 unresolved reference |
| Ownership-token identity range is exhausted | 500 using the existing unknown-failure contract; no ID is wrapped or reused |
| Unexpected database failure | 500 using the existing unknown-failure contract |

Cross-tenant GETs return 404 to avoid disclosing resource existence.

## DMS Retrieval Contract

### Application context

DMS extends `ApplicationContext` with:

```csharp
short? CreatorOwnershipTokenId,
IReadOnlyList<short> OwnershipTokenIds
```

Ownership values do not become fields on JWT-derived `ClientAuthorizations`.

DMS resolves application context at most once for an authenticated resource request when a
consumer requires it. The request-scoped result is available to profile processing and relational
authorization. DMS-1373 propagates both the JWT-derived client authorizations and CMS-derived
application context into `RelationalAuthorizationContext`. DMS-1060 consumes both ownership
fields; DMS-1410 consumes `OwnershipTokenIds`.

The request-scoped holder memoizes the first `Success`, `NotFound`, or `Unavailable` result for the
remainder of that request. Only `Success` crosses the request boundary into `HybridCache`.

Application context is required:

- for every POST, because DMS-1060 stamps every create even if the resource does not use
  `OwnershipBased`; and
- for DMS-1060 GET-by-id, PUT, and DELETE and DMS-1410 GET-many when the selected authorization
  strategies include `OwnershipBased`.

Existing profile behavior may independently require application context.

### Tenant-aware CMS request

`IApplicationContextProvider` and `IConfigurationServiceApplicationProvider` gain the current
tenant as an argument:

```csharp
Task<ApplicationContextLookupResult> GetApplicationByClientIdAsync(string clientId, string? tenant);
Task<ApplicationContextLookupResult> ReloadApplicationByClientIdAsync(
    string clientId,
    string? tenant
);
```

`ApplicationContextLookupResult` distinguishes `Success`, `NotFound`, and `Unavailable`.
Malformed CMS responses map to `Unavailable`.

The CMS provider:

- sends the `Tenant` header on the individual HTTP request when a tenant is present;
- omits it in single-tenant mode; and
- does not mutate shared `HttpClient.DefaultRequestHeaders` for tenant selection.

### Cache contract

DMS continues to use `CachedApplicationContextProvider` and `HybridCache`.

- Single-tenant cache key: `ApplicationContext:single:{clientId}`.
- Multitenant cache key:
  `ApplicationContext:tenant:{tenant.ToLowerInvariant()}:{clientId}`. CMS tenant lookup is
  case-insensitive, so reload and lookup normalize the cache-key tenant identically while the HTTP
  header retains the request's tenant value.
- Expiration: existing `ApplicationContextCacheExpirationSeconds`, default 600 seconds. The
  configured positive value is the actual stale-access bound; v1 introduces no hard 600-second
  maximum.
- Successful contexts are cacheable.
- Not-found, unavailable, and malformed responses are not negatively cached.
- `HybridCache` continues to provide per-key stampede protection.
- A normal cache miss issues one CMS request. `NotFound` does not trigger the current immediate
  second "reload" request.
- No push or event-driven invalidation is introduced.
- The explicit reload operation removes and reloads only the normalized matching tenant/client
  key, using one CMS request.

An ownership assignment or revocation becomes visible to DMS no later than the configured cache
lifetime. The bounded stale-access window is an explicit tradeoff of this proposal.

### Failure behavior

The provider must distinguish:

1. API client not found in the current tenant;
2. CMS unavailable or returning an unsuccessful dependency response; and
3. malformed CMS response.

When a required application context is not already cached:

- API-client not found fails authentication/authorization with 401;
- CMS unavailability or malformed data fails closed with 503; and
- DMS never substitutes an empty ownership configuration for a failed lookup.

A successfully resolved application context with a null creator and empty token collection is a
valid configuration:

- DMS-1060 POST stamps null; and
- DMS-1060 applies its ownership-uninitialized or ownership-mismatch behavior to a single record,
  while DMS-1410 applies its GET-many ownership filter using the empty token collection.

Public failures do not disclose ownership-token values.

## Consequences

- Removing a client's token assignment can remain ineffective for up to the configured cache
  lifetime.
- Changing a creator token can take the same amount of time to affect new documents.
- The default lifetime is 600 seconds, not an enforced maximum. Changing the positive configured
  value changes the operational stale-access bound without changing the contract.
- Every POST depends on a successfully resolved application context even when the resource does
  not use `OwnershipBased`. A warm cached success permits the write during a CMS outage; a cold or
  expired lookup fails closed with 503.
- A direct CMS lookup avoids access-token-sized ownership collections and avoids waiting for token
  renewal.
- Tenant-qualified cache keys and per-request tenant headers are required before ownership metadata
  can be treated as authorization input.

The proposed configured-TTL bounded-staleness model requires explicit approval. If immediate
revocation or an enforced maximum is required, this proposal must be revised to add invalidation,
cap the setting, or remove caching; those are not part of the simplest first implementation.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Put ownership claims in access tokens | Rejected for the initial design: it requires separate OpenIddict and Keycloak delivery work, can enlarge JWTs substantially, and makes revocation depend on token renewal. |
| Return ownership values from `/oauth/token_info` | Rejected: DMS already has a narrower CMS application-context dependency, and the token-info endpoint is not needed for request processing. |
| Perform an uncached CMS lookup for every applicable request | Rejected: it adds avoidable latency and dependency load when the existing application-context cache provides a bounded freshness contract. |
| Split ownership identity across JWT and mutable values across CMS | Rejected: it creates two ownership freshness models and downgrade/failure rules without adding value to the initial contract. |
| Add push or event-driven cache invalidation | Deferred: it adds a distributed invalidation path; the proposed first implementation explicitly accepts the configured cache lifetime as its stale-access bound. |
| Hard-delete ownership-token identities | Rejected: durable identifiers are needed to interpret historical document ownership after assignments are removed. |

## Impact on DMS-1060, DMS-1410, and `auth.md`

DMS-1060's CRUD and SQL semantics remain unchanged. Its description is updated to state:

> `CreatorOwnershipTokenId` and `OwnershipTokenIds` come from the tenant-qualified CMS
> `ApplicationContext`, not JWT claims. DMS resolves and caches this context through
> `GET /v3/apiClients/{clientId}`. CMS limits assignments to 1,999 tokens; DMS retains its defensive
> failure for 2,000 or more.

DMS-1410's GET-many filtering uses `OwnershipTokenIds` from the same tenant-qualified CMS
`ApplicationContext`. `CreatorOwnershipTokenId` is not a GET-many input.

The authentication section of the [authorization design](auth.md) should likewise replace the
ownership-token JWT statement with the direct CMS application-context contract.

No ownership fields are added to `/oauth/token_info`.

## Follow-up Spike

[DMS-1374](../epics/14-authorization/25-ownership-token-operational-lifecycle-spike.md) owns the
investigation of the operational enhancements excluded above. It covers revocation guarantees,
administration, retirement, API-client hand-off, diagnostics, and identifier-capacity safeguards
without expanding the initial implementation stories.

DMS-1374 does not defer DMS-1058's caching acceptance criterion. This contract retains the initial
cache lifetime and failure model. The follow-up blocks neither DMS-1060 nor DMS-1410 unless product
or security rejects the bounded-staleness model for the initial release.

JWT delivery is not part of DMS-1374 unless new requirements invalidate the direct CMS
application-context decision.

## Post-Approval Implementation Handoff

The approved handoff created implementation stories DMS-1372 and DMS-1373 and operational
follow-up spike DMS-1374.

### Story 1: DMS-1372 — Store and maintain API-client ownership tokens in CMS

Scope:

- PostgreSQL and SQL Server schema changes;
- tenant-scoped ownership-token repositories and models;
- secured catalog endpoints;
- secured atomic API-client ownership GET/PUT endpoints;
- ownership fields on limited API-client responses;
- validation, ProblemDetails, audit, transactional behavior, and provider-safe handling at the
  1,999-token boundary; and
- unit, PostgreSQL/SQL Server integration, and CMS E2E coverage.

This story blocks DMS-1060 and DMS-1410.

### Story 2: DMS-1373 — Load and cache API-client ownership tokens from CMS in DMS

Scope:

- ownership fields on `ApplicationContext`;
- tenant-aware provider methods, request headers, and cache keys;
- typed not-found versus unavailable results;
- positive cache-expiration validation;
- request-scoped result reuse and at most one CMS lookup per normal cache miss;
- fail-closed behavior;
- propagation into relational authorization context;
- focused provider, cache, pipeline, multitenancy, and integration tests; and
- correction of the ownership delivery statement in `auth.md`.

This story blocks DMS-1060 and DMS-1410.

Story 1 owns the CMS wire contract and must be available before Story 2's end-to-end acceptance.
Development may proceed in parallel against this approved contract, but both stories directly block
DMS-1060 and DMS-1410.

### DMS-1060 and DMS-1410 updates

The approved handoff:

1. links DMS-1372 and DMS-1373 as blockers of DMS-1060 and DMS-1410;
2. adds the CMS application-context paragraphs above to both tickets: DMS-1060 consumes both
   ownership fields for POST stamping and single-record authorization, while DMS-1410 consumes
   `OwnershipTokenIds` for GET-many filtering only;
3. retains DMS-1060's ownership CRUD, ProblemDetails, batching, and database-provider acceptance
   criteria, while assigning GET-many filtering and removal of the temporary GET-many 501 only to
   DMS-1410; and
4. retains the defensive SQL Server failure at 2,000 or more tokens for both consumers.

## Acceptance Criteria Traceability

| DMS-1058 acceptance criterion | Contract section |
| --- | --- |
| Review ODS storage and propose CMS storage | [Evidence Baseline](#evidence-baseline); [Ownership Semantics](#ownership-semantics); [CMS Persistence Contract](#cms-persistence-contract) |
| Propose endpoints to maintain API-client ownership tokens | [CMS HTTP Contract](#cms-http-contract) |
| Propose how DMS reads and caches ownership tokens | [DMS Retrieval Contract](#dms-retrieval-contract); [Consequences](#consequences) |
| After approval, create tickets that block DMS-1060 and DMS-1410 | [Post-Approval Implementation Handoff](#post-approval-implementation-handoff) |
| Update DMS-1060 and DMS-1410 if necessary | [Impact on DMS-1060, DMS-1410, and `auth.md`](#impact-on-dms-1060-dms-1410-and-authmd) |
