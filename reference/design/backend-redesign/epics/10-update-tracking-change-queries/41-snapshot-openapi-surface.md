---
jira: TBD
jira_url: TBD
---

# Story: Add the Snapshot Contract to Served OpenAPI Documents

## Description

Restore the snapshot OpenAPI surface intentionally deferred from DMS v1.0.

The contract has two halves in two different repositories. The `Use-Snapshot` parameter and the snapshot response components are authored in MetaEd and reach DMS only inside published ApiSchema packages; DMS then assembles and serves documents that must define every component they reference. This story owns the DMS half. It cannot be delivered by hand-editing OpenAPI in this repository, and it must not be implemented by changing backend authoritative fixture inputs that do not feed the served documents.

Three served documents are package-authored: resources, descriptors, and the standalone Change Queries document. Profile documents are not. DMS derives each served profile document by filtering a clone of the assembled resource document through `ProfileOpenApiSpecificationFilter`, so there is no profile base document for MetaEd to author. Everything the profile documents must serve is therefore this story's responsibility, produced from upstream resource content rather than requested from upstream.

This split follows the precedent in `20-openapi-change-query-surface.md` (DMS-1183), which separated the already-delivered MetaEd contract from the DMS continuation that consumed it.

Per `EPIC.md` § Follow-on Stories (spawned by DMS-1190), this story and its upstream publication are the release gate for the runtime changes delivered by Stories 39 and 40. That is a release-level rule, not a dependency of Story 42: Publisher validation and documentation may close independently, but those runtime changes must not ship while the served OpenAPI contract is stale.

## Upstream Prerequisite

The following is MetaEd/ApiSchema work in the upstream repository, not DMS implementation work in this story. It is the input contract this story consumes.

- MetaEd defines a reusable boolean `Use-Snapshot` header parameter with default `false`.
- MetaEd references the parameter from resource and descriptor GET-many and GET-by-id operations, from resource and descriptor `/deletes` and `/keyChanges`, and from `/changeQueries/v1/availableChangeVersions`. The profile-shaped operations that `20-openapi-change-query-surface.md` requires profile documents to preserve are not authored upstream; they inherit the parameter from the resource document through DMS profile filtering, and preserving it there is this story's work under § Served documents.
- MetaEd documents Snapshot Not Found `404` on snapshot-eligible GET operations, and the snapshot-specific `405` with its exact ProblemDetails schema, `application/problem+json`, and `Allow: GET` response header on resource and descriptor `POST`, `PUT`, and `DELETE`.
- MetaEd emits the reusable parameter and the snapshot response components into the `components` block of every independently served base document that references them: resources, descriptors, and the standalone `projectSchema.openApiBaseDocuments.changeQueries` document. The Change Queries document ships today with empty `parameters` and `responses` collections and no `$ref` of any kind, so it must be populated rather than assumed to inherit. `openApiBaseDocuments` has no `profiles` key, so no profile components are requested upstream.
- MetaEd advertises the header on GET-many as well as GET-by-id, deliberately diverging from the older ODS-derived fixture shape that advertises it only for by-id.

### Dependency mechanics

- DMS consumes seven ApiSchema NuGet packages: `EdFi.DataStandard52.ApiSchema`, `EdFi.DataStandard52.TPDM.ApiSchema`, `EdFi.DataStandard52.Homograph.ApiSchema`, `EdFi.DataStandard52.Sample.ApiSchema`, `EdFi.DataStandard61.ApiSchema`, `EdFi.DataStandard61.Homograph.ApiSchema`, and `EdFi.DataStandard61.Sample.ApiSchema`. Data Standard 6.1 folds TPDM into core, so there is no `EdFi.DataStandard61.TPDM.ApiSchema` package and none is expected; 6.1 is Core plus Sample plus Homograph. There is no in-repository source for the served OpenAPI documents.
- Those seven do not arrive by one mechanism, and this story must update both paths:
  - **Bundled path.** The frontend project takes direct `PackageReference`s on `EdFi.DataStandard52.ApiSchema` and `EdFi.DataStandard52.TPDM.ApiSchema` with `GeneratePathProperty="true"`; their versions resolve from `src/Directory.Packages.props` and their resolution is recorded in that project's `packages.lock.json`. `src/dms/Directory.Build.targets` declares bundled entries for four Data Standard 5.2 packages but gates each on the generated path property, so only packages carrying a direct reference are materialized, and it declares no Data Standard 6.1 entries.
  - **File-based path.** `SCHEMA_PACKAGES` and the bootstrap schema catalog select and download packages at deployment time. The pins live in the tracked `eng/docker-compose/` environment overlays and in the bootstrap catalog's core package identity and fallback version, independent of `src/Directory.Packages.props`. This is how Sample, Homograph, and all of Data Standard 6.1 reach a running DMS, and it can serve the full set rather than only the families the bundled path omits.
- A central version declaration is therefore not by itself a served-package update: five of the seven have no direct `PackageReference` consumer today, so bumping only `src/Directory.Packages.props` would leave the file-based pins on their previous versions and those families would keep serving OpenAPI without the snapshot contract. **No new `PackageReference` is added merely to make a single-mechanism claim true** — the two paths are existing runtime topology and changing it is outside this story.
- This story is therefore blocked until the upstream change is merged and published, and its first DMS commit updates the version-selection surfaces for the packages being adopted, across both intake paths, rather than a single central-version edit.
- The upstream change must be tracked as its own MetaEd ticket, created and linked before this story is scheduled. This story is not "done" on DMS-side assembly code alone; it is done when DMS serves the snapshot contract from published packages.
- If the upstream ticket cannot be scheduled and published in the same release, this DMS story does not start. DMS assembly-and-reference-resolution work is not landed against a hand-authored or backend fixture ahead of the package bump. If preparatory DMS work is needed before publication, create a separate explicitly scoped story that identifies an upstream-produced prerelease ApiSchema artifact; that preparatory story cannot satisfy any served-surface acceptance criterion below.
- The bump is expected to be hash-neutral. DMS-1183 established that `projectSchema.openApiBaseDocuments` is stripped before effective-schema hashing, model derivation, DDL generation, and mapping-pack selection, so an OpenAPI-only package change should not alter the effective-schema hash, require an `apiSchemaVersion` bump, or churn DDL and plan goldens. This story verifies that expectation rather than assuming it; a hash change means the upstream package carried more than the OpenAPI contract and must be investigated before the bump is accepted.

## Acceptance Criteria

### Package intake

- Release evidence confirms that the runtime changes delivered by Stories 39 and 40 are held from shipment until this story serves the matching contract from published upstream packages; a hand-authored document, backend fixture, prerelease-only artifact, or planned later package bump does not open the gate.
- Every active ApiSchema version-selection surface is updated to published package versions containing the snapshot OpenAPI contract, for every one of the seven supported package families. That means the bundled path's `src/Directory.Packages.props` versions and the affected `packages.lock.json`, **and** the file-based path's tracked `SCHEMA_PACKAGES` pins in the `eng/docker-compose/` environment overlays together with the bootstrap schema catalog's core package identity and fallback version. Updating central versions alone does not satisfy this criterion, because five of the seven families have no direct `PackageReference` consumer and are served only through the file-based path.
- Associated version assertions and documentation are updated in the same change, including the bootstrap Pester suites and `eng/docker-compose/README.md`, so the bump does not land as unexplained test breakage.
- No new `PackageReference` is added merely to route a family through the bundled path. The existing bundled-versus-file-based topology is preserved.
- The bump does not change the effective-schema hash and requires no `apiSchemaVersion` bump. Existing DDL, plan, and mapping-set goldens are unchanged by it.
- No served OpenAPI content is authored in this repository. The snapshot parameter and response components originate only in the packages; in the resource, descriptor, and Change Queries documents they are served as supplied, and in the profile documents they are carried through by DMS filtering of the resource document rather than authored here.
- Backend authoritative fixture inputs used for DDL and plan compilation are not edited to affect served OpenAPI.

### Served documents

- Resource, descriptor, and profile GET-many and GET-by-id operations serve the `Use-Snapshot` parameter.
- Resource and descriptor `/deletes` and `/keyChanges` operations serve the parameter.
- Profile `/deletes` and `/keyChanges` operations serve the parameter and the Snapshot Not Found `404`, matching their unprofiled counterparts. `20-openapi-change-query-surface.md` requires profile documents to preserve those paths for readable profiled resources, and `29-snapshot-support.md` routes them through the same snapshot-eligible tracked-changes pipeline, so omitting them here would advertise a contract narrower than the runtime's.
- `ProfileOpenApiSpecificationFilter` preserves the snapshot parameter and response `$ref`s on every profiled operation that survives filtering, and every served profile document remains self-resolving. This is delivered work rather than an assumed side effect. The filter prunes two component collections and never prunes a third, and the resulting failure modes run in opposite directions:
  - `RemoveUnusedParameters` deletes any `components.parameters` entry that no surviving path references, so a profiled operation that loses its `Use-Snapshot` reference also loses the component. A correct upstream package alone does not guarantee a correct profile document.
  - `RemoveUnusedSchemas` deletes any `components.schemas` entry unreachable from the surviving paths, seeding from `paths` and following `$ref` indirection through `components.responses`, `components.parameters`, and `components.requestBodies`. A snapshot response referenced by a surviving operation therefore keeps its ProblemDetails schema.
  - The filter has **no** response-pruning step; `components.responses` entries are never removed. The residual hazard is a response entry that outlives every operation referencing it and is left holding a `$ref` to a schema that schema-pruning removed. Coverage must therefore assert that retained `components.responses` entries still resolve, not that they survived — survival is unconditional and asserting it proves nothing.
- Profile filtering behaves identically for readable and writable profiles: readable profiled GET, `/deletes`, and `/keyChanges` operations keep the parameter and the `404`, and writable profiled `POST`, `PUT`, and `DELETE` operations keep the snapshot `405` with its `Allow: GET` header.
- `/changeQueries/v1/availableChangeVersions` serves the parameter.
- Snapshot-eligible GET operations serve Snapshot Not Found `404` with the exact runtime ProblemDetails contract from `40-snapshot-problem-details.md`.
- Resource and descriptor `POST`, `PUT`, and `DELETE` operations serve the snapshot-specific `405`, its exact ProblemDetails schema, `application/problem+json`, and the `Allow: GET` response header.
- The reusable parameter and response components are present in every independently served document that references them: resource, descriptor, profile, and standalone Change Queries documents.
- The standalone Change Queries document's own `components.parameters` and `components.responses` collections are populated; it does not rely on components from a sibling document.
- GET-many operations advertise the header consistently with GET-by-id.
- DMS document assembly preserves the snapshot components through the injection it already performs for `servers`, `components.securitySchemes.oauth2_client_credentials`, and the root `security` requirement, so nothing added by DMS drops or overwrites them.
- Read-replica routing adds no OpenAPI parameter or response field.

### Tests

- DMS document-assembly tests cover resource, descriptor, profile, and Change Queries documents.
- Served-document, operation-coverage, and local-reference-resolution verification runs under **both** intake modes — the bundled frontend path and the file-based `SCHEMA_PACKAGES` path — and across every supported package family, including Sample, Homograph, and Data Standard 6.1. Coverage limited to the two bundled Data Standard 5.2 packages does not satisfy this criterion, because it would pass while the majority of served documents still lacked the snapshot contract.
- Tests resolve every local `$ref` within each independently served document and fail for missing sibling-only components. This check is written so it fails on the pre-bump packages, proving it detects the gap rather than passing vacuously.
- Tests verify the parameter type/default, operation coverage, exact ProblemDetails metadata, response content type, and `Allow` header declaration.
- Operation-coverage tests enumerate the profile document's `/deletes` and `/keyChanges` paths explicitly, so a profile document that preserves those paths without the snapshot parameter fails rather than passing on the unprofiled operations alone.
- `ProfileOpenApiSpecificationFilter` tests prove the snapshot parameter and response references survive filtering on readable and writable profiled operations, including profile `/deletes` and `/keyChanges`, and that the `components.parameters` entries they resolve to are retained rather than pruned as unreferenced. Because `components.responses` is never pruned, the response-side assertion is that every retained response entry's `$ref` still resolves after `RemoveUnusedSchemas` runs — asserting that the response entry itself survived would pass unconditionally. These run against the filter directly, so a regression is attributed to filtering rather than to the packages.
- A test asserts the served snapshot response metadata matches the runtime contract in `40-snapshot-problem-details.md` exactly, so the two cannot drift independently.
- Effective-schema hash and golden-stability coverage confirms the package bump is OpenAPI-only.

## Dependencies

- An upstream MetaEd/ApiSchema ticket delivering the § Upstream Prerequisite contract for the resource, descriptor, and standalone Change Queries base documents, and published ApiSchema packages containing it. This is a hard, cross-repository dependency; see § Dependency mechanics. The profile-document work depends on it only for the resource content it filters, and is otherwise owned here.
- `40-snapshot-problem-details.md`. This is a scheduling dependency, not only a consistency note: the served response definitions must remain identical to that story's runtime contract, and the § Tests criterion requiring an assertion that served snapshot response metadata matches it exactly cannot be written before that story lands.

## Out of Scope

- Authoring the MetaEd/ApiSchema source change, which belongs to the upstream ticket.
- Runtime connection routing or response emission.
- Documenting read-replica selection as an API request option.
- Changes to backend DDL/plan fixture inputs or generated relational goldens.
