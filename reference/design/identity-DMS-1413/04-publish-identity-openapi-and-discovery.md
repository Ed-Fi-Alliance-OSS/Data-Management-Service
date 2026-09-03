---
jira: TBD
source_spike: DMS-1413
depends_on: 03
---

# Story: Publish Identity OpenAPI and Discovery Entries

## Description

Publish the fixed Identity OpenAPI document and the Discovery/metadata entries for the identity surface.
All metadata is gated by `AppSettings:EnableIdentityManagement`.

## Acceptance Criteria

- `/metadata/identity/v2/swagger.json` exists only when the feature is enabled.
- The metadata listing includes `Other: Identity` only when enabled.
- Discovery includes the `urls.identity` URL only when enabled.
- The OpenAPI document declares the five operations under `/identity/v2`.
- The served OpenAPI document injects a `servers` entry ending in the actual route-qualified `/identity/v2` base.
- The served OpenAPI document injects the same `oauth2_client_credentials` security scheme and root security requirement used by other authenticated DMS OpenAPI documents.
- Request media types are `application/json` and `text/json`.
- Request schemas are object for create, array of string for find, and array of object for search, with standard identifying properties documented on create/search objects.
- Success response schemas match the payload obligations in `design.md`, including standard identifying attributes, unsupported-as-null semantics, ordered search-response groups, `BirthDate` as `date-time`, and `Score` as `number`/`double`.
- Schemas are property-for-property, type-for-type, and required-list compatible with the pinned ODS 7.3.2 identity OpenAPI document except for differences explicitly named in the divergence ledger.
- Examples include no-match find/search response groups with empty `Responses` arrays.
- Request and response objects allow additional properties.
- Provider `InvalidProperties` `400` responses are declared for every operation.
- Unsupported-media-type `415` responses are declared for create, find, and search POST operations.
- Operation-unsupported `404` and identity-not-found `404` responses are declared with distinct problem-detail schemas or documented problem-detail `type` values.
- `Location` headers are declared for async `202` find/search and incomplete `200` results.
- Create success is declared as `200` with a string body and no `Location`.
- Provider-contract-violation and identity-upstream-failure `502` responses have distinct problem-detail schemas or documented problem-detail `type` values.

## Tasks

1. Add the embedded OpenAPI document.
2. Add metadata endpoint/listing integration.
3. Add Discovery integration.
4. Add toggle-gated absence tests.
5. Add schema/runtime agreement tests for request/response bodies, ODS schema compatibility, headers, status responses, problem-detail type values, server URLs, and security metadata.
