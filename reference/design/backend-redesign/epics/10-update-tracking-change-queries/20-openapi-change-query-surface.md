---
jira: DMS-1183
jira_url: https://edfi.atlassian.net/browse/DMS-1183
---

# Story: Consume Change Queries OpenAPI Metadata in DMS

## Description

The MetaEd side of the Change Queries OpenAPI contract has been implemented. This story tracks the DMS continuation: accepting the updated ApiSchema OpenAPI payloads and serving them through DMS metadata and discovery surfaces.

DMS must accept and preserve `projectSchema.openApiBaseDocuments.changeQueries` in the raw loaded ApiSchema used for metadata serving. The normalized effective schema must continue stripping OpenAPI payloads before hash/model derivation, so this OpenAPI-only contract remains hash-neutral and does not require an `apiSchemaVersion` bump.

Runtime Change Query route resolution is not driven by OpenAPI:

- `/changeQueries/v1/availableChangeVersions` is a fixed DMS runtime route owned by `21-available-change-versions-endpoint.md`.
- Resource and descriptor `/deletes` and `/keyChanges` route resolution is model-driven from the effective `ApiSchema.json` endpoint mappings and RelationalBackend model inventory, as owned by `23-deletes-endpoint.md` and `24-keychanges-endpoint.md`.

OpenAPI metadata serving must not become the runtime route source of truth.

## Acceptance Criteria

### MetaEd Contract Already Delivered

The following items are the input contract DMS consumes; they are not new DMS implementation work in this story.

- MetaEd emits resource, descriptor, and profile `/deletes` endpoint definitions for every non-composite generated resource and descriptor in the OpenAPI documents.
- MetaEd emits resource, descriptor, and profile `/keyChanges` endpoint definitions for every non-composite generated resource and descriptor in the OpenAPI documents.
- MetaEd emits `minChangeVersion` and `maxChangeVersion` query parameters on live resource and descriptor GET-many endpoint definitions.
- MetaEd emits `minChangeVersion`, `maxChangeVersion`, `limit`, `offset`, and `totalCount` query parameters on `/deletes` and `/keyChanges` definitions.
- MetaEd emits a core-only `projectSchema.openApiBaseDocuments.changeQueries` document containing the standalone ODS-style `/availableChangeVersions` OpenAPI path.
- MetaEd emits ODS-compatible response body shapes for `/deletes`, `/keyChanges`, and `/availableChangeVersions`.
- MetaEd does not advertise unsupported snapshot behavior in the DMS v1.0 OpenAPI surface.
- MetaEd includes core resources, descriptors, extension-defined resources/descriptors, profiles, and `SchoolYearType` where applicable.

### DMS ApiSchema Loading

- DMS schema validation accepts optional `projectSchema.openApiBaseDocuments.changeQueries` alongside the existing `resources` and `descriptors` base documents.
- DMS preserves `projectSchema.openApiBaseDocuments.changeQueries` in the raw loaded ApiSchema document used for metadata serving.
- DMS continues stripping `projectSchema.openApiBaseDocuments`, including `changeQueries`, from the normalized/effective schema before effective-schema hashing, model derivation, DDL generation, and mapping-pack selection.
- Adding or removing OpenAPI payloads, including `changeQueries`, does not change the effective-schema hash.
- This story does not require an `apiSchemaVersion` bump.

### DMS Standalone Change-Queries OpenAPI

- DMS serves the standalone Change-Queries OpenAPI document only from the selected core schema's `projectSchema.openApiBaseDocuments.changeQueries`.
- DMS ignores extension-side `projectSchema.openApiBaseDocuments.changeQueries` if present.
- DMS does not synthesize the standalone Change-Queries OpenAPI document.
- If the selected core ApiSchema does not contain `projectSchema.openApiBaseDocuments.changeQueries`, DMS startup and schema validation still succeed.
- If the selected core document is absent, `/metadata/specifications` omits `Change-Queries`, `/metadata/changequeries/v1/swagger.json` returns `404`, and any DMS-convention alias such as `/metadata/specifications/changequeries-spec.json` returns `404`.
- DMS exposes the canonical Change-Queries metadata URL at `/metadata/changequeries/v1/swagger.json`.
- DMS may also expose the DMS-convention alias `/metadata/specifications/changequeries-spec.json`; if present, it serves the same document as the canonical URL.
- `/metadata/specifications` conditionally lists a `Change-Queries` entry with prefix `Other` and `endpointUri` pointing to `/metadata/changequeries/v1/swagger.json`.
- DMS replaces the served Change-Queries document's `servers` array with the actual route-qualified runtime base ending in `/changeQueries/v1`, not `/data`.
- Change-Queries server URL generation preserves the same tenant and route-qualifier variable behavior used by resources/descriptors.
- DMS injects the same OpenAPI 3 OAuth2 `components.securitySchemes.oauth2_client_credentials` and root `security` requirement into the served Change-Queries document that it injects into resources and descriptors documents.

### DMS Resource, Descriptor, and Profile OpenAPI

- DMS metadata endpoints serve the MetaEd-emitted live GET-many `minChangeVersion` and `maxChangeVersion` parameters in resource and descriptor OpenAPI documents.
- DMS metadata endpoints serve the MetaEd-emitted `/deletes` and `/keyChanges` paths in resource and descriptor OpenAPI documents for core and extension-defined resources/descriptors.
- DMS profile OpenAPI documents preserve live GET-many `minChangeVersion` and `maxChangeVersion` for readable profiled resources.
- DMS profile OpenAPI documents preserve `/deletes` and `/keyChanges` paths for readable profiled resources.
- The standalone `Change-Queries` document for `/availableChangeVersions` remains unprofiled and is not duplicated under profile metadata.
- Profile-specific `/deletes` and `/keyChanges` responses remain normal `application/json` Change Query responses using identity-key schemas.
- DMS profile OpenAPI filtering does not apply normal readable-resource property filtering to tracked-change key schemas.

### DMS Discovery

- Root Discovery API responses include `urls.changeQueries`.
- `urls.changeQueries` ends in `/changeQueries/v1/`.
- `urls.changeQueries` uses the same tenant and route-qualifier prefix behavior as `urls.dataManagementApi`.
- The Discovery API URL is emitted independently of the standalone OpenAPI document's presence because runtime Change Queries are not sourced from OpenAPI.

### Tests

- Tests cover ApiSchema validation and loading with optional `projectSchema.openApiBaseDocuments.changeQueries`.
- Tests cover preserving the raw Change-Queries OpenAPI document while stripping it from normalized/effective schema inputs.
- Tests prove OpenAPI payload changes, including adding/removing `changeQueries`, do not affect the effective-schema hash.
- Tests cover serving the canonical Change-Queries metadata URL and any DMS-convention alias.
- Tests cover missing core Change-Queries document behavior: successful startup, omitted catalog entry, and `404` metadata URLs.
- Tests cover ignoring extension-side standalone `openApiBaseDocuments.changeQueries`.
- Tests cover Change-Queries server URL rewriting, including multi-tenancy and route-qualifier variables.
- Tests cover OAuth2 security injection for the standalone Change-Queries document.
- Tests cover `/metadata/specifications` listing `Change-Queries` under prefix `Other` when the core document is present.
- Tests cover root Discovery API `urls.changeQueries`.
- Tests cover profile OpenAPI preservation of readable-resource `/deletes` and `/keyChanges` without profile-filtering tracked-change key schemas.

## DMS Implementation Notes

- `src/dms/core/EdFi.DataManagementService.Core/ApiSchema/JsonSchemaForApiSchema.json` already appears to accept optional `openApiBaseDocuments.changeQueries`.
- `src/dms/core/EdFi.DataManagementService.Core/Startup/ApiSchemaInputNormalizer.cs` already strips `projectSchema.openApiBaseDocuments`; keep that behavior so `changeQueries` remains hash-neutral.
- `src/dms/core/EdFi.DataManagementService.Core/OpenApi/OpenApiDocument.cs` and the API service layer currently handle resources/descriptors only; add a Change-Queries OpenAPI path sourced only from the selected core `projectSchema.openApiBaseDocuments.changeQueries`.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/MetadataEndpointModule.cs` currently lists `Resources`, `Descriptors`, and `Discovery`; add conditional `Change-Queries` metadata exposure under prefix `Other`.
- The shared server builder in `MetadataEndpointModule` currently appends `/data`; Change-Queries needs a route-base option ending in `/changeQueries/v1`.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/DiscoveryEndpointModule.cs` currently omits `urls.changeQueries`; add it with the route-qualified `/changeQueries/v1/` base.

## Boundary Notes

- Keep runtime route-resolution points as context, not implementation acceptance criteria for this story.
- Runtime `GET /changeQueries/v1/availableChangeVersions` behavior belongs to `21-available-change-versions-endpoint.md`.
- Runtime resource and descriptor `/deletes` behavior belongs to `23-deletes-endpoint.md`.
- Runtime resource and descriptor `/keyChanges` behavior belongs to `24-keychanges-endpoint.md`.
- Removing any temporary frontend stub for runtime `/deletes` and `/keyChanges` responses has moved to a separate runtime cleanup ticket.

## Out of Scope

- Implementing Change Query runtime endpoint behavior.
- Using OpenAPI as the runtime route source of truth.
- Snapshot runtime support or advertising snapshot behavior.
- Runtime feature-flagging for Change Queries.
- Synthesizing Change-Queries OpenAPI in DMS when the core ApiSchema document is absent.

## Clarifying Questions and Answers

### Questions 1

1. Should the DMS-convention alias `/metadata/specifications/changequeries-spec.json` be required in this story, or should implementation and tests target only the canonical `/metadata/changequeries/v1/swagger.json` route?
2. If a core ApiSchema contains `projectSchema.openApiBaseDocuments.changeQueries` but that document is structurally incomplete or lacks the `/availableChangeVersions` path, should DMS serve it with only `servers`/security injection, reject the ApiSchema at validation/startup, or treat it as absent for catalog/404 behavior?
3. For profile OpenAPI filtering, should Change Query `/deletes` and `/keyChanges` paths be associated to a profile by the base resource/descriptor endpoint path rather than by the tracked-change response schema name, with tracked-change response schemas and `application/json` content left unchanged?
4. Should `DomainsExcludedFromOpenApi` filtering apply to MetaEd-emitted `/deletes` and `/keyChanges` paths, including profile OpenAPI copies, the same way it applies to live resource and descriptor paths?

### Answers 1

1. Target only the canonical `/metadata/changequeries/v1/swagger.json` route in this story. Do not require or test `/metadata/specifications/changequeries-spec.json`; if that alias is added later, it should be a separate compatibility task and serve the same document as the canonical route.
2. If `projectSchema.openApiBaseDocuments.changeQueries` is present and passes the existing ApiSchema JSON schema validation, DMS should treat it as present and serve it with only the standard `servers` and security injection. Do not add DMS validation for the `/availableChangeVersions` path, do not reject startup for a pathless-but-schema-valid document, and do not treat it as absent. A missing or malformed path is a MetaEd contract defect, not a DMS runtime route decision.
3. Yes. Profile OpenAPI filtering should associate `/deletes` and `/keyChanges` with the profiled base resource or descriptor path, not with the tracked-change response schema name. Include those GET paths when the base resource/descriptor is readable in the profile, and leave the tracked-change response schemas and `application/json` content unchanged rather than creating profile-specific readable schemas for them.
4. Yes. Apply `DomainsExcludedFromOpenApi` to MetaEd-emitted `/deletes` and `/keyChanges` paths using the same path-level `x-Ed-Fi-domains` rule as live resource and descriptor paths, including the existing behavior for paths with multiple domains. Profile OpenAPI documents should inherit that filtered path set so excluded-domain change-query paths are not reintroduced under profiles.
