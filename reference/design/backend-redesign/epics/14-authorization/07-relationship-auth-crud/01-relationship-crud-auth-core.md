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
- Duplicate configured relationship strategies are preserved as distinct OR strategy entries with their own configured strategy indexes and failure metadata, even when they have the same strategy name/kind.
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
- Preserves duplicate configured relationship strategies as separate OR entries with distinct configured indexes, including when GET-many consumes the operation-neutral core through the page-query adapter.
- Emits normal and inverted Source/Target direction metadata.
- Produces stored-value and proposed-value check specs from the same strategy model.
- Produces deterministic PostgreSQL, SQL Server scalar, and SQL Server TVP parameter metadata.

### Integration tests

No endpoint or database integration tests are required for this slice. Later slices own operation execution and provider roundtrips.

## Reviewer Focus

Reviewers should focus on the contract boundary: later operation code should be able to ask for relationship auth specs without knowing whether the original consumer was GET-many or CRUD.

## Clarifying Questions and Answers

### Questions 1

  1. Should Slice 1 refactor GET-many to consume the new operation-neutral core immediately, or should it add the CRUD-facing core beside the current RelationalGetMany... types and migrate GET-
  many later?
  2. Are you comfortable renaming/moving the existing DMS-1055 types, such as RelationalGetManyAuthorizationStrategyClassifier, RelationalEdOrgAuthorizationSubjectSelector, and
  PageDocumentIdAuthorizationSpec, or do you prefer compatibility wrappers to reduce churn?
  3. For strategy index metadata, should indexes preserve the raw configured order including NoFurtherAuthorizationRequired, or the effective relationship-strategy order after no-op strategies
  are removed?
  4. Should duplicate configured relationship strategies be preserved as separate OR entries with distinct indexes? The current GET-many classifier deduplicates supported strategy kinds, which
  may lose order/index metadata.
  5. For the operation-neutral classifier, should Namespace, Ownership, and custom view strategies continue to be classified as known-but-not-enabled, or should Slice 1 only classify relationship
  strategy names plus no-op/unknown?
  6. For EdOrg subject resolution, should any unresolved configured EdOrg securable element be a configuration failure, or only a strategy that has no applicable concrete root-table EdOrg
  subjects after filtering out child-table paths?
  7. Should proposed-value check specs bind from flattened RootWriteRowBuffer/TableWritePlan values, from JSON paths before flattening, or should Slice 1 only define placeholders and leave actual
  proposed-value extraction to Slice 3?
  8. Should Slice 1 introduce a general relational authorization context for GET-by-id/POST/PUT/DELETE now, or keep RelationalAuthorizationContext scoped to GET-many until the vertical endpoint
  slices?
  9. Should the core emit reusable SQL fragments/check builders now, or only structured inputs that later operation slices turn into EXISTS checks and AUTH1 statements?
  10. For failure metadata, do you want a typed result model now that carries strategy, subject, path, hint, and failure kind, even if response formatting stays minimal until Slice 6?
  11. Should Slice 1 change the current middleware behavior for non-GET relationship strategies, or leave the staged 501/legacy-provider behavior untouched until Slice 2 starts endpoint
  integration?
  12. For testing, should this slice stay unit-only as written, even if GET-many is refactored to consume the new core, or should we include a small regression pass over existing GET-many
  integration tests?

### Answers 1

  1. Refactor GET-many to consume the operation-neutral core in Slice 1. Otherwise the new CRUD core and DMS-1055 code will drift immediately. Keep PageDocumentId... as the page-query adapter,
     but feed it from shared relationship auth specs.
  2. Rename/move the shared types, with thin adapters only where useful. RelationalGetManyAuthorizationStrategyClassifier and RelationalEdOrgAuthorizationSubjectSelector should become operation-
     neutral. Keep PageDocumentIdAuthorizationSpec page-specific, but build it from a shared RelationshipAuthorizationSpec.
  3. Preserve both indexes: raw configured strategy index and dense emitted check index. NoFurtherAuthorizationRequired should not emit a check, but later strategies should still retain their
     original configured index for diagnostics.
  4. Preserve duplicate relationship strategies as distinct OR entries. The current HashSet dedupe in RelationalGetManyAuthorizationStrategyClassifier loses order/index metadata, which conflicts
     with the CRUD/ProblemDetails needs.
  5. Continue classifying Namespace, Ownership, and custom view strategies as known-but-not-enabled. That preserves the DMS-1055 distinction between known staged work, custom-view convention
     matches, and invalid/unknown security metadata. The core should not generate specs for them in this slice.
  6. Treat truly unresolved configured EdOrg securable elements as security configuration failures. Child-table-only resolved paths should be ignored for this EdOrg CRUD core, unless all
     configured EdOrg subjects are child-only, in which case the strategy has no applicable concrete root-table subject and should fail as configuration.
  7. Proposed-value specs should be column/root-row based, not raw JSON based. Carry JSON path/readable-name metadata for errors, but actual values should come from the flattened write row after
     reference/descriptor resolution. Slice 1 can define the binding shape; Slice 3 should own extraction from write buffers.
  8. Make RelationalAuthorizationContext operation-neutral now. It already contains deduped/sorted ClaimEducationOrganizationIds; rename the comments/contracts away from GET-many and make it
     usable by GET-by-id/write/delete request shapes without enabling endpoint behavior yet.
  9. Emit structured inputs, not reusable full SQL fragments. Shared code can expose direction metadata, auth table names, subjects, parameterization, and check intent. Later operation slices
     should build their own EXISTS/AUTH1 SQL because batching shape differs by operation.
  10. Add typed failure metadata now. Carry strategy name/index, subject kind, JSON path/readable name, table/column, failure kind, stored/proposed source, auth object name, and hint metadata.
     Slice 6 can format responses later, but it should not have to rediscover this context.
  11. Leave non-GET middleware behavior mostly untouched in Slice 1. The existing staged 501 behavior should remain until Slice 2 starts GET-by-id/DELETE integration. Slice 1 should not silently
     change endpoint authorization behavior.
  12. If GET-many is refactored to use the new core, include a small DMS-1055 regression pass. The new Slice 1 tests can remain unit-focused, but run or preserve focused GET-many unit/integration
     coverage for normal, inverted, OR, empty claims, and parameterization behavior.

### Questions 2

  1. Where should the operation-neutral relationship auth contracts live: Backend.Plans, Backend.External, or internal Backend? The current implementation is split across all three, and Slice 2+
     will need to consume this cleanly.
  2. Should RelationalAuthorizationContext stay minimal with only ClaimEducationOrganizationIds, or should Slice 1 create an extensible operation-neutral context for later namespace, ownership,
     and People inputs?
  3. For AUTH1 mapping, should the dense emitted check index be dense only within relationship checks, or dense across the final auth.md execution order including future namespace/view/ownership
     checks?
  4. When two configured securable elements resolve to the same root-table column, should SQL dedupe the predicate while preserving both securable element names/paths in failure metadata?
  5. Should configuration failures aggregate all discovered strategy/subject problems, or return the first blocking security configuration failure?
  6. For empty EdOrg claims, should the core return an explicit “no claims” check/failure shape, or should each consuming operation short-circuit before asking for parameterization/check specs?
  7. Should proposed-value check specs assign deterministic parameter names now, or only describe required root-row column bindings and let Slice 3 choose collision-free names in the write batch?
  8. Should Slice 1 preserve the current DMS-1055 exact 501/security-configuration message text for GET-many tests, or is status plus structured failure kind enough if wording changes?
  9. Should NoFurtherAuthorizationRequired be represented in the shared classification result with raw configured index metadata, even though it emits no check, or omitted after classification as
     today?
  10. For custom view strategy classification, should Slice 1 keep the current convention resolution exactly, or align it with the DMS-1055 answer that basis resource or descriptor names can make
     a custom view “known but not implemented”?
  11. Should the current static EdOrg element resolution cache remain, or should this refactor introduce an injectable/cache-keyed service aligned to effective schema/mapping set/resource/
     securable element?
  12. Should Slice 1 add only unit tests plus run existing GET-many integration/E2E regressions, or should it add new focused integration tests specifically for the refactored adapter path?

### Answers 2

  1. Contract location: split the boundary. Keep request-scoped auth inputs like RelationalAuthorizationContext in Backend.External, because Core builds them. Put operation-neutral relationship
     auth specs, subject specs, check specs, parameterization metadata, and failure metadata in Backend.Plans. Keep endpoint orchestration and GET-many/CRUD adapters in internal Backend.
  2. Authorization context: make RelationalAuthorizationContext operation-neutral now, but keep it typed. Do not use a generic bag. Include normalized ClaimEducationOrganizationIds; add
     normalized NamespacePrefixes now if useful because ClientAuthorizations already has them. Add ownership fields later when that token data exists.
  3. AUTH1 index: final AUTH1 indexes should be dense across the full operation auth batch, not only relationship checks. Slice 1 should carry raw configured strategy index plus relationship-
     local order, but the later operation composer should assign the final emitted check index.
  4. Same resolved column: dedupe SQL predicates within a single strategy/check when multiple securable elements resolve to the same physical root-table column, but preserve all configured
     element names/paths in failure metadata. Do not dedupe duplicate configured strategies or normal vs inverted strategies.
  5. Configuration failures: aggregate all discoverable configuration problems in deterministic order, then return the security-configuration 500. If there is any true configuration error, that
     should win over known-but-not-implemented staging behavior.
  6. Empty EdOrg claims: core should expose an explicit “relationship check requires EdOrg claims, but none are present” metadata shape. Consumers decide behavior: GET-many returns empty page/
     count 0; CRUD returns 403 with claims rendered as none. Do not ask SQL parameterization to handle an empty EdOrg list.
  7. Proposed-value parameters: Slice 1 should define required root-row column bindings and stable logical parameter keys/seeds, not final SQL parameter names. Slice 3/4 should allocate concrete
     collision-free SQL names inside the write batch.
  8. GET-many wording tests: avoid pinning tests to exact transitional 501/security-config wording. Assert status/result kind, strategy/resource facts, and canonical security ProblemDetails
     fields. Exact long diagnostic text can evolve during the refactor.
  9. NoFurtherAuthorizationRequired: represent it in classification with raw configured index metadata and emits no check. Omit it from generated check specs. If it is the only effective
     strategy, classify as no further authorization required.
  10. Custom view classification: align with DMS-1055 guidance. A {BasisResource}With... name whose basis resolves to a known resource or descriptor should be “known but not enabled/implemented”
     in Slice 1. Non-matching names or unknown basis names are security-configuration failures. Do not validate the backing auth view in Slice 1.
  11. Resolution cache: replace the hidden static cache with an injectable cache/service. Cache raw securable path resolution by mapping set/effective schema, resource, and securable element.
     Apply operation-specific eligibility, such as root-table-only EdOrg CRUD subjects, after reading cached resolution. Still do not cache generated SQL.
  12. Tests: add unit tests for the new operation-neutral core and the GET-many page adapter. Run the existing DMS-1055 backend integration/E2E regressions. Add only focused new integration
     coverage where existing tests do not cover the refactored adapter risks, such as duplicate strategies, NoFurther mixed with relationship strategies, same-column dedupe, and configuration
     aggregation.

### Questions 3

  1. Should the operation-neutral classifier in Backend.Plans accept AuthorizationStrategyEvaluator, or should internal Backend adapt that into a new Plans-owned configured-strategy record first?
  2. Should RelationalAuthorizationContext add normalized NamespacePrefixes in Slice 1 now, or keep this slice to ClaimEducationOrganizationIds only?
  3. Does “concrete root-table EdOrg subject” include DbTableKind.RootExtension tables, or strictly only the concrete aggregate root table?
  4. For proposed-value specs, should Slice 1 resolve root-row subjects to TableWritePlan binding indexes/logical keys, or only carry table/column metadata and let Slice 3 map into row buffers?
  5. When multiple securable elements resolve to the same physical root column, should the shared spec model represent one predicate with multiple metadata entries, or multiple subject specs that
     adapters dedupe later?
  6. Do you want Slice 1 to introduce the full typed failure-kind taxonomy now, including endpoint-oriented cases like stored-null/proposed-missing, or only the failure kinds actually emitted by
     this slice?
  7. If classification finds both true security configuration errors and known-but-not-enabled strategies, should the returned metadata include both, even though the config error wins the
     response?
  8. Should the replacement for the current static EdOrg resolution cache be a DI singleton service registered in relational runtime services, or a Plans-level injectable object constructed by
     the repository/adapters?
  9. Should PageDocumentIdAuthorizationSpec stay a page-query-only simplified adapter, or should it grow strategy indexes and richer failure metadata so GET-many tests exercise the new shared
     contract directly?
  10. Should Slice 1 add security-configuration result variants for GET-by-id/POST/PUT/DELETE result contracts now, or defer those endpoint result-shape changes to Slice 2+?

### Answers 3

  1. Use a Plans-owned configured-strategy record. Do not let Backend.Plans accept AuthorizationStrategyEvaluator directly. Internal Backend should adapt evaluators into something like
     ConfiguredAuthorizationStrategy(StrategyName, RawConfiguredIndex, Composition) so Plans stays free of legacy filter-provider contracts.
  2. Add NamespacePrefixes to RelationalAuthorizationContext now. Keep it typed and normalized, but do not use it in this relationship slice. Store raw prefixes, deduped/sorted ordinal; SQL LIKE
     patterns can be built later by namespace strategy code.
  3. “Concrete root-table” should mean strictly DbTableKind.Root, not RootExtension. Extension-defined resources still authorize normally because they have their own root table. _ext fields on
     core resources should not become relationship auth subjects; auth.md says extension-added fields do not qualify as securable elements.
  4. Resolve proposed-value specs to root TableWritePlan binding locators now. Carry table/column plus stable binding index/logical key/parameter seed, but do not extract runtime values or
     allocate final SQL parameter names until Slice 3/4.
  5. Model same-column dedupe in the shared spec, not in adapters. Represent one predicate/binding with multiple contributing securable-element metadata entries. Preserve duplicate strategies
     separately.
  6. Introduce the typed failure taxonomy now, but only emit the cases Slice 1 can know. Include future cases such as stored-null/proposed-missing as contract values so Slice 2-6 do not invent
     parallel shapes later.
  7. Include both config errors and known-but-not-enabled metadata. Security configuration still wins the response, but keeping the staged-strategy facts is useful for deterministic diagnostics
     and regression tests.
  8. Replace the static cache with a DI singleton service registered with relational runtime services. Implement/cache raw path resolution by mapping set/resource/securable element, then apply
     operation-specific eligibility after cache lookup.
  9. Keep PageDocumentIdAuthorizationSpec a page-query adapter. Add only minimal strategy order/index fields if needed to prove duplicates/order survive the refactor. Do not copy the rich shared
     failure metadata into the page SQL model.
  10. Defer GET-by-id/POST/PUT/DELETE result-contract variants to Slice 2+. Slice 1 should return shared core metadata/results only; endpoint result surfaces belong to the vertical operation
     slices.

### Questions 4

  1. Can Slice 1 treat the order of AuthorizationStrategyEvaluator[] as the raw configured strategy index, or do we need Core/CMS to pass an explicit configured index?
  2. If the current Core/CMS path collapses duplicate strategy names before they reach the backend, should Slice 1 fix that upstream now, or only preserve duplicates once they reach the new
     operation-neutral core?
  3. Should Slice 1 plumb RelationalAuthorizationContext into relational GET-by-id/write/delete request contracts now, while leaving endpoint behavior unchanged, or just make the type operation-
     neutral and defer plumbing to Slice 2+?
  4. For the Plans-owned configured strategy record, is StrategyName, RawConfiguredIndex, and a local composition enum enough, or do we need extra claim/action/source metadata to distinguish
     repeated strategies from separate matched claims?
  5. For same-column dedupe, should predicate order follow the first contributing securable element in ApiSchema order, with all contributing names/paths preserved in that same order for failure
     metadata?
  6. For proposed-value specs, should the stable locator be TableWritePlan root ColumnBindings index plus existing WriteColumnBinding.ParameterName as the parameter seed, or should Slice 1
     introduce an auth-specific logical parameter seed independent of write parameter names?
  7. If a selected auth column is represented through key-unification aliasing, should proposed-value binding always resolve to the canonical stored WriteColumnBinding and treat a missing binding
     as a security configuration failure?
  8. For the new DI singleton securable resolution cache, should the cache key be per MappingSet.Key including dialect, or cross-dialect by effective schema hash/resource/securable element?
  9. Should the new typed failure metadata be adapted back to current GET-many QueryFailureSecurityConfiguration(string[] Errors) for now, or should Slice 1 introduce a richer query failure
     contract for GET-many immediately?

### Answers 4

  1. Use a Backend.Plans configured-strategy record, adapted by internal Backend from AuthorizationStrategyEvaluator. Include StrategyName, RawConfiguredIndex, and composition/order metadata. Do
     not let Plans depend on legacy filter-provider contracts. Current coupling is in src/dms/backend/EdFi.DataManagementService.Backend/RelationalGetManyAuthorizationStrategyClassifier.cs:81.
  2. Add normalized NamespacePrefixes to RelationalAuthorizationContext now, but do not use them in this slice. The current context is still GET-many-worded and EdOrg-only in src/dms/backend/
     EdFi.DataManagementService.Backend.External/RelationalQueryRequestContracts.cs:18.
  3. “Concrete root table” should mean strictly DbTableKind.Root, not RootExtension. Extension-defined resources have their own root table; _ext fields on core resources should not become
     relationship auth subjects, matching reference/design/backend-redesign/design-docs/auth.md:1442.
  4. Proposed-value specs should resolve to root TableWritePlan binding locators now: table, column, binding index, logical key, and parameter seed. Do not extract runtime values or allocate
     final SQL parameter names until Slice 3/4.
  5. Same-column dedupe belongs in the shared spec. Represent one predicate/binding with multiple contributing securable-element metadata entries. Preserve duplicate configured strategies
     separately. The current page adapter dedupes subjects with DistinctBy, which is fine only after the shared model has preserved contributor metadata: src/dms/backend/
     EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:1069.
  6. Introduce the typed failure-kind taxonomy now, but only emit what Slice 1 can know. Include future contract values like stored-null/proposed-missing so later slices do not invent parallel
     shapes.
  7. Include both true configuration errors and known-but-not-enabled strategy metadata in deterministic order, with configuration errors winning the response. That aligns with the security
     configuration 500 contract in reference/design/backend-redesign/design-docs/auth.md:1425.
  8. Replace the static cache with a DI singleton service registered with relational runtime services. Cache raw path resolution by mapping set/resource/securable element, then apply operation-
     specific eligibility afterward. Current static cache is in src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationalEdOrgAuthorizationSubjectSelector.cs:29.
  9. Keep PageDocumentIdAuthorizationSpec as a page-query adapter. Add only minimal raw configured index/order fields needed to prove duplicates and order survive; do not copy rich failure
     metadata into the page SQL model.
  10. Defer GET-by-id/POST/PUT/DELETE result-contract variants to Slice 2+. Slice 1 should return shared core planning metadata/results only.
