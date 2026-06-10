---
jira: DMS-1183
jira_url: https://edfi.atlassian.net/browse/DMS-1183
---

# Story: Extend MetaEd and DMS OpenAPI for Change Queries

## Description

Extend the OpenAPI surface so Change Query routes and query parameters are advertised consistently.

MetaEd owns emission of the OpenAPI definitions. DMS owns consuming the updated OpenAPI metadata for discovery and documentation, while runtime resource Change Query routing is resolved from DMS's effective resource model rather than from OpenAPI paths.

`/changeQueries/v1/availableChangeVersions` is a fixed DMS runtime route. It is advertised in OpenAPI, but it is not generated from `ApiSchema.json` resource metadata.

This ticket is intentionally cross-project because the API contract is split across MetaEd generation and DMS runtime consumption.

## Acceptance Criteria

- MetaEd emits `/deletes` endpoint definitions for each resource and descriptor that supports `ReadChanges`.
- MetaEd emits `/keyChanges` endpoint definitions for each resource and descriptor that supports `ReadChanges`.
- MetaEd emits `/changeQueries/v1/availableChangeVersions`.
- MetaEd adds `minChangeVersion` and `maxChangeVersion` query parameters to live resource and descriptor GET-many endpoint definitions.
- MetaEd adds `minChangeVersion`, `maxChangeVersion`, `limit`, `offset`, and `totalCount` query parameters to `/deletes` and `/keyChanges` definitions.
- Response schemas for `/deletes`, `/keyChanges`, and `/availableChangeVersions` match the ODS-compatible Change Queries contract.
- DMS effective-schema loading accepts and preserves the new OpenAPI paths and query parameters.
- DMS metadata endpoints can serve the updated OpenAPI definitions for discovery and documentation.
- DMS runtime route resolution for resource and descriptor `/deletes` and `/keyChanges` remains model-driven from the effective `ApiSchema.json` endpoint mappings and RelationalBackend `MappingSet.Model` / `ConcreteResourceModel` inventory.
- DMS runtime route resolution for `/changeQueries/v1/availableChangeVersions` is hardcoded and does not depend on `ApiSchema.json` or OpenAPI path presence.
- DMS does not require a separate hard-coded route list for resource and descriptor `/deletes` or `/keyChanges` endpoints.
- Tests cover OpenAPI generation for core resources, including `SchoolYearType` as a regular Change Query resource, extension-defined resources, and descriptors.
- DMS tests cover startup/loading of the updated OpenAPI and verify that advertised Change Query resource and descriptor paths align with model-driven route classification.

## Out of Scope

- Implementing the endpoint runtime behavior.
- Snapshot OpenAPI support.
- Runtime feature-flagging for Change Queries.

## Clarifying Questions and Answers

### Questions 1

1. Should `availableChangeVersions` be emitted as a new Change-Queries OpenAPI document served as an `Other` metadata section, or inserted into an existing resources/descriptors OpenAPI document; and should its path be ODS-style `/availableChangeVersions` within that document or the full runtime path `/changeQueries/v1/availableChangeVersions`?
2. What is MetaEd's source of truth for deciding that a resource or descriptor supports `ReadChanges`, given current ApiSchema/OpenAPI generation does not appear to carry claim/action metadata?
3. Should Story 20 include `SchoolYearType` in generated `/deletes` and `/keyChanges` OpenAPI paths, matching the current DMS design that treats it as a regular Change Query resource?
4. Since Snapshot OpenAPI support is out of scope, should new Change Query OpenAPI definitions omit the ODS `Use-Snapshot` header and snapshot-specific response entries even though the legacy ODS metadata includes them?
5. For `/deletes` and `/keyChanges` response schemas, should MetaEd match legacy ODS component names and schema details exactly, or is compatibility limited to the JSON response body shape under DMS's existing OpenAPI component naming conventions?
6. What DMS metadata catalog entry and URL should expose the Change-Queries OpenAPI document: an ODS-compatible `Change-Queries` entry at `/metadata/changequeries/v1/swagger.json`, the existing DMS `/metadata/specifications/{section}-spec.json` convention, or both?

### Answers 1

1. Emit `availableChangeVersions` as its own Change Queries OpenAPI document, listed under the `Other` metadata prefix. Inside that document, keep the ODS-style path key `/availableChangeVersions`; the document server/base URL should resolve to the DMS runtime route base `/changeQueries/v1`, producing the effective route `/changeQueries/v1/availableChangeVersions`. Do not insert this endpoint into the resources or descriptors documents.
2. For OpenAPI emission, treat Change Query support as a generated-resource capability, not as claim/action metadata. MetaEd should advertise `/deletes` and `/keyChanges` for every non-composite generated resource and descriptor in the resources/descriptors OpenAPI documents. DMS runtime authorization remains responsible for enforcing whether the caller has the `ReadChanges` action and strategies for the resolved resource.
3. Yes. Include `SchoolYearType` with the rest of the non-composite generated resources. Do not add `SchoolYearType`-specific OpenAPI or runtime exclusions; if its `/keyChanges` result is empty because its identity is immutable, that follows normal key-change semantics.
4. Omit the ODS `Use-Snapshot` header and snapshot-specific response entries from new DMS Change Query OpenAPI definitions, including live GET-many, `/deletes`, `/keyChanges`, and `/availableChangeVersions`. Snapshot support is deferred for DMS v1.0, and the OpenAPI should not advertise snapshot behavior that DMS only ignores at runtime. This requires cleaning up or suppressing existing MetaEd snapshot artifacts in DMS OpenAPI output as part of this story, not only avoiding them on newly added paths: remove or avoid the shared `NotFoundUseSnapshot` response, `Use-Snapshot` header parameter, and snapshot-specific write response descriptions from the affected resources, descriptors, and Change-Queries documents. Snapshot OpenAPI behavior should be revisited by the deferred snapshot story.
5. Match the ODS-compatible JSON response body contract, not the exact legacy ODS component names. `/deletes` responses should expose `id`, `changeVersion`, and `keyValues`; `/keyChanges` responses should expose `id`, `changeVersion`, `oldKeyValues`, and `newKeyValues`; key fields should use the public query-field names from `queryFieldMapping`. Component names may follow DMS's existing OpenAPI naming conventions.
6. Use both discovery surfaces with one canonical catalog target: add a DMS `/metadata/specifications` entry named `Change-Queries` with prefix `Other`, and set its `endpointUri` to the ODS-compatible URL `/metadata/changequeries/v1/swagger.json`. Also serve a DMS-convention alias at `/metadata/specifications/changequeries-spec.json` if needed by the existing section loader, but the catalog entry should point to the ODS-compatible URL.

### Questions 2

1. What exact `ApiSchema.json` contract should carry the standalone Change Queries OpenAPI document: add a new `projectSchema.openApiBaseDocuments` key accepted by DMS schema validation, emit a schema-adjacent static OpenAPI asset outside `ApiSchema.json`, or have DMS synthesize the document; and if it is in `ApiSchema.json`, what key spelling and `apiSchemaVersion` behavior are required?
2. Should the standalone Change Queries OpenAPI document be emitted only by the core/data-standard ApiSchema package, or also by extension ApiSchema packages; and if extension packages should not carry it, should DMS ignore extension-side Change Queries documents if present?
3. For `/deletes` and `/keyChanges` response components, should MetaEd generate strongly typed per-resource key schemas containing only identity fields derived from `identityJsonPaths` and `queryFieldMapping`, or is a generic key-values object schema acceptable when the runtime response body still returns the correct keys?
4. For `/deletes` and `/keyChanges` OpenAPI response schemas, should `id`, `changeVersion`, and the key object properties be marked `required`, and should `changeVersion` use the ODS legacy `type: number` shape or DMS-consistent `type: integer`, `format: int64`?
5. When DMS serves the standalone Change Queries OpenAPI document, should the `servers` URL be rewritten to the route-qualified runtime base ending in `/changeQueries/v1` rather than `/data`, including multi-tenancy and route-qualifier server variables?

### Answers 2

1. Carry the standalone document in the core ApiSchema under `projectSchema.openApiBaseDocuments.changeQueries`. MetaEd should emit that document as the base OpenAPI document for the `Change-Queries` metadata section, and DMS schema validation should accept the optional `changeQueries` key alongside the existing `resources` and `descriptors` keys. Do not synthesize the document in DMS and do not make it a schema-adjacent static asset outside `ApiSchema.json`. Because this is OpenAPI-only metadata and `openApiBaseDocuments` is stripped before effective-schema hash/model derivation, keep `apiSchemaVersion` unchanged for this story and update the DMS normalizer to strip `changeQueries` with the rest of `openApiBaseDocuments`.
2. Emit `projectSchema.openApiBaseDocuments.changeQueries` only from the core/data-standard ApiSchema package. This core-only rule applies only to the standalone `Change-Queries` document that advertises `/availableChangeVersions`; it does not exclude extension-defined top-level resources or descriptors from Change Query OpenAPI. Extension packages should continue to carry only their resource/descriptor OpenAPI fragments, and those fragments should include `/deletes`, `/keyChanges`, and live GET-many `minChangeVersion` / `maxChangeVersion` metadata for extension-defined resources and descriptors under their project endpoint. Resource extensions under `_ext` do not get separate Change Query endpoints; their changes are surfaced through the owning resource. DMS should source the Change Queries document only from the selected core ApiSchema and ignore any extension-side `openApiBaseDocuments.changeQueries` if present.
3. Generate strongly typed per-resource key schemas for `/deletes` and `/keyChanges`. For regular resources, the key schemas should contain only public identity fields derived from `identityJsonPaths` and `queryFieldMapping`, including descriptor-reference fields composed as public descriptor URI strings. Descriptor key schemas need an explicit descriptor identity rule because MetaEd currently sets descriptor `identityJsonPaths` to empty in `packages/metaed-plugin-edfi-api-schema/src/enhancer/IdentityJsonPathsEnhancer.ts`. For descriptor `/deletes` and `/keyChanges`, generate the key schema from the public descriptor identity fields `namespace` and `codeValue`, matching the DMS design and legacy ODS metadata shape. Do not expose internal descriptor IDs in descriptor key schemas. A generic `keyValues` object schema is not sufficient for the advertised discovery contract.
4. Mark `id`, `changeVersion`, and the containing key object properties (`keyValues`, `oldKeyValues`, `newKeyValues`) as required on the response item schemas, and mark each generated key schema identity property as required. Use DMS-consistent `type: integer`, `format: int64` for `changeVersion`, matching the `ChangeVersion` bigint design and the existing `minChangeVersion`, `maxChangeVersion`, and `availableChangeVersions` shapes rather than the legacy ODS `type: number` response-schema quirk.
5. Yes. When serving the standalone Change Queries document, DMS should replace the document's `servers` array with the actual route-qualified runtime base ending in `/changeQueries/v1`, not `/data`. The server builder should preserve the same multi-tenancy and route-qualifier variable behavior used by resources/descriptors, then append `changeQueries/v1` as the terminal route base.

### Questions 3

1. Should profile-specific OpenAPI documents include the new Change Query surface for profiled resources, including live GET-many `minChangeVersion` / `maxChangeVersion` and `/deletes` / `/keyChanges` paths; and if so, should `/deletes` and `/keyChanges` response schemas remain unprofiled identity-key schemas with `application/json`, or follow legacy ODS profile-suffixed tracked-change schema names?

### Answers 3

1. Yes. Profile-specific resource OpenAPI documents should include the Change Query surface for resources covered by the profile's readable content type: live GET-many operations should retain `minChangeVersion` and `maxChangeVersion`, and `/deletes` and `/keyChanges` GET paths should be present for those profiled resources. The standalone `Change-Queries` document for `/availableChangeVersions` remains unprofiled and should not be duplicated under profile metadata. For `/deletes` and `/keyChanges`, keep the response body contract as the same strongly typed identity-key Change Query JSON shape from Answers 2, but in profile-specific documents follow legacy ODS by using profile-suffixed tracked-change component schema names. These tracked-change responses should remain normal `application/json` Change Query responses, not profile-filtered resource representations. DMS profile OpenAPI filtering should therefore classify `/deletes` and `/keyChanges` by their owning resource path, preserve them for readable profile resources, create/reference the profile-suffixed tracked-change schemas, and avoid applying normal readable resource property filtering to the tracked-change key schemas.

### Questions 4

1. Should Story 20 also update the root Discovery API `urls` object to include an ODS-compatible `changeQueries` URL ending in `/changeQueries/v1/`, or is root discovery URL exposure owned by the runtime `availableChangeVersions` story or out of scope?
2. If the selected core ApiSchema does not contain `projectSchema.openApiBaseDocuments.changeQueries` after this story lands, should DMS omit the `Change-Queries` metadata catalog entry and return 404 for its metadata URLs, or should startup/schema validation fail?
3. Should DMS inject the same OAuth2 `components.securitySchemes` and root `security` metadata into the standalone `Change-Queries` OpenAPI document that it injects into resources/descriptors documents, or should MetaEd emit those security sections directly in `openApiBaseDocuments.changeQueries`?

### Answers 4

1. Yes. Story 20 should update the root Discovery API `urls` object to include `changeQueries` with the route-qualified base URL ending in `/changeQueries/v1/`, using the same tenant and route-qualifier prefix behavior as `dataManagementApi`. This is discovery/catalog exposure, so it belongs with the OpenAPI metadata story; the runtime `GET /changeQueries/v1/availableChangeVersions` handler remains owned by `21-available-change-versions-endpoint.md`. Because Change Queries are always on for DMS v1.0, the root discovery URL should be emitted unconditionally once this story lands.
2. Do not fail startup or schema validation when `projectSchema.openApiBaseDocuments.changeQueries` is absent. The key is optional OpenAPI-only metadata and remains hash-neutral. If the selected core ApiSchema lacks the standalone document, DMS should omit the `Change-Queries` entry from `/metadata/specifications` and return 404 for `/metadata/changequeries/v1/swagger.json` and any DMS-convention alias such as `/metadata/specifications/changequeries-spec.json`. Runtime Change Query routes remain governed by their own stories and do not depend on the OpenAPI document's presence.
3. DMS should inject the same OpenAPI 3 OAuth2 `components.securitySchemes.oauth2_client_credentials` and root `security` requirement into the served standalone `Change-Queries` document that it injects into resources and descriptors documents. MetaEd should not emit deployment-specific security metadata or token URLs in `openApiBaseDocuments.changeQueries`; it should emit the schema/path contract, and DMS should add or overwrite the security block at serve time from the configured authentication service so all OpenAPI documents advertise the same security scheme.
