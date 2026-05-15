# DMS-1025 Descriptor Runtime Integration Coverage Design

## Source Ticket

- Jira: `DMS-1025`
- Design document: `reference/design/backend-redesign/epics/13-test-migration/04-descriptor-tests.md`
- Related dependency: `DMS-955` owns `ddl provision --seed-descriptors`; DMS-1025 does not implement descriptor seeding.

## Goal

Add focused API integration coverage for descriptor runtime behavior in the relational backend, with equivalent PostgreSQL and SQL Server assertions through the real DMS HTTP pipeline.

## Scope

In scope:

- Descriptor POST creates a retrievable descriptor resource.
- Descriptor PUT updates non-identity fields and advances runtime metadata.
- Descriptor PUT with unchanged non-identity fields succeeds as a no-op and preserves metadata.
- Descriptor PUT rejects identity changes to `namespace` or `codeValue`.
- Descriptor GET-many supports descriptor key-field filtering and deterministic paging.
- Resource writes fail when a required descriptor reference is missing and succeed after the descriptor is created through the API.

Out of scope:

- Implementing `ddl provision --seed-descriptors`.
- Adding descriptor seed parsing or provisioning behavior.
- Expanding Docker E2E coverage for this ticket.
- Authorization end-to-end behavior; the API integration harness continues to use fakes for external auth dependencies.

Descriptor seeding coverage remains deferred until `DMS-955` provides the CLI capability. If `--seed-descriptors` is not present in the branch, DMS-1025 should record the seeding acceptance item as blocked by `DMS-955`.

## Architecture

Use the existing API integration test pattern:

- Add an integration-owned fixture `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime`.
- Add `FixtureKey.DescriptorRuntime` and map it in `FixtureRepositoryPaths`.
- Add one scenario class in `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DescriptorRuntimeScenario.cs`.
- Add thin per-dialect wrappers in `Tests/Postgresql` and `Tests/Mssql`.

The wrapper classes only bind `FixtureKey.DescriptorRuntime` and expose one `[Test]` per scenario method. All request construction and assertions live in `DescriptorRuntimeScenario`.

## Fixture Design

The `descriptor-runtime` fixture should be derived from the existing `profile-root-only-merge` shape, but owned under `Backend.IntegrationFixtures` so descriptor-specific runtime needs do not disturb existing DDL golden fixtures.

Keep these resources:

- `Student`
- `SchoolTypeDescriptor`
- `ProfileRootOnlyMergeItem`

Expand `SchoolTypeDescriptor` to include:

- `namespace`
- `codeValue`
- `shortDescription`
- `description`
- `effectiveBeginDate`
- `effectiveEndDate`

Descriptor GET-many depends on the shared descriptor table query contract compiled by `DescriptorQueryCapabilityCompiler`. The descriptor fixture must provide exactly these seven `queryFieldMapping` fields with matching paths and types:

- `id` -> `$.id`, `string`
- `namespace` -> `$.namespace`, `string`
- `codeValue` -> `$.codeValue`, `string`
- `shortDescription` -> `$.shortDescription`, `string`
- `description` -> `$.description`, `string`
- `effectiveBeginDate` -> `$.effectiveBeginDate`, `date`
- `effectiveEndDate` -> `$.effectiveEndDate`, `date`

A partial mapping silently omits descriptor GET-many support for the resource, so the fixture must match the compiler contract exactly.

`ProfileRootOnlyMergeItem` should keep descriptor references to `SchoolTypeDescriptor`. Because it has an equality constraint between `primarySchoolTypeDescriptor` and `secondarySchoolTypeDescriptor`, tests that set both values must use the same descriptor URI.

## Scenarios

### Create And Read

POST to `/data/ed-fi/schoolTypeDescriptors` with all descriptor fields. Assert:

- response is `201 Created`,
- `Location` points to `/data/ed-fi/schoolTypeDescriptors/{id}`,
- response includes an `ETag`,
- GET by location returns the request fields plus `id`, `_etag`, and `_lastModifiedDate`.

### Update Advances Metadata

Create a descriptor, read its initial `_etag`, `_lastModifiedDate`, and database metadata from `dms.Document`.

PUT to the descriptor location with the same identity fields and changed non-identity fields. Assert:

- response is `204 NoContent`,
- PUT emits a new `ETag`,
- GET returns changed `shortDescription`, `description`, `effectiveBeginDate`, and `effectiveEndDate`,
- `_etag` changes,
- `dms.Document.ContentVersion` advances,
- `dms.Document.ContentLastModifiedAt` advances or changes.

### No-Op PUT Preserves Metadata

Create a descriptor, capture GET `_etag`, GET `_lastModifiedDate`, `dms.Document.ContentVersion`, and `dms.Document.ContentLastModifiedAt`.

PUT the same representation back with `If-Match`. Assert:

- response is `204 NoContent`,
- GET `_etag` is unchanged,
- GET `_lastModifiedDate` is unchanged,
- `ContentVersion` is unchanged,
- `ContentLastModifiedAt` is unchanged.

### Identity Change Rejected

Create a descriptor, then PUT to its location with a changed `namespace` or `codeValue`. Assert:

- response is `400 BadRequest`,
- body type is `urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported`,
- body title is `Key Change Not Supported`.

### Query Filtering And Paging

Create three descriptors in a namespace unique to the scenario. Assert:

- filtering by `namespace` returns only the three scenario-owned descriptors,
- filtering by `namespace` and `codeValue` returns one expected descriptor,
- paging the namespace-filtered result with `limit` and `offset` partitions the three descriptor ids without overlap.

If the test asserts order, it should describe the current runtime behavior as `DocumentId ASC`, not as a generic creation-order guarantee.

### Descriptor Reference Resolution

POST a `ProfileRootOnlyMergeItem` with `primarySchoolTypeDescriptor` and `secondarySchoolTypeDescriptor` pointing to the same missing descriptor URI. Assert:

- response is `400 BadRequest`,
- body type is `urn:ed-fi:api:bad-request`,
- `validationErrors` mentions the descriptor path and missing descriptor value.

Then create the required descriptor through `/data/ed-fi/schoolTypeDescriptors` and repeat the same `ProfileRootOnlyMergeItem` write. Assert the write succeeds with `201 Created` or `200 OK`.

## Error Handling

Descriptor-reference failures should follow the current descriptor validation path, not the non-descriptor unresolved-reference path. The scenario should assert `400` and descriptor-specific `validationErrors`, not `409` or `urn:ed-fi:api:data-conflict:unresolved-reference`.

No-op PUT metadata verification requires direct database assertions because the HTTP response alone may not prove that `ChangeVersion` and persisted last-modified metadata were preserved.

## Testing

Targeted verification should include:

- `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "FullyQualifiedName~DescriptorRuntime"`
- PostgreSQL-only category filter when only PostgreSQL is configured.
- SQL Server-only category filter when SQL Server is configured.

The tests should skip cleanly through the existing dialect base classes when required connection strings are not configured.

Run `dotnet csharpier format` on touched C# files after implementation.

## Acceptance Mapping

- Descriptor POST and GET: `It_creates_and_reads_a_descriptor`.
- Descriptor PUT changed fields and metadata advancement: `It_updates_descriptor_non_identity_fields_and_advances_metadata`.
- Descriptor PUT no-op preservation: `It_preserves_metadata_for_unchanged_descriptor_put`.
- Descriptor identity immutability: `It_rejects_descriptor_identity_changes`.
- Descriptor query filtering and deterministic paging: `It_filters_and_pages_descriptor_queries`.
- Descriptor reference failure and success after API creation: `It_requires_descriptor_reference_resolution_before_resource_write`.
- PostgreSQL and SQL Server parity: thin wrappers bind the same scenario methods to each dialect base.
- Provisioning-time descriptor seeding: deferred until `DMS-955` provides `--seed-descriptors`.
