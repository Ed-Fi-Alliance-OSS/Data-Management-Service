# Backend Redesign: Mapping Pack File Format v1 (`.mpack`)

Status: Draft (**normative** file/serialization contract for Mapping Pack *PackFormatVersion=1*).

This document defines the on-disk bytes for a **Mapping Pack** (“`.mpack`”) as referenced by:

- AOT compilation overview: `reference/design/backend-redesign/design-docs/aot-compilation.md`
- Flattening & reconstitution models/plans: `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- Key unification model/plan requirements: `reference/design/backend-redesign/design-docs/key-unification.md`
- Effective schema fingerprinting: `reference/design/backend-redesign/design-docs/data-model.md` (`EffectiveSchemaHash`)
- DDL generator workflow and `dms.ResourceKey` seeding: `reference/design/backend-redesign/design-docs/ddl-generation.md`

Authorization is addressed in [auth.md](auth.md). Mapping packs focus on schema-derived relational mapping artifacts and do not embed token-dependent authorization SQL (which depends on caller context and configured strategy sets). The runtime applies authorization using `auth.*` companion objects and token-derived context alongside the plans/materialization described by this format.

---

## 1. Purpose

The `.mpack` format is a redistributable artifact that contains **dialect-specific, precompiled relational mapping artifacts** for one effective schema:

- deterministic `dms.ResourceKey` seed mapping for that schema
- per-resource relational models (tables/columns/paths) needed by generic flatten/reconstitute
- per-resource, dialect-specific SQL plans and projection metadata (write/read/reference-identity/descriptor-URI)

The consumer (DMS runtime) MUST be able to execute schema-dependent relational work for that effective schema **without compiling** models or SQL from `ApiSchema.json` at runtime (but Core may still load `ApiSchema.json` for validation and identity extraction).

---

## 2. Pack identity (selection key)

Mapping packs are selected strictly by this 4-tuple:

1. `EffectiveSchemaHash` (lowercase hex, 64 chars) — see `reference/design/backend-redesign/design-docs/data-model.md`
2. `SqlDialect` (`PGSQL` or `MSSQL`)
3. `RelationalMappingVersion` (DMS-controlled string constant; the pre-production v1 design is finalized in place)
4. `PackFormatVersion` (integer protocol/version gate; bump only for breaking serialization/envelope changes)

Logical identity is therefore:

```text
(effective_schema_hash, dialect, relational_mapping_version, pack_format_version)
```

File naming/layout is a *distribution convenience* only and MUST NOT be treated as authoritative.

Recommended layout (non-normative):

```text
{MappingPackRoot}/
  pgsql/
    dms-mappingpack-{relMappingVersion}-{effectiveSchemaHash}.mpack
  mssql/
    dms-mappingpack-{relMappingVersion}-{effectiveSchemaHash}.mpack
```

---

## 3. Encoding overview (bytes on disk)

A `.mpack` file contains exactly one protobuf message:

- `MappingPackEnvelope` encoded as protobuf (uncompressed)

The envelope contains:

- the selection key fields (hash/dialect/versioning)
- `payload_zstd`: zstd-compressed bytes of a second protobuf message `MappingPackPayload`
- `payload_sha256`: SHA-256 of the **uncompressed** `MappingPackPayload` bytes
- `zstd_uncompressed_payload_length`: payload length for bounded decompression

Compression algorithm:
- PackFormatVersion 1 uses **Zstandard (zstd)** only.

Hashing:
- Use SHA-256.
- `payload_sha256` is computed over the exact `MappingPackPayload` protobuf byte sequence (see determinism rules).

### 3.1 Normalized payload model (not 1:1 executor-contract serialization)

PackFormatVersion 1 stores a **normalized representation** of runtime artifacts. The payload is not required to mirror executor-facing contract object graphs 1:1.

Stored in payload (authoritative):
- canonical identifiers (`DbTableName`, `DbColumnName`, canonical JsonPath strings)
- SQL text and deterministic parameter/binding metadata
- deterministic indices/ordinals and ordering-sensitive lists
- deterministic batching metadata
- per-resource certified same-statement reference-resolution executor plans where a submitted future target identity is
  not resolvable before the initiating cascade
- globally compiled dialect-specific lineage-anchor projection plans for every used non-empty target `AnchorSetId`
- minimal finalized abstract-target propagation-key records for pack validation (not the full abstract derivation model)
- finalized foreign keys, including stable `PhysicalForeignKeyId`, stable minimal `AnchorSetId`, expanded ordered
  identity/lineage-anchor/`DocumentId` vectors, and final referential actions

Not stored (build-time derivation inputs and audit diagnostics):
- SQL Server `MssqlForeignKeyDecision`, `MssqlPropagationMode`, and `CoverageCertificate` values. The payload stores the
  finalized `ForeignKeyConstraint.on_update` action instead.
- provider-neutral `AnchorOmissionProof` values, physical FK candidates, SQL Server value-flow proof facts,
  `AbstractIdentityMemberMapping`, full lineage-anchor closure inventories, and semantic
  `RelationalExecutorRequirements`. Runtime consumers need the resulting columns, validation projections, finalized FKs,
  compiled same-statement executor plans, and write bindings, not derivation proof state.

Pack decode reconstructs `RuntimeRelationalModelSet`, the strict finalized per-resource projection used by `MappingSet`.
It does not reconstruct `DerivedRelationalModelSet`; DDL and manifest producers consume the original
`DerivedRelationalModelArtifact` and never use a decoded pack as a derivation substitute.

Derived at pack load/reconstruction time:
- `KeysetTableContract` values from `SqlDialect` (`page` for PGSQL, `#page` for MSSQL; `DocumentId` column)
- `JsonPathExpression` runtime objects (`Segments`) by compiling canonical JsonPath strings
- table/column object references by lookup (`(schema, name)` for tables, `DbColumnName.value` within table for columns)

Consumers MUST reconstruct executor-facing contracts from these normalized payload values and MUST NOT infer bindings from SQL text parsing.

Pack production and compile-equivalence tests, not pack load, prove action provenance: PostgreSQL actions come from the
fixed target-mutability rule, while SQL Server actions come from the successful global value-flow/error-1785 selection.
The payload deliberately omits the mutability and proof inventories needed to recompute that fact. A consumer validates
that every final action is explicit and structurally consistent, then trusts the checksum-bound producer result selected
by the effective-schema/dialect/mapping-version key.

---

## 4. Determinism requirements (normative)

For a fixed `(EffectiveSchemaHash, SqlDialect, RelationalMappingVersion, PackFormatVersion)`, producers MUST emit a payload that is **byte-for-byte stable**.

This is required for:
- golden-file tests,
- reproducible builds,
- reliable diagnostics (diffing packs),
- stable `payload_sha256`.

### 4.1 Protobuf determinism

Producers MUST serialize `MappingPackPayload` using deterministic protobuf serialization.

Notes:
- `proto3` does not guarantee deterministic output by default.
- Producers should use the deterministic serialization option provided by their protobuf runtime/library (e.g., `Google.Protobuf` deterministic output).

The envelope MAY contain build/producer metadata that changes between runs (e.g., timestamps), but MUST NOT affect payload determinism.

### 4.2 Ordering rules (no `map<>`)

The `.proto` schema for PackFormatVersion 1 MUST NOT use `map<...>` fields.

All collections are `repeated` and MUST be emitted in stable deterministic order:

- `resource_keys`: ascending by `(resource_key_id)` (and then by `(project_name, resource_name)` for tie-breaking, though ties are invalid).
- `resources`: ascending by `(project_name, resource_name)` using ordinal (culture-invariant) string ordering.
- `lineage_anchor_resolution_plans`: ascending by `(target_resource.project_name, target_resource.resource_name,
  anchor_set_id)` using ordinal ordering; result bindings preserve stable `IdentityLineageId` order.
- `abstract_target_propagation_keys`: ascending by `(target_resource.project_name, target_resource.resource_name,
  anchor_set_id)` using ordinal ordering; target columns preserve propagation-vector order and lineage entries preserve
  stable `IdentityLineageId` order.
- Within each `RelationalResourceModel`:
  - `tables_in_dependency_order`: root-first, then depth-first; stable within sibling set by `(json_scope, table_name)` using ordinal string ordering
    - This canonical order MUST match in-memory `RelationalResourceModel.TablesInDependencyOrder` and is used by both read hydration and write flattening.
  - `columns`: stable per table; key columns first (in key order), then document reference groups and internal lineage
    anchors, descriptor FKs, then scalars
  - `constraints`: ascending by `(constraint_kind_group, name)` where
    `constraint_kind_group = unique < foreign_key < all_or_none_nullability < null_or_true`
    - within each document-reference FK, identity columns preserve target identity order, lineage anchors preserve stable
      `IdentityLineageId` order for its `AnchorSetId`, and `DocumentId` is last
    - `foreign_key.lineage_anchors_in_order` uses that same stable `IdentityLineageId` order; ordinals refer to the final
      ordered local and target column lists
  - `document_reference_bindings`: ascending by `(reference_object_path)`
  - `descriptor_edge_sources`: ascending by `(descriptor_value_path)`
  - `key_unification_classes`: ascending by `(canonical_column.value)`
  - each table's `persisted_occurrence_identity.complete_match_in_order` is emitted exactly in compiled ancestor-context
    then semantic-identity order; `stable_row_locator_columns_in_order` preserves the provider-neutral physical locator
    order. Neither list is sorted by column name
  - within `document_reference_bindings[*]`: `identity_bindings` preserve ApiSchema `referenceJsonPaths` order
    (identity field order); duplicate `reference_json_path` values are allowed only within one binding and represent a
    same-site flattened reference group
  - within `key_unification_classes[*]`:
    - `member_path_columns` order is semantically significant and MUST NOT be sorted
    - producers MUST emit the list exactly as derived (used for deterministic write-time coalescing)
- Within each `ResourceWritePlan`:
  - `table_plans`: exactly `relational_model.tables_in_dependency_order` (root/parents before children), one plan per
    table; this execution order is authoritative and is not replaced by lexical table-name sorting
  - `same_statement_reference_resolution_plans`: ascending by
    `(document_reference_binding_index, allowed_direct_origin.mutation_origin_id, mutation_case_id)`; changed items and
    future values preserve propagation-vector order
    - each correlation set input is ordered as correlation key, origin `DocumentId`, materialized occurrence-match values
      in persisted occurrence order, then submitted public identity values in selected-vector order
    - each post-verification set input is ordered as correlation key, submitted referential id, expected target
      `DocumentId`
    - correlation SELECT results are ordered as correlation key, stored receiver locator values in locator order, target
      `DocumentId`, then locked unchanged target values in selected-vector order; post-verification results are ordered as
      correlation key, target `DocumentId`, then demanded anchors in `IdentityLineageId` order
- Within each `TableWritePlan`:
  - `collection_merge_plan`, when present, is semantically significant and MUST be emitted deterministically
    - `semantic_identity_bindings` order is semantically significant and MUST preserve compiled semantic-identity order
    - `compare_binding_indexes_in_order` order is semantically significant and MUST preserve deterministic compare/no-op projection order
  - `bulk_insert_batching` is semantically significant and MUST be derived deterministically from:
    - dialect limits (e.g., SQL Server parameter limits),
    - policy row caps, and
    - the plan's `column_bindings` count
    - `bulk_insert_batching.max_rows_per_batch`, `bulk_insert_batching.parameters_per_row`, and `bulk_insert_batching.max_parameters_per_command` MUST all be stable for the same selection key
  - `column_bindings`: order is semantically significant (defines parameter ordering) and MUST NOT be sorted
    - `parameter_name` is semantically significant and MUST be deterministic and unique within its statement
  - `collection_key_preallocation_plan`, when present, is semantically significant and MUST deterministically reference one `column_bindings` slot that receives reserved collection-row identities
  - `key_unification_plans`: ascending by `(canonical_column.value)`
  - within `key_unification_plans[*]`:
    - `members_in_order` order is semantically significant and MUST NOT be sorted
    - producers MUST emit the list exactly as derived (must match the corresponding `key_unification_classes[*].member_path_columns`)
- Within each `ResourceReadPlan`:
  - `table_plans`: order is semantically significant and MUST match `relational_model.tables_in_dependency_order` (do not sort independently)
  - `reference_identity_projection_table_plans`: order is semantically significant and MUST follow `table_plans` dependency order for tables that emit reference-identity metadata
  - `descriptor_projection_plans`: order is semantically significant (execution order) and MUST NOT be sorted
- Within each `ReferenceIdentityProjectionTablePlan`:
  - `bindings_in_order`: order is semantically significant and MUST preserve deterministic `reference_object_path` order from `relational_model.document_reference_bindings` for that table
  - within `bindings_in_order[*]`: `identity_field_ordinals_in_order` preserves logical reference field order and MUST
    NOT be sorted
    - duplicate `reference_json_path` values from `relational_model.document_reference_bindings[*].identity_bindings`
      MUST be grouped by `reference_json_path` after storage resolution
    - emitted `identity_field_ordinals_in_order[*].reference_json_path` values MUST therefore be distinct within a
      binding
- Within each `DescriptorProjectionPlan`:
  - `sources_in_order`: order is semantically significant and MUST preserve deterministic `descriptor_value_path` order from `relational_model.descriptor_edge_sources` for the plan's selected source set

### 4.3 SQL text canonicalization

All SQL strings embedded in the pack MUST be byte-for-byte stable for a fixed key.

Follow the canonicalization requirements in:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (“SQL text canonicalization (required)”)

At minimum:
- `\n` line endings only
- stable keyword casing per dialect
- stable alias/parameter naming (no randomized suffixes)
- stable select/join/predicate ordering

---

## 5. `dms.ResourceKey` seeding contract (pack ↔ DB)

Every pack payload includes the deterministic `dms.ResourceKey` seed mapping for its effective schema.

### 5.1 Seed list requirements

The payload MUST include the complete set of `ResourceKeyEntry` rows for:

- every concrete `resourceSchema` in the effective schema where `isResourceExtension` is not `true` (including descriptors and non-extension resources from extension projects),
- excluding `isResourceExtension: true` resource-extension overlays because they compile into `_ext` extension tables on the owning base resource rather than standalone document/resource-key rows, and
- every `abstractResources[*]` name (used for polymorphic/superclass alias behavior).

`resource_key_id` MUST fit in SQL `smallint` (≤ 32767).

### 5.2 Seed hash requirements

The payload MUST include `resource_key_seed_hash` and `resource_key_count`, computed as:

- `resource_key_count = number of entries` and MUST fit the same `smallint`-bounded contract as `dms.EffectiveSchema.ResourceKeyCount` (maximum 32,767)
- `resource_key_seed_hash = SHA-256(UTF8(manifest))`

Where `manifest` is:

```text
resource-key-seed-hash:v1\n
{resource_key_id}|{project_name}|{resource_name}|{resource_version}\n
...
```

### 5.3 Runtime validation against database

Consumers MUST validate that the target database’s `dms.ResourceKey` contents match the pack’s embedded seed mapping for the same effective schema.

Recommended fast-path (depends on DDL design):
- read `ResourceKeyCount` + `ResourceKeySeedHash` from `dms.EffectiveSchema` for the database’s `EffectiveSchemaHash`, compare to payload (byte-for-byte; `ResourceKeySeedHash` is raw SHA-256 bytes, 32 bytes)

Required slow-path fallback:
- read `dms.ResourceKey` ordered by `ResourceKeyId` and diff vs payload, then fail fast on mismatch

Rationale and seeding algorithm guidance: `reference/design/backend-redesign/design-docs/ddl-generation.md`.

---

## 6. Consumer validation and reconstruction algorithm (normative)

Given `(expectedEffectiveSchemaHash, expectedDialect, expectedRelationalMappingVersion, expectedPackFormatVersion=1)`:

1. Read file bytes and parse `MappingPackEnvelope` protobuf.
2. Validate envelope header fields:
   - `pack_format_version == 1`
   - `effective_schema_hash` matches expected (ordinal compare)
   - `dialect` matches expected
   - `relational_mapping_version` matches expected (ordinal compare)
   - `compression_algorithm == ZSTD`
   - `zstd_uncompressed_payload_length` is non-zero and within configured max
3. Decompress `payload_zstd` with zstd into a byte array of exactly `zstd_uncompressed_payload_length`.
4. Compute SHA-256 over decompressed bytes and validate `payload_sha256` (fixed-time compare).
5. Parse decompressed bytes as `MappingPackPayload` protobuf.
6. Validate payload invariants:
   - `resource_key_count == resource_keys.Count`
   - recompute `resource_key_seed_hash` from `resource_keys` and compare
   - `resources` are unique by `(project_name, resource_name)` and sorted
   - For each `ResourcePack`:
     - if `is_abstract=false`, has `relational_model`, `write_plan`, and `read_plan`
     - within `relational_model`, canonical path keys are unique:
       - `document_reference_bindings[*].reference_object_path` has no duplicates
       - `descriptor_edge_sources[*].descriptor_value_path` has no duplicates
       - `document_reference_bindings[*].identity_bindings[*].reference_json_path` MAY repeat only within one
         `document_reference_binding` and only to represent one same-site flattened reference group
     - all referenced tables/columns/bindings referenced by plans exist in the model
     - write/query binding invariants:
       - `write_plan.table_plans` has exactly one entry per relational-model table and its table sequence exactly equals
         `relational_model.tables_in_dependency_order`
       - each `column_bindings[*].parameter_name` is present and unique case-insensitively within a statement
       - all binding-index references are in-range for the target list
       - every document-reference binding has a non-`UNSPECIFIED` resolution policy. `PRESTATEMENT_LOOKUP_ONLY` has no
         same-statement plan; `PRESTATEMENT_LOOKUP_OR_CERTIFIED_SAME_STATEMENT` has at least one matching plan
       - same-statement plans are unique and canonically sorted by `(binding_index, mutation_origin_id, mutation_case_id)`;
         their binding/site/FK/target resource exactly match the referenced `DocumentReferenceBinding`
       - plans are `PUT_BY_DOCUMENT_UUID` only, name the exact direct origin and one statement boundary, and cover an
         existing stored reference column on the binding's table. POST, creates, newly-present sites, and hidden/absent
         sites never select a plan
       - the retained changed-target route always reconstructs `RouteStart.MutationRow`; an `OriginWrite` route start is
         invalid in this field. Every route hop names an existing finalized FK with `ON UPDATE CASCADE`, has the declared
         boundary, and connects the direct origin to the stored target row. The request binding's own reference FK may be
         either final action; it must match the plan but need not be a route hop. Full requirement/certificate provenance
         equivalence is a producer test, not inferred by the loader
       - row-locator columns, correlation column pairs, set-input columns, and result ordinals are explicit and valid.
         Each pre/post command has one non-empty JSON-recordset parameter, unique typed column names, and
         `max_instances_per_batch > 0`; SQL uses canonical `jsonb_to_recordset` on PostgreSQL or `OPENJSON ... WITH` on
         SQL Server. Correlation/origin/stored-target/submitted-referential-id roles are singletons when applicable;
         `OCCURRENCE_MATCH_VALUE` and `SUBMITTED_IDENTITY_VALUE` may repeat for composite semantic and public identities
       - built-in role types are fixed: correlation key, origin `DocumentId`, and stored/expected target `DocumentId` are
         `SCALAR_KIND_INT64`; submitted referential id is `SCALAR_KIND_GUID`. Occurrence values exactly match their
         materialized write-binding types, and submitted identity values exactly match their public identity-binding
         types. String substitution plus provider casts is not an equivalent encoding
       - `occurrence_to_stored_row.complete_semantic_identity_in_order` exactly matches the compiled full receiver-row
         semantic identity/ancestor context needed to identify one persisted occurrence; it never uses request ordinal as
         identity. Every source is exactly one of a typed materialized write binding available after ordinary resolution
         and key unification or `CORRELATED_CHANGED_TARGET_DOCUMENT_ID`. Materialized source names/types have a one-to-one
         match with correlation-input `OCCURRENCE_MATCH_VALUE` columns and may cover scalars, descriptors, ordinary
         resolved references, parent/root locators, or precomputed values; no such binding may consume the deferred
         reference or one of its deferred anchors. The correlated-target marker has no input column, names the target's
         current `DocumentId` column, and is supplied only by the locking retained-route join comparing the receiver's
         stored reference FK with that stable target id. Reference-backed semantic identity never uses propagated public
         components
       - stored-row locator result bindings exactly equal `StoredTargetDocumentIdSource.row_locator_columns_in_order`, in
         order, with distinct result ordinals. Correlation input contains exactly one correlation key and origin id plus
         every materialized occurrence-match value and exactly one `SUBMITTED_IDENTITY_VALUE` for each public future
         vector item. Each public future binding names its matching typed input column and submitted identity-binding
         index; non-public items name neither. The query uses these public inputs with origin-derived changed values and
         locked unchanged values to disambiguate fan-out routes, derives any correlated target sentinel internally, takes
         the dialect's required update lock, and returns the stable receiver locator and stored target id
       - correlation input column order is exactly correlation key, origin `DocumentId`, materialized occurrence values in
         complete-match order, then submitted identity values in public-vector order. Its output ordinals are exactly:
         correlation key at 0, stored-row locators in locator order, target `DocumentId`, then unchanged target items in
         vector order. They are globally distinct and dense. Post-write
         input contains correlation key, submitted referential id, and expected target id; its output's correlation-key,
         target-id, and demanded-anchor ordinals are exactly 0, 1, then 2.. in `IdentityLineageId` order. Its input columns
         occur in the same stated order. Both commands require exactly one result row per
         unique input key; missing/duplicate/extra keys fail. Post SQL is cache-bypassing and anchor ids/order exactly
         match the selected `AnchorSetId`
       - changed items are unique and in propagation-vector order. `future_values_in_vector_order` exactly equals the
         selected FK's public/anchor/terminal-`DocumentId` target vector; public items name the matching submitted identity
         binding and typed correlation-input column, anchors name the selected lineage id, and terminal `DocumentId` is
         last and sourced only from the stored target id
       - every origin-write source references a valid stored/writable binding on the declared origin table. It may use the
         unresolved reference's already-submitted public scalar only when the requirement's value lineage proves that
         exact changed item reaches the correlated target in the same boundary; it may not depend on the deferred target
         id or deferred anchors. Every stored-target-column source names exactly one locked correlation-result
         column/ordinal for an unchanged item. Exactly one origin binding, stored target column, or stored target id
         supplies each item, and target-id/anchor dependencies are acyclic. No missing/null/recursive deferred source can
         reach runtime
       - `correlation_command.stored_target_values_in_vector_order` is a bijection, in vector order, with future items
         sourced by `stored_target_column`: same item, physical column, and result ordinal, with no unused/duplicate entry.
         Such items are absent from `changed_items_in_vector_order` and omit `changed_value_lineage_id`
       - `changed_value_lineage_id` is non-empty exactly for an origin-write item structurally marked changed on the
         retained route. Semantic equivalence to the omitted producer requirement/case lineage is enforced by
         producer/equivalence tests, not reconstructed by the loader. Stored unchanged target values and terminal stored
         `DocumentId` omit it
     - lineage-anchor resolution-plan invariants:
       - plans are unique and already sorted by `(target_resource.project_name, target_resource.resource_name,
         anchor_set_id)`; duplicate or out-of-order entries are rejected
       - every `(target_resource, anchor_set_id)` whose finalized document-reference FKs have a non-empty
         `lineage_anchors_in_order` has exactly one global plan, no other/unused plan exists, and variants whose lineage
         mapping is empty have no plan
       - plan target table/resource exists in a concrete normalized model or exactly one matching
         `abstract_target_propagation_keys` record, SQL is non-empty/canonical, `max_document_ids_per_batch > 0`, and
         exactly one set-parameter union arm matches the envelope dialect
       - PostgreSQL plans have a non-empty canonical array parameter name; SQL Server plans have a non-empty canonical
         parameter name and
         `type_name = "dms.BigIntTable"`, whose provisioned v1 shape is `(Id bigint NOT NULL PRIMARY KEY)`
       - `lineage_anchors_in_id_order` is non-empty, unique, and strictly ordered by `IdentityLineageId`; it exactly
         matches the stable ordered lineage mappings on every finalized FK using that target/variant
       - the target `DocumentId` result ordinal and anchor result ordinals are distinct and together form the dense range
         `0..lineage_anchor_count`; a pack consumer never infers result shape from SQL text
     - abstract-target propagation-key invariants:
       - records are unique and already sorted by `(target_resource.project_name, target_resource.resource_name,
         anchor_set_id)`; each target resource exists as `is_abstract_resource = true`, and no concrete target has a
         record
       - `target_columns_in_order` is non-empty and distinct, `document_id_column` is its last entry, and
         `unique_constraint_name` plus the full stable `anchor_set_id` are non-empty
       - `lineage_anchors_in_id_order` is unique and strictly ordered by `IdentityLineageId`; each target column occurs at
         the matching propagation-vector position between the public-identity prefix and terminal `DocumentId`
       - every finalized document-reference FK targeting an abstract resource has exactly one record whose target table,
         `AnchorSetId`, ordered target columns, and lineage-id/target-column pairs match exactly; every record is used by at
         least one such FK
     - read/projection invariants:
       - every `read_plan.table_plans[*].table` exists in `relational_model.tables_in_dependency_order`
       - every `read_plan.reference_identity_projection_table_plans[*].table` exists, and `fk_column_ordinal` plus every `identity_field_ordinals_in_order[*].column_ordinal` are valid ordinals for that table's hydration row shape
       - every `read_plan.reference_identity_projection_table_plans[*].bindings_in_order[*].identity_field_ordinals_in_order[*].reference_json_path` is distinct within that binding
       - every `read_plan.descriptor_projection_plans[*].sources_in_order[*].table` exists, and `descriptor_id_column_ordinal` is a valid ordinal for that source table's hydration row shape
       - every `read_plan.descriptor_projection_plans[*].result_shape` has distinct `descriptor_id_ordinal` and `uri_ordinal`
     - key unification invariants (when used in this `RelationalMappingVersion`):
       - every `DbColumnModel` has `storage` set
       - any `UnifiedAlias` storage references only stored columns on the same table (canonical + optional presence)
       - any `DbTableModel.key_unification_classes` entries reference only columns on the same table and the member list is ordered and distinct
       - any `WriteValueSource.precomputed` bindings are populated either by exactly one `TableWritePlan.key_unification_plans` entry or by deterministic collection-id reservation associated with `TableWritePlan.collection_merge_plan`
     - persisted occurrence-identity invariants:
       - every table has one non-empty, ordered `persisted_occurrence_identity`; every stored match/locator column exists
         on that table, stable locators are distinct, stored, non-ordinal physical row locators, and request ordinal is
         never a match source
       - ancestor-context parts precede semantic-identity parts. `relative_json_path` is absent exactly for ancestor
         context and present/unique exactly for semantic members; supported persisted multi-item collection tables have at
         least one semantic member
       - every match source selects exactly one union arm. A materialized source names an existing writable column/binding
         available before certified resolution. A document-reference target source names an existing site on the table
         and its terminal target-`DocumentId` role; no reference-backed match source names a propagated public component
       - `CollectionMergePlan.semantic_identity_bindings`, when present, is the exact binding-index projection of the
         model's semantic-member entries in order. Same-statement occurrence matchers are the exact complete-match
         projection for the selected site; pack load never reconstructs either list from UNIQUE constraints or names
     - identity-lineage column and write-binding invariants:
       - every `COLUMN_KIND_IDENTITY_LINEAGE_ANCHOR` has non-empty `identity_lineage_id`, stored storage, no
         `source_json_path`, and a `target_resource`
       - a `COLUMN_KIND_DOCUMENT_FK` that serves as intrinsic storage for a reference-backed identity lineage carries the
         same stable `identity_lineage_id`; unrelated document FKs leave it empty
       - every added writable anchor column has exactly one `WriteColumnBinding` whose
         `WriteIdentityLineageAnchor.binding_index` names the demanding document-reference binding and whose
         `identity_lineage_id` matches the column; an explicitly reused document-FK column keeps its normal document-
         reference binding and is not written twice
     - finalized foreign-key invariants:
       - the payload contains finalized constraints only, not pre-action physical FK candidates
       - `on_delete` and `on_update` are never `REFERENTIAL_ACTION_UNSPECIFIED`; omitted/default actions are rejected
       - every finalized FK carries a unique stable `physical_foreign_key_id`; every document-reference FK also carries
         the stable minimal `anchor_set_id` selected by its logical reference
       - duplicate physical FK identities on one table are rejected even when their `on_update` values differ; action is
         not part of physical identity
       - every FK associated with a `DocumentReferenceBinding` is expanded full-composite: identity storage columns
         first, the selected `AnchorSetId`'s stored lineage anchors in stable order next, and `DocumentId` last
       - `lineage_anchors_in_order` contains exactly the selected demanded lineages in stable `IdentityLineageId` order;
         each local/target ordinal is in range, points to positionally paired columns in the FK lists, and identifies a
         column carrying that same lineage id (whether an added anchor or a proved-reused document FK)
       - every logical reference selects exactly one FK/`AnchorSetId` variant. A concrete target's normalized table has
         exactly one matching UNIQUE propagation key; an abstract target has exactly one matching
         `abstract_target_propagation_keys` record. A table-wide anchor union is not required
       - each `DocumentReferenceBinding` has a stable `reference_site_id` and names exactly one matching finalized
         `physical_foreign_key_id` / `anchor_set_id` pair
       - for every optional document-reference binding, exactly one `AllOrNoneNullabilityConstraint` uses that binding's
         `fk_column`; its `dependent_columns` are the complete ordered per-site public identity binding columns plus every
         added local anchor written by `WriteIdentityLineageAnchor` for that same binding, with no omitted or unrelated
         column. Required references instead require those stored columns to be non-nullable. An anchor supplied by a
         proved-reused document-FK column retains its own owning-reference presence contract and is not duplicated as a
         dedicated dependent column
       - `on_update` is explicit and valid for the envelope dialect; provenance is a producer/equivalence-test invariant,
         not a consumer reconstruction check
7. Reconstruct executor-facing contracts deterministically from normalized payload values:
   - resolve table identities by `(schema, name)` and columns by `DbColumnName.value` (within each resolved table)
   - compile canonical JsonPath strings into `JsonPathExpression` runtime objects
   - derive keyset temp-table contract from dialect constants (`page`/`#page`, column `DocumentId`)
   - preserve authoritative payload order for all ordering-sensitive collections (no runtime resorting)
   - bind parameters by explicit metadata (`column_bindings`, query parameter inventories), never by SQL-text parsing
   - construct `RuntimeRelationalModelSet` from envelope/payload effective-schema metadata, concrete resource storage
     kinds, and finalized per-resource models; do not manufacture omitted `DerivedRelationalModelSet` inventories
   - reconstruct the ordered minimal `AbstractTargetPropagationKey` validation projection and attach it to `MappingSet`;
     do not treat it as a complete abstract table or propagation-key derivation inventory
   - reconstruct global `LineageAnchorResolutionPlan` values directly from their typed SQL/input/result contracts and
     attach them to `MappingSet`; never regenerate them from final FKs or ApiSchema at pack-load time
   - reconstruct each resource's certified same-statement plans directly from typed payload fields and attach them to
     `ResourceWritePlan`; never reconstruct semantic executor requirements or infer future-value sources from SQL text
8. Validate `dms.ResourceKey` mapping in the target database (fast path if available; otherwise full diff) and cache:
   - `(ProjectName, ResourceName) -> ResourceKeyId`
   - `ResourceKeyId -> (ProjectName, ResourceName, ResourceVersion)`

If any step fails, the consumer MUST reject the pack and treat it as unusable for that database.

### 6.1 Required fail-fast reconstruction checks

During step 7, consumers MUST fail fast with deterministic errors when any reconstruction invariant is violated, including:
- unknown `(schema, name)` table identities
- unknown `DbColumnName.value` in a resolved table
- out-of-range binding indices and select-list ordinals
- duplicate canonical paths (`reference_object_path`, `descriptor_value_path`)
- duplicate `reference_json_path` values in `read_plan.reference_identity_projection_table_plans[*].bindings_in_order[*].identity_field_ordinals_in_order`
- duplicate `document_reference_bindings[*].identity_bindings[*].reference_json_path` values that are not confined to one same-site flattened reference group
- duplicate/ambiguous parameter names where uniqueness is required
- unsupported dialect values when deriving keyset table constants

---

## 7. Normative protobuf schema (PackFormatVersion 1)

This section is the normative `.proto` contract for `.mpack` files with `pack_format_version = 1`.

Protobuf evolution rules:
- never reuse field numbers
- only add new optional fields
- keep deprecated fields to preserve wire compatibility where possible

Implementation note (non-normative):
- Maintain this `.proto` schema and the generated C# types in a dedicated “contracts” project (recommended: in-repo, optionally publishable as a NuGet package) so pack producers and consumers share a single source of truth.

```proto
syntax = "proto3";

package edfi.dms.mappingpacks.v1;

// ---------- Envelope ----------

enum SqlDialect {
  SQL_DIALECT_UNSPECIFIED = 0;
  SQL_DIALECT_PGSQL = 1;
  SQL_DIALECT_MSSQL = 2;
}

enum CompressionAlgorithm {
  COMPRESSION_ALGORITHM_UNSPECIFIED = 0;
  COMPRESSION_ALGORITHM_ZSTD = 1;
}

message MappingPackEnvelope {
  // Selection key (authoritative; file name is not trusted).
  string effective_schema_hash = 1;          // lowercase hex (64 chars)
  SqlDialect dialect = 2;
  string relational_mapping_version = 3;     // e.g. "v1"
  uint32 pack_format_version = 4;            // must be 1 for this schema

  // Payload envelope.
  CompressionAlgorithm compression_algorithm = 5;      // must be ZSTD
  uint64 zstd_uncompressed_payload_length = 6;         // bytes
  bytes payload_sha256 = 7;                             // 32 bytes (SHA-256 of uncompressed payload bytes)

  // Optional producer metadata (non-key; must not be used for selection).
  string producer = 8;                                  // e.g. "dms-mappingpack"
  string producer_version = 9;                          // e.g. git SHA or semver
  uint64 produced_at_unix_ms_utc = 10;                  // optional

  // Zstd-compressed bytes of MappingPackPayload protobuf.
  bytes payload_zstd = 11;
}

// ---------- Payload ----------

message MappingPackPayload {
  // Useful for diagnostics; does not participate in selection (EffectiveSchemaHash does).
  string api_schema_format_version = 1;                 // ApiSchema.json apiSchemaVersion

  // Optional: expected schema components for diagnostics.
  repeated SchemaComponent schema_components = 2;

  // Deterministic dms.ResourceKey seed mapping.
  uint32 resource_key_count = 10;
  bytes resource_key_seed_hash = 11;                    // 32 bytes (SHA-256)
  repeated ResourceKeyEntry resource_keys = 12;

  // Per-resource artifacts.
  repeated ResourcePack resources = 20;

  // Global dialect-specific projections compiled from the finalized model. Empty when every used
  // document-reference variant has an empty demanded anchor set.
  repeated LineageAnchorResolutionPlan lineage_anchor_resolution_plans = 21;

  // Minimal validation projection for used propagation-key variants on abstract identity tables.
  repeated AbstractTargetPropagationKey abstract_target_propagation_keys = 22;
}

message SchemaComponent {
  string project_endpoint_name = 1;
  string project_name = 2;
  string project_version = 3;
  bool is_extension_project = 4;
}

message ResourceKeyEntry {
  // Deterministic id (seeded by DDL generator) used in core tables.
  uint32 resource_key_id = 1;                           // must fit SQL smallint
  string project_name = 2;
  string resource_name = 3;
  string resource_version = 4;                          // SemVer from ApiSchema projectSchema.projectVersion
  bool is_abstract_resource = 5;
}

message ResourcePack {
  string project_name = 1;
  string resource_name = 2;
  bool is_abstract_resource = 3;
  ResourceStorageKind storage_kind = 4;                    // concrete resources only

  reserved 10; // formerly identity_projection_plan (now represented by read_plan.reference_identity_projection_table_plans)

  // Concrete resources only (required when is_abstract_resource=false).
  RelationalResourceModel relational_model = 20;
  ResourceWritePlan write_plan = 21;
  ResourceReadPlan read_plan = 22;
}

enum ResourceStorageKind {
  RESOURCE_STORAGE_KIND_UNSPECIFIED = 0;
  RESOURCE_STORAGE_KIND_RELATIONAL_TABLES = 1;
  RESOURCE_STORAGE_KIND_SHARED_DESCRIPTOR_TABLE = 2;
}

message AbstractTargetPropagationKey {
  QualifiedResourceName target_resource = 1;
  DbTableName target_table = 2;
  string anchor_set_id = 3;
  string unique_constraint_name = 4;
  repeated DbColumnName target_columns_in_order = 5;
  repeated AbstractTargetPropagationKeyLineage lineage_anchors_in_id_order = 6;
  DbColumnName document_id_column = 7;
}

message AbstractTargetPropagationKeyLineage {
  string identity_lineage_id = 1;
  DbColumnName target_column = 2;
}

message LineageAnchorResolutionPlan {
  QualifiedResourceName target_resource = 1;
  DbTableName target_table = 2;
  string anchor_set_id = 3;                               // non-empty demanded variant only
  DocumentIdSetParameter document_ids = 4;
  string select_by_document_ids_sql = 5;                  // canonical dialect-specific SQL
  uint32 referenced_document_id_result_ordinal = 6;
  repeated LineageAnchorResultBinding lineage_anchors_in_id_order = 7;
  uint32 max_document_ids_per_batch = 8;
}

message DocumentIdSetParameter {
  oneof kind {
    PgsqlDocumentIdArrayParameter pgsql_array = 1;
    MssqlDocumentIdTableValuedParameter mssql_tvp = 2;
  }
}

message PgsqlDocumentIdArrayParameter {
  string parameter_name = 1;
}

message MssqlDocumentIdTableValuedParameter {
  string parameter_name = 1;
  string type_name = 2;
}

message LineageAnchorResultBinding {
  string identity_lineage_id = 1;
  uint32 result_ordinal = 2;
}

// ---------- Model ----------

message QualifiedResourceName {
  string project_name = 1;
  string resource_name = 2;
}

message DbTableName {
  string schema = 1;      // e.g. "edfi"
  string name = 2;        // e.g. "StudentSchoolAssociation"
}

message DbColumnName {
  string value = 1;       // e.g. "School_DocumentId"
}

message RelationalResourceModel {
  QualifiedResourceName resource = 1;
  string physical_schema = 2;                            // schema where this resource's tables live (e.g. "edfi")

  DbTableModel root = 10;
  reserved 11, 12;
  repeated DbTableModel tables_in_dependency_order = 13;

  repeated DocumentReferenceBinding document_reference_bindings = 20;
  repeated DescriptorEdgeSource descriptor_edge_sources = 21;
}

message DbTableModel {
  DbTableName table = 1;
  string json_scope = 2;                                 // "$", "$.addresses[*]", "$.addresses[*].periods[*]"
  bool is_json_array_scope_required = 3;                 // used for array presence rule (write [] if required)

  TableKey key = 10;
  repeated DbColumnModel columns = 11;                   // order is significant (binding + DDL)
  repeated TableConstraint constraints = 12;             // deterministic ordering by kind-group, then name
  repeated KeyUnificationClass key_unification_classes = 20;
  PersistedOccurrenceIdentity persisted_occurrence_identity = 21;
}

enum PersistedOccurrenceMatchRole {
  PERSISTED_OCCURRENCE_MATCH_ROLE_UNSPECIFIED = 0;
  PERSISTED_OCCURRENCE_MATCH_ROLE_ANCESTOR_CONTEXT = 1;
  PERSISTED_OCCURRENCE_MATCH_ROLE_SEMANTIC_IDENTITY_MEMBER = 2;
}

message PersistedOccurrenceColumnRef {
  DbTableName table = 1;
  DbColumnName column = 2;
}

message PersistedOccurrenceMaterializedWriteColumn {
  PersistedOccurrenceColumnRef column = 1;
}

message PersistedOccurrenceDocumentReferenceTargetDocumentId {
  string reference_site_id = 1;
}

message PersistedOccurrenceMatchSource {
  oneof kind {
    PersistedOccurrenceMaterializedWriteColumn materialized_write_column = 1;
    PersistedOccurrenceDocumentReferenceTargetDocumentId document_reference_target_document_id = 2;
  }
}

message PersistedOccurrenceMatchPart {
  PersistedOccurrenceMatchRole role = 1;
  optional string relative_json_path = 2;
  PersistedOccurrenceColumnRef stored_column = 3;
  PersistedOccurrenceMatchSource source = 4;
}

message PersistedOccurrenceIdentity {
  repeated PersistedOccurrenceMatchPart complete_match_in_order = 1;
  repeated DbColumnName stable_row_locator_columns_in_order = 2;
}

message TableKey {
  repeated DbKeyColumn columns = 1;                      // root/root-scope extension: [DocumentId]; collection: [CollectionItemId]; collection/common-type extension scope: [BaseCollectionItemId]
}

enum ColumnKind {
  COLUMN_KIND_UNSPECIFIED = 0;
  COLUMN_KIND_SCALAR = 1;
  COLUMN_KIND_DOCUMENT_FK = 2;
  COLUMN_KIND_DESCRIPTOR_FK = 3;
  COLUMN_KIND_ORDINAL = 4;
  COLUMN_KIND_PARENT_KEY_PART = 5;
  COLUMN_KIND_COLLECTION_KEY = 6;
  COLUMN_KIND_IDENTITY_LINEAGE_ANCHOR = 7;
  COLUMN_KIND_MIRRORED_CONTENT_VERSION = 8;
  COLUMN_KIND_MIRRORED_CONTENT_LAST_MODIFIED_AT = 9;
}

enum ScalarKind {
  SCALAR_KIND_UNSPECIFIED = 0;
  SCALAR_KIND_BOOL = 1;
  SCALAR_KIND_INT32 = 2;
  SCALAR_KIND_INT64 = 3;
  SCALAR_KIND_STRING = 4;
  SCALAR_KIND_DATE = 5;          // ISO local date
  SCALAR_KIND_DATETIME = 6;      // UTC instant
  SCALAR_KIND_DECIMAL = 7;
  SCALAR_KIND_GUID = 8;
  SCALAR_KIND_TIME = 9;
}

message RelationalScalarType {
  ScalarKind kind = 1;

  // Optional details per kind.
  uint32 string_max_length = 10;                         // for strings (0 = unspecified)
  uint32 decimal_precision = 11;                         // for decimals (0 = unspecified)
  uint32 decimal_scale = 12;                             // for decimals (0 = unspecified)
}

message DbKeyColumn {
  DbColumnName column_name = 1;
  ColumnKind kind = 2;                                   // root/root-scope extension: PARENT_KEY_PART; collection: COLLECTION_KEY; collection/common-type extension scope: PARENT_KEY_PART
}

message DbColumnModel {
  DbColumnName column_name = 1;
  ColumnKind kind = 2;
  bool is_nullable = 3;

  // For kind=SCALAR only.
  RelationalScalarType scalar_type = 10;

  // Absolute JSON path for the value in the API document (null/empty for derived columns like ordinals/key parts).
  string source_json_path = 11;

  // For kind=DOCUMENT_FK, IDENTITY_LINEAGE_ANCHOR, and DESCRIPTOR_FK.
  QualifiedResourceName target_resource = 12;

  // Required for IDENTITY_LINEAGE_ANCHOR and for a DOCUMENT_FK that is the intrinsic source of
  // a reference-backed identity lineage; empty for unrelated columns.
  string identity_lineage_id = 13;

  // Column storage semantics:
  // - Stored: a writable physical column.
  // - UnifiedAlias: a read-only generated alias of a canonical stored column (optionally presence-gated).
  ColumnStorage storage = 20;
}

message ColumnStorage {
  oneof kind {
    StoredStorage stored = 1;
    UnifiedAliasStorage unified_alias = 2;
  }
}

message StoredStorage {}

message UnifiedAliasStorage {
  DbColumnName canonical_column = 1;
  DbColumnName presence_column = 2; // optional; omitted for ungated aliases
}

message KeyUnificationClass {
  DbColumnName canonical_column = 1;
  repeated DbColumnName member_path_columns = 2;         // ordered (do not sort)
}

enum ReferentialAction {
  REFERENTIAL_ACTION_UNSPECIFIED = 0;
  REFERENTIAL_ACTION_NO_ACTION = 1;
  REFERENTIAL_ACTION_CASCADE = 2;
}

message TableConstraint {
  // Deterministic, portable constraint name (used for diagnostics).
  string name = 1;

  oneof kind {
    UniqueConstraint unique = 10;
    ForeignKeyConstraint foreign_key = 11;
    AllOrNoneNullabilityConstraint all_or_none_nullability = 12;
    NullOrTrueConstraint null_or_true = 13;
  }
}

message UniqueConstraint {
  repeated DbColumnName columns = 1;
}

message ForeignKeyConstraint {
  repeated DbColumnName columns = 1;
  DbTableName target_table = 2;
  repeated DbColumnName target_columns = 3;
  ReferentialAction on_delete = 4;
  ReferentialAction on_update = 5; // final emitted action; derivation-only MSSQL mode/certificates are not packed
  string physical_foreign_key_id = 6; // stable semantic id, assigned before name shortening
  string anchor_set_id = 7;            // stable minimal demanded subset; empty for non-document-reference FKs
  repeated ForeignKeyLineageAnchorMapping lineage_anchors_in_order = 8;
}

message ForeignKeyLineageAnchorMapping {
  string identity_lineage_id = 1;
  uint32 local_column_ordinal = 2;      // position in ForeignKeyConstraint.columns
  uint32 target_column_ordinal = 3;     // position in ForeignKeyConstraint.target_columns
}

message AllOrNoneNullabilityConstraint {
  DbColumnName fk_column = 1;
  repeated DbColumnName dependent_columns = 2;
}

message NullOrTrueConstraint {
  DbColumnName column = 1;
}

message DocumentReferenceBinding {
  bool is_identity_component = 1;
  string reference_object_path = 2;                      // wildcard path (e.g. "$.schoolReference", "$.students[*].studentReference")
  DbTableName table = 3;
  DbColumnName fk_column = 4;                            // "..._DocumentId"
  QualifiedResourceName target_resource = 5;
  repeated ReferenceIdentityBinding identity_bindings = 6; // identity field order; duplicate reference_json_path allowed only for same-site flattened reference groups
  string reference_site_id = 7;                            // stable logical-site id used by derivation diagnostics
  string anchor_set_id = 8;                                // exactly one minimal propagation-vector variant
  string physical_foreign_key_id = 9;                      // finalized deduplicated FK selected by this site
  DocumentReferenceResolutionPolicy resolution_policy = 10;
}

enum DocumentReferenceResolutionPolicy {
  DOCUMENT_REFERENCE_RESOLUTION_POLICY_UNSPECIFIED = 0;
  DOCUMENT_REFERENCE_RESOLUTION_POLICY_PRESTATEMENT_LOOKUP_ONLY = 1;
  DOCUMENT_REFERENCE_RESOLUTION_POLICY_PRESTATEMENT_LOOKUP_OR_CERTIFIED_SAME_STATEMENT = 2;
}

message ReferenceIdentityBinding {
  string reference_json_path = 1;                        // where to write in the referencing document
  DbColumnName column = 2;                               // physical column holding the identity value
}

message DescriptorEdgeSource {
  bool is_identity_component = 1;
  string descriptor_value_path = 2;                      // absolute JSON path to descriptor URI string
  DbTableName table = 3;
  DbColumnName fk_column = 4;                            // "..._DescriptorId"
  QualifiedResourceName descriptor_resource = 5;         // e.g. ("EdFi","GradeLevelDescriptor")
}

// ---------- Plans ----------

message ResourceWritePlan {
  repeated TableWritePlan table_plans = 1;               // exact relational_model.tables_in_dependency_order
  repeated SameStatementReferenceResolutionPlan same_statement_reference_resolution_plans = 2;
}

enum DmsWriteOperation {
  DMS_WRITE_OPERATION_UNSPECIFIED = 0;
  DMS_WRITE_OPERATION_PUT_BY_DOCUMENT_UUID = 1;
}

message SameStatementDirectOrigin {
  string mutation_origin_id = 1;
  DmsWriteOperation write_operation = 2;
  QualifiedResourceName resource = 3;
  DbTableName root_table = 4;
  string statement_boundary_id = 5;
}

enum PersistedReferenceRowLocatorKind {
  PERSISTED_REFERENCE_ROW_LOCATOR_KIND_UNSPECIFIED = 0;
  PERSISTED_REFERENCE_ROW_LOCATOR_KIND_ROOT_DOCUMENT_ID = 1;
  PERSISTED_REFERENCE_ROW_LOCATOR_KIND_COLLECTION_ITEM_ID = 2;
  PERSISTED_REFERENCE_ROW_LOCATOR_KIND_COMPLETE_SEMANTIC_IDENTITY = 3;
}

message StoredTargetDocumentIdSource {
  DbTableName table = 1;
  DbColumnName target_document_id_column = 2;
  PersistedReferenceRowLocatorKind row_locator_kind = 3;
  repeated DbColumnName row_locator_columns_in_order = 4;
}

message SameStatementPropagationItem {
  oneof kind {
    uint32 public_identity_ordinal = 1;
    string identity_lineage_id = 2;
    bool document_id = 3;                                // when selected, value must be true
  }
}

message SameStatementPhysicalColumnRef {
  DbTableName table = 1;
  DbColumnName column = 2;
}

message SameStatementColumnPair {
  SameStatementPhysicalColumnRef left = 1;
  SameStatementPhysicalColumnRef right = 2;
}

enum SameStatementRowCorrelationKind {
  SAME_STATEMENT_ROW_CORRELATION_KIND_UNSPECIFIED = 0;
  SAME_STATEMENT_ROW_CORRELATION_KIND_DOCUMENT_ID = 1;
  SAME_STATEMENT_ROW_CORRELATION_KIND_COLLECTION_ITEM_ID = 2;
  SAME_STATEMENT_ROW_CORRELATION_KIND_COMPLETE_UNIQUE_KEY = 3;
}

message SameStatementRowCorrelation {
  SameStatementRowCorrelationKind kind = 1;
  repeated SameStatementColumnPair complete_key_pairs = 2;
}

message SameStatementPropagationRoute {
  string mutation_origin_id = 1;
  string statement_boundary_id = 2;
  DbTableName start_table = 3;                           // always reconstruct RouteStart.MutationRow
  repeated string native_cascade_hop_physical_foreign_key_ids = 4;
  DbTableName end_table = 5;
}

enum SameStatementSetInputColumnRole {
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_UNSPECIFIED = 0;
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_CORRELATION_KEY = 1;
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_ORIGIN_DOCUMENT_ID = 2;
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_STORED_TARGET_DOCUMENT_ID = 3;
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_SUBMITTED_REFERENTIAL_ID = 4;
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_OCCURRENCE_MATCH_VALUE = 5;
  SAME_STATEMENT_SET_INPUT_COLUMN_ROLE_SUBMITTED_IDENTITY_VALUE = 6;
}

message SameStatementSetInputColumn {
  string name = 1;
  SameStatementSetInputColumnRole role = 2;
  RelationalScalarType type = 3;
}

message SameStatementReferenceSetInput {
  string parameter_name = 1;
  repeated SameStatementSetInputColumn columns_in_order = 2;
  uint32 max_instances_per_batch = 3;
}

message SameStatementCorrelationCommand {
  SameStatementReferenceSetInput input = 1;
  string select_correlated_target_document_ids_sql = 2;
  uint32 correlation_key_result_ordinal = 3;
  uint32 target_document_id_result_ordinal = 4;
  repeated SameStatementCorrelationResultBinding stored_target_values_in_vector_order = 5;
}

message SameStatementCorrelationResultBinding {
  SameStatementPropagationItem item = 1;
  SameStatementPhysicalColumnRef stored_target_column = 2;
  uint32 result_ordinal = 3;
}

message SameStatementPostWriteVerificationCommand {
  SameStatementReferenceSetInput input = 1;
  string select_resolved_targets_and_anchors_sql = 2;
  uint32 correlation_key_result_ordinal = 3;
  uint32 target_document_id_result_ordinal = 4;
  repeated LineageAnchorResultBinding lineage_anchors_in_id_order = 5;
}

message SameStatementOriginWriteBinding {
  DbTableName table = 1;
  uint32 binding_index = 2;
}

message SameStatementMaterializedWriteBinding {
  DbTableName table = 1;
  uint32 binding_index = 2;
  string input_column_name = 3;
  RelationalScalarType type = 4;
}

message SameStatementCorrelatedChangedTargetDocumentId {
  SameStatementPhysicalColumnRef current_target_document_id_column = 1;
}

message SameStatementOccurrenceMatchValueSource {
  oneof kind {
    SameStatementMaterializedWriteBinding materialized_write_binding = 1;
    SameStatementCorrelatedChangedTargetDocumentId correlated_changed_target_document_id = 2;
  }
}

message SameStatementOccurrenceMatchBinding {
  SameStatementOccurrenceMatchValueSource source = 1;
  SameStatementPhysicalColumnRef stored_receiver_column = 2;
}

message SameStatementStoredRowLocatorResultBinding {
  DbColumnName stored_row_locator_column = 1;
  uint32 result_ordinal = 2;
}

message SameStatementOccurrenceToStoredRowCorrelation {
  repeated SameStatementOccurrenceMatchBinding complete_semantic_identity_in_order = 1;
  repeated SameStatementStoredRowLocatorResultBinding stored_row_locator_results_in_order = 2;
}

message SameStatementStoredTargetColumn {
  SameStatementPhysicalColumnRef column = 1;
  uint32 correlation_result_ordinal = 2;
}

message SameStatementStoredTargetDocumentId {}

message SameStatementFutureValueSource {
  oneof kind {
    SameStatementOriginWriteBinding origin_write_binding = 1;
    SameStatementStoredTargetDocumentId stored_target_document_id = 2;
    SameStatementStoredTargetColumn stored_target_column = 3;
  }
}

message SameStatementFutureValueBinding {
  SameStatementPropagationItem item = 1;
  optional uint32 submitted_identity_binding_index = 2;  // present for public items only
  SameStatementPhysicalColumnRef future_target_column = 3;
  SameStatementFutureValueSource source = 4;
  optional string changed_value_lineage_id = 5;
  optional string submitted_identity_input_column_name = 6; // present for public items only
}

message SameStatementReferenceResolutionPlan {
  uint32 document_reference_binding_index = 1;
  string reference_site_id = 2;
  string reference_physical_foreign_key_id = 3;
  SameStatementDirectOrigin allowed_direct_origin = 4;
  string mutation_case_id = 5;
  repeated SameStatementPropagationItem changed_items_in_vector_order = 6;
  StoredTargetDocumentIdSource stored_target_document_id = 7;
  SameStatementOccurrenceToStoredRowCorrelation occurrence_to_stored_row = 8;
  SameStatementPropagationRoute retained_changed_target_route = 9;
  SameStatementRowCorrelation changed_target_to_stored_target_correlation = 10;
  SameStatementCorrelationCommand correlation_command = 11;
  SameStatementPostWriteVerificationCommand post_write_verification_command = 12;
  repeated SameStatementFutureValueBinding future_values_in_vector_order = 13;
}

message TableWritePlan {
  DbTableName table = 1;

  // SQL is dialect-specific and MUST be canonicalized (stable bytes).
  string insert_sql = 10;
  string update_sql = 11;                                // empty => not present
  string delete_by_parent_sql = 12;                      // empty => not present; collection tables leave this empty and use collection_merge_plan instead

  // Deterministic bulk-insert batching bound for this table.
  // Derived from dialect limits (e.g., SQL Server parameter limits), policy row caps, and the plan's bound column count.
  BulkInsertBatchingInfo bulk_insert_batching = 13;
  CollectionMergePlan collection_merge_plan = 14;        // omitted when not a persisted collection table

  // Parameter/value ordering for insert is defined by this list.
  repeated WriteColumnBinding column_bindings = 20;
  CollectionKeyPreallocationPlan collection_key_preallocation_plan = 21; // omitted when this table does not reserve collection-row identities

  // Empty when this table has no key-unification classes.
  repeated KeyUnificationWritePlan key_unification_plans = 30;
}

message CollectionMergePlan {
  repeated CollectionMergeSemanticIdentityBinding semantic_identity_bindings = 2; // required and non-empty for persisted multi-item collection scopes
  uint32 stable_row_identity_binding_index = 12;         // index into TableWritePlan.column_bindings
  string update_by_stable_row_identity_sql = 13;         // canonical UPDATE keyed by the stable row identity binding
  string delete_by_stable_row_identity_sql = 14;         // canonical DELETE keyed by the stable row identity binding
  uint32 ordinal_binding_index = 15;                     // index into TableWritePlan.column_bindings
  repeated uint32 compare_binding_indexes_in_order = 16; // ordered projection of stored/writable values for compare/no-op work
}

message CollectionMergeSemanticIdentityBinding {
  string relative_path = 1;                              // relative to table scope node
  uint32 binding_index = 2;                              // index into TableWritePlan.column_bindings
}

message CollectionKeyPreallocationPlan {
  DbColumnName column_name = 1;
  uint32 binding_index = 2;                              // index into TableWritePlan.column_bindings
}

message BulkInsertBatchingInfo {
  uint32 max_rows_per_batch = 1;
  uint32 parameters_per_row = 2;
  uint32 max_parameters_per_command = 3;
}

message WriteColumnBinding {
  DbColumnName column = 1;
  WriteValueSource source = 2;
  string parameter_name = 3;                             // bare name; SQL uses "@{parameter_name}"
}

message WriteValueSource {
  oneof kind {
    WriteDocumentId document_id = 1;
    WriteParentKeyPart parent_key_part = 2;
    WriteOrdinal ordinal = 3;
    WriteScalar scalar = 4;
    WriteDocumentReference document_reference = 5;
    WriteDescriptorReference descriptor_reference = 6;
    WritePrecomputed precomputed = 7;
    WriteIdentityLineageAnchor identity_lineage_anchor = 8;
  }
}

message WriteDocumentId {}

message WritePrecomputed {}

message WriteParentKeyPart {
  uint32 index = 1;                                      // index in parent scope locator array
}

message WriteOrdinal {}

message WriteScalar {
  string relative_path = 1;                              // relative to table scope node
  RelationalScalarType scalar_type = 2;
}

message WriteDocumentReference {
  uint32 binding_index = 1;                              // index into RelationalResourceModel.document_reference_bindings
}

message WriteIdentityLineageAnchor {
  uint32 binding_index = 1;                              // same concrete reference-instance index as document_reference
  string identity_lineage_id = 2;                        // stable semantic lineage id
}

message WriteDescriptorReference {
  string descriptor_value_path = 1;                      // absolute descriptor value path (or relative to scope)
  string relative_path = 2;                              // relative to table scope node (preferred for perf)
  QualifiedResourceName descriptor_resource = 3;
}

message KeyUnificationWritePlan {
  DbColumnName canonical_column = 1;
  uint32 canonical_binding_index = 2;                    // index into TableWritePlan.column_bindings
  repeated KeyUnificationMemberWritePlan members_in_order = 3; // ordered (do not sort)
}

message KeyUnificationMemberWritePlan {
  DbColumnName member_path_column = 1;                   // member binding column name (typically a UnifiedAlias)
  string relative_path = 2;                              // relative to table scope node; empty => value-at-scope
  ColumnKind kind = 3;                                   // must be SCALAR or DESCRIPTOR_FK
  RelationalScalarType scalar_type = 4;                  // for kind=SCALAR
  QualifiedResourceName descriptor_resource = 5;         // for kind=DESCRIPTOR_FK
  DbColumnName presence_column = 6;                      // optional; omitted when ungated
  optional uint32 presence_binding_index = 7;            // index into TableWritePlan.column_bindings when present
  bool presence_is_synthetic = 8;
}

message ResourceReadPlan {
  repeated TableReadPlan table_plans = 1;                // unique by table; order matches relational_model.tables_in_dependency_order
  repeated ReferenceIdentityProjectionTablePlan reference_identity_projection_table_plans = 2; // dependency-order subset by table
  repeated DescriptorProjectionPlan descriptor_projection_plans = 3; // deterministic execution order
}

message TableReadPlan {
  DbTableName table = 1;
  string select_by_keyset_sql = 10;                      // expects a materialized keyset table with BIGINT DocumentId
}

message ReferenceIdentityProjectionTablePlan {
  DbTableName table = 1;
  repeated ReferenceIdentityProjectionBinding bindings_in_order = 2; // ordered; do not sort
}

message ReferenceIdentityProjectionBinding {
  bool is_identity_component = 1;
  string reference_object_path = 2;                      // canonical JsonPath string
  QualifiedResourceName target_resource = 3;
  uint32 fk_column_ordinal = 4;                          // zero-based hydration select-list ordinal
  repeated ReferenceIdentityProjectionFieldOrdinal identity_field_ordinals_in_order = 5; // ordered logical fields; do not sort; reference_json_path values must be distinct within a binding
}

message ReferenceIdentityProjectionFieldOrdinal {
  string reference_json_path = 1;                        // canonical path written under reference_object_path
  uint32 column_ordinal = 2;                             // zero-based hydration select-list ordinal
}

message DescriptorProjectionPlan {
  string select_by_keyset_sql = 1;                       // page-batched SQL, no runtime SQL parsing for shape/binding
  DescriptorProjectionResultShape result_shape = 2;
  repeated DescriptorProjectionSource sources_in_order = 3; // ordered; do not sort
}

message DescriptorProjectionResultShape {
  uint32 descriptor_id_ordinal = 1;                      // zero-based descriptor result-row ordinal
  uint32 uri_ordinal = 2;                                // zero-based descriptor result-row ordinal
}

message DescriptorProjectionSource {
  string descriptor_value_path = 1;                      // canonical descriptor JSON path
  DbTableName table = 2;
  QualifiedResourceName descriptor_resource = 3;
  uint32 descriptor_id_column_ordinal = 4;               // zero-based hydration select-list ordinal
}
```

---

## 8. Contracts package (recommended)

To avoid checked-in generated protobuf code in the main DMS repo and avoid requiring `protoc` in every consumer build:

- Own the `.proto` file(s) above in a small “contracts” project/repo.
- Generate C# types via `Grpc.Tools` and ship them as a NuGet package used by:
  - pack producers (builder CLI),
  - pack consumers (DMS runtime),
  - any validation/test harnesses.

Suggested package: `EdFi.DataManagementService.MappingPacks.Contracts`.

---

## 9. Compatibility and evolution rules

### 9.1 `PackFormatVersion`

`PackFormatVersion` is a strict protocol gate.

The rules below begin after the complete schema in this document becomes the first supported v1 pack contract. Earlier
branch-local/draft v1 schemas and packs are not compatibility inputs: this design may renumber draft enum values or add
required fields in place, and every such pre-production pack is regenerated. Consequently, the finalized
`ReferentialAction` zero sentinel and other DMS-1129 additions do not require a `PackFormatVersion` or
`RelationalMappingVersion` bump.

Bump it only for breaking changes to:
- envelope structure,
- payload compression/encryption semantics,
- protobuf schema changes that are not wire-compatible (field renames/removals with tag reuse),
- any change where an older consumer would misinterpret bytes.

### 9.2 `RelationalMappingVersion`

`RelationalMappingVersion` remains part of `EffectiveSchemaHash` and the envelope selection key (see
`reference/design/backend-redesign/design-docs/data-model.md`). It is reserved for evolution after the first supported
production mapping contract is established.

The backend redesign, key unification, and DMS-1129 cascade rules are all being finalized before DMS has a production
deployment. They therefore define the v1 baseline in place: no `RelationalMappingVersion` bump or database migration is
part of this design. No additional physical-model hash is introduced; producers and consumers implement the same complete
v1 schema, and pre-production packs are regenerated from it.

SQL Server `MssqlPropagationMode` and `CoverageCertificate` values are intentionally absent from the pack. They are
derivation/DDL/manifest diagnostics, while runtime consumers need only the finalized `ForeignKeyConstraint.on_update`
value together with the expanded FK vector and stable ids. A failed SQL Server value-flow or 1785 analysis produces no
mapping pack. PostgreSQL pack production has no cascade classifier or unsafe-topology failure path.

---
