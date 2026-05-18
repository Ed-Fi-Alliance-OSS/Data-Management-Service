---
jira: DMS-1056
jira_url: https://edfi.atlassian.net/browse/DMS-1056
---

# Slice 1: Relationship CRUD Auth Core

## Purpose

Create the operation-neutral relationship authorization core needed by single-record GET-by-id, POST, PUT, and DELETE operations before any endpoint path starts executing relationship authorization checks.

This slice generalizes the DMS-1055 GET-many classifier, subject-resolution, and parameterization work so later slices can consume reusable authorization specs instead of copying page-query-specific code.

## In Scope

- Refactor relationship strategy classification and subject resolution out of GET-many-only names and contracts.
- Preserve DMS-1055 behavior for `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted`.
- Produce operation-neutral authorization specs for:
  - stored-value checks against an existing root document,
  - proposed-value checks against request-body/root-row values, and
  - strategy OR composition with per-strategy metadata.
- Generate reusable SQL-fragment inputs for single-record `EXISTS` checks without executing endpoint operations.
- Reuse the DMS-1055 `ClaimEducationOrganizationIds` parameter contract for PostgreSQL and SQL Server.
- Carry structured failure metadata for strategy index, readable securable names, securable element paths, auth view/table names, and failure hints.
- Return security-configuration failures when a configured relationship strategy has no applicable authorization subjects.

## Explicitly Out Of Scope

- Enabling relationship authorization on GET-by-id, POST, PUT, or DELETE endpoints.
- People-involved relationship subject resolution; Slice 5 owns that core.
- Exact RFC 9457 ProblemDetails formatting; Slice 6 hardens the final response shape.
- New auth database objects or DDL.
- Caching generated operation-specific SQL.

## Design Constraints

- The core must not be tied to `PageDocumentId`, `RelationalGetMany`, page/count SQL, or root page alias naming.
- EdOrg-only CRUD subject scope must match DMS-1055: only concrete root-table EdOrg authorization subjects participate.
- Child-table EdOrg securable paths may remain resolvable/indexed metadata, but this slice must not turn them into CRUD authorization subjects.
- Normal EdOrg hierarchy filtering uses token EdOrg IDs against `SourceEducationOrganizationId` and resource EdOrg values against `TargetEducationOrganizationId`.
- Inverted EdOrg hierarchy filtering swaps those roles.
- Multiple EdOrg subjects inside one relationship strategy remain ANDed.
- Multiple relationship strategies remain ORed and keep configured index order.
- Known relationship strategies that are not implemented by this slice must be classified as known-but-not-enabled rather than unknown security metadata.

## Core Contracts

### Strategy classification

The classifier should distinguish:

- supported EdOrg-only CRUD core strategies:
  - `RelationshipsWithEdOrgsOnly`
  - `RelationshipsWithEdOrgsOnlyInverted`
- known People relationship strategies owned by Slice 5,
- known no-op strategy `NoFurtherAuthorizationRequired`, and
- unknown or invalid security metadata.

### Subject specs

An EdOrg relationship subject spec should carry:

- resource full name,
- authorization strategy name,
- configured strategy index,
- securable element kind,
- original JSON path,
- readable/MetaEd securable element name,
- resolved concrete root-table column binding,
- source/target hierarchy direction, and
- failure metadata needed by later ProblemDetails mapping.

### Check specs

The core should expose check specs that downstream operation slices can place into their own batches:

- stored-value check: root document alias/DocumentId plus root-table EdOrg column bindings,
- proposed-value check: proposed parameter names and values derived from the request/root-row buffer,
- SQL dialect selection inputs, and
- deterministic parameter binding metadata.

## Acceptance Criteria

- DMS-1055 relationship strategy classification is reusable outside GET-many without retaining page-query-specific names in the core contract.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` produce operation-neutral EdOrg CRUD auth specs.
- Stored-value and proposed-value checks use the same strategy metadata and parameterization contract.
- Inverted EdOrg behavior is explicit in the spec and can be consumed by SQL generation without branching on raw strategy strings downstream.
- Multiple strategies preserve OR composition metadata, configured index ordering, and readable strategy identity.
- Multiple EdOrg subjects in one strategy preserve AND composition metadata.
- A relationship strategy with no applicable concrete root-table EdOrg subject returns a security-configuration failure with resource, strategy, and securable element details.
- PostgreSQL binding uses one `ClaimEducationOrganizationIds` bigint array parameter.
- SQL Server binding uses deterministic expanded scalar bigint parameters below 2,000 unique EdOrg IDs and `dms.BigIntTable` at 2,000 or more unique EdOrg IDs.
- Token EdOrg IDs are deduplicated and sorted before threshold selection and binding metadata generation.
- Generated operation-specific SQL is not cached; reusable metadata may be cached by effective schema/mapping set/resource/strategy/securable element.

## Tests Required

### Unit tests

- Classifies EdOrg-only, inverted EdOrg-only, People relationship, no-op, known unsupported, and unknown strategy names correctly.
- Resolves only concrete root-table EdOrg subjects for CRUD auth specs.
- Rejects strategies with only child-table EdOrg paths as security-configuration failures.
- Preserves strategy OR index ordering and subject AND ordering.
- Emits normal and inverted Source/Target direction metadata.
- Produces stored-value and proposed-value check specs from the same strategy model.
- Produces deterministic PostgreSQL, SQL Server scalar, and SQL Server TVP parameter metadata.

### Integration tests

No endpoint or database integration tests are required for this slice. Later slices own operation execution and provider roundtrips.

## Reviewer Focus

Reviewers should focus on the contract boundary: later operation code should be able to ask for relationship auth specs without knowing whether the original consumer was GET-many or CRUD.
