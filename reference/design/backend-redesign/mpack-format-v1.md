# Backend Redesign: Mapping Pack File Format v1 (`.mpack`)

Status: Draft (**normative** file/serialization contract for Mapping Pack *PackFormatVersion=1*).

This document defines the on-disk bytes for a **Mapping Pack** (“`.mpack`”) as referenced by:

- AOT compilation overview: `reference/design/backend-redesign/aot-compilation.md`
- Flattening & reconstitution models/plans: `reference/design/backend-redesign/flattening-reconstitution.md`
- Effective schema fingerprinting: `reference/design/backend-redesign/data-model.md` (`EffectiveSchemaHash`)
- DDL generator workflow and `dms.ResourceKey` seeding: `reference/design/backend-redesign/ddl-generation.md`

Authorization is intentionally out of scope.

---

## 1. Purpose

The `.mpack` format is a redistributable artifact that contains **dialect-specific, precompiled relational mapping artifacts** for one effective schema:

- deterministic `dms.ResourceKey` seed mapping for that schema
- per-resource relational models (tables/columns/paths) needed by generic flatten/reconstitute
- per-resource, dialect-specific SQL plans (write/read/identity projection)

The consumer (DMS runtime) MUST be able to execute schema-dependent relational work for that effective schema **without compiling** models or SQL from `ApiSchema.json` at runtime (but Core may still load `ApiSchema.json` for validation and identity extraction).

---

## 2. Pack identity (selection key)

Mapping packs are selected strictly by this 4-tuple:

1. `EffectiveSchemaHash` (lowercase hex, 64 chars) — see `reference/design/backend-redesign/data-model.md`
2. `SqlDialect` (`PGSQL` or `MSSQL`)
3. `RelationalMappingVersion` (DMS-controlled string constant; bump when mapping rules change)
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

All collections are `repeated` and MUST be emitted in stable sort order:

- `resource_keys`: ascending by `(resource_key_id)` (and then by `(project_name, resource_name)` for tie-breaking, though ties are invalid).
- `resources`: ascending by `(project_name, resource_name)` using ordinal (culture-invariant) string ordering.
- Within each `RelationalResourceModel`:
  - `tables_in_read_dependency_order`: root-first, then increasing depth; stable within depth by `(json_scope, table_name)`
  - `tables_in_write_dependency_order`: root-first, then depth-first; stable within sibling set by `(json_scope, table_name)`
  - `columns`: stable per table; key columns first (in key order), then non-key columns in deterministic JSON-path order
  - `constraints`: ascending by `(name)`
  - `document_reference_edge_sources`: ascending by `(reference_object_path)`
  - `descriptor_edge_sources`: ascending by `(descriptor_value_path)`
  - `reference_field_mappings`: preserve ApiSchema `referenceJsonPaths` order (identity field order)

### 4.3 SQL text canonicalization

All SQL strings embedded in the pack MUST be byte-for-byte stable for a fixed key.

Follow the canonicalization requirements in:

- `reference/design/backend-redesign/flattening-reconstitution.md` (“SQL text canonicalization (required)”)

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

- every concrete `resourceSchema` in the effective schema (including descriptors), and
- every `abstractResources[*]` name (used for polymorphic/superclass alias behavior).

`resource_key_id` MUST fit in SQL `smallint` (≤ 32767).

### 5.2 Seed hash requirements

The payload MUST include `resource_key_seed_hash` and `resource_key_count`, computed as:

- `resource_key_count = number of entries`
- `resource_key_seed_hash = SHA-256(UTF8(manifest))`

Where `manifest` is:

```text
resource-key-seed-hash:v1\n
{resource_key_id}|{project_name}|{resource_name}|{resource_version}\n
...
```

Entries are emitted in the same order as `resource_keys` (ascending `resource_key_id`).

### 5.3 Runtime validation against database

Consumers MUST validate that the target database’s `dms.ResourceKey` contents match the pack’s embedded seed mapping for the same effective schema.

Recommended fast-path (depends on DDL design):
- read `ResourceKeyCount` + `ResourceKeySeedHash` from `dms.EffectiveSchema` for the database’s `EffectiveSchemaHash`, compare to payload

Required slow-path fallback:
- read `dms.ResourceKey` ordered by `ResourceKeyId` and diff vs payload, then fail fast on mismatch

Rationale and seeding algorithm guidance: `reference/design/backend-redesign/ddl-generation.md`.

---

## 6. Consumer validation algorithm (normative)

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
     - has `identity_projection_plan`
     - if `is_abstract=false`, has `relational_model`, `write_plan`, and `read_plan`
     - all referenced tables/columns/edge-sources referenced by plans exist in the model
7. Validate `dms.ResourceKey` mapping in the target database (fast path if available; otherwise full diff) and cache:
   - `(ProjectName, ResourceName) -> ResourceKeyId`
   - `ResourceKeyId -> (ProjectName, ResourceName, ResourceVersion)`

If any step fails, the consumer MUST reject the pack and treat it as unusable for that database.

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

  // Identity projection required for reference reconstitution:
  // - concrete resources: project from root tables (and joins for reference-bearing identities)
  // - abstract resources: project from {AbstractResource}_View
  IdentityProjectionPlan identity_projection_plan = 10;

  // Concrete resources only (required when is_abstract_resource=false).
  RelationalResourceModel relational_model = 20;
  ResourceWritePlan write_plan = 21;
  ResourceReadPlan read_plan = 22;
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
  repeated DbTableModel tables_in_read_dependency_order = 11;
  repeated DbTableModel tables_in_write_dependency_order = 12;

  repeated DocumentReferenceEdgeSource document_reference_edge_sources = 20;
  repeated DescriptorEdgeSource descriptor_edge_sources = 21;
}

message DbTableModel {
  DbTableName table = 1;
  string json_scope = 2;                                 // "$", "$.addresses[*]", "$.addresses[*].periods[*]"
  bool is_json_array_scope_required = 3;                 // used for array presence rule (write [] if required)

  TableKey key = 10;
  repeated DbColumnModel columns = 11;                   // order is significant (binding + DDL)
  repeated TableConstraint constraints = 12;             // deterministic ordering by name
}

message TableKey {
  repeated DbKeyColumn columns = 1;                      // root: [DocumentId]; child: [ParentKeyParts..., Ordinal]
}

enum ColumnKind {
  COLUMN_KIND_UNSPECIFIED = 0;
  COLUMN_KIND_SCALAR = 1;
  COLUMN_KIND_DOCUMENT_FK = 2;
  COLUMN_KIND_DESCRIPTOR_FK = 3;
  COLUMN_KIND_ORDINAL = 4;
  COLUMN_KIND_PARENT_KEY_PART = 5;
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
  ColumnKind kind = 2;                                   // must be PARENT_KEY_PART or ORDINAL
}

message DbColumnModel {
  DbColumnName column_name = 1;
  ColumnKind kind = 2;
  bool is_nullable = 3;

  // For kind=SCALAR only.
  RelationalScalarType scalar_type = 10;

  // Absolute JSON path for the value in the API document (null/empty for derived columns like ordinals/key parts).
  string source_json_path = 11;

  // For kind=DOCUMENT_FK and kind=DESCRIPTOR_FK.
  QualifiedResourceName target_resource = 12;
}

message TableConstraint {
  // Deterministic, portable constraint name (used for diagnostics).
  string name = 1;

  oneof kind {
    UniqueConstraint unique = 10;
    ForeignKeyConstraint foreign_key = 11;
  }
}

message UniqueConstraint {
  repeated DbColumnName columns = 1;
}

message ForeignKeyConstraint {
  repeated DbColumnName columns = 1;
  DbTableName target_table = 2;
  repeated DbColumnName target_columns = 3;
}

message DocumentReferenceEdgeSource {
  bool is_identity_component = 1;
  string reference_object_path = 2;                      // wildcard path (e.g. "$.schoolReference", "$.students[*].studentReference")
  DbTableName table = 3;
  DbColumnName fk_column = 4;                            // "..._DocumentId"
  QualifiedResourceName target_resource = 5;
  repeated ReferenceFieldMapping field_mappings = 6;     // identity field order
}

message ReferenceFieldMapping {
  string reference_json_path = 1;                        // where to write in the referencing document
  string target_identity_json_path = 2;                  // where to read from referenced identity projection
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
  repeated TableWritePlan table_plans = 1;               // unique by table
}

message TableWritePlan {
  DbTableName table = 1;

  // SQL is dialect-specific and MUST be canonicalized (stable bytes).
  string insert_sql = 10;
  string update_sql = 11;                                // empty => not present
  string delete_by_parent_sql = 12;                      // empty => not present

  // Parameter/value ordering for insert is defined by this list.
  repeated WriteColumnBinding column_bindings = 20;
}

message WriteColumnBinding {
  DbColumnName column = 1;
  WriteValueSource source = 2;
}

message WriteValueSource {
  oneof kind {
    WriteDocumentId document_id = 1;
    WriteParentKeyPart parent_key_part = 2;
    WriteOrdinal ordinal = 3;
    WriteScalar scalar = 4;
    WriteDocumentReference document_reference = 5;
    WriteDescriptorReference descriptor_reference = 6;
  }
}

message WriteDocumentId {}

message WriteParentKeyPart {
  uint32 index = 1;                                      // index in parent key parts array
}

message WriteOrdinal {}

message WriteScalar {
  string relative_path = 1;                              // relative to table scope node
  RelationalScalarType scalar_type = 2;
}

message WriteDocumentReference {
  string reference_object_path = 1;                      // matches DocumentReferenceEdgeSource.reference_object_path
}

message WriteDescriptorReference {
  string descriptor_value_path = 1;                      // absolute descriptor value path (or relative to scope)
  string relative_path = 2;                              // relative to table scope node (preferred for perf)
  QualifiedResourceName descriptor_resource = 3;
}

message ResourceReadPlan {
  repeated TableReadPlan table_plans = 1;                // unique by table
}

message TableReadPlan {
  DbTableName table = 1;
  string select_by_keyset_sql = 10;                      // expects a materialized keyset table with BIGINT DocumentId
}

message IdentityProjectionPlan {
  QualifiedResourceName resource = 1;
  string sql = 10;                                       // returns DocumentId + identity fields (stable aliases)
  repeated IdentityField fields = 11;                    // ApiSchema identity order
}

message IdentityField {
  string identity_json_path = 1;                         // where to write in reference object (e.g. "$.schoolId")
  string sql_alias = 2;                                  // column alias in projection result
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

Bump it only for breaking changes to:
- envelope structure,
- payload compression/encryption semantics,
- protobuf schema changes that are not wire-compatible (field renames/removals with tag reuse),
- any change where an older consumer would misinterpret bytes.

### 9.2 `RelationalMappingVersion`

`RelationalMappingVersion` is bumped when mapping rules change, even if `ApiSchema.json` content is unchanged.

It is expected to be included in `EffectiveSchemaHash` computation (see `reference/design/backend-redesign/data-model.md`), and is also present in the envelope key for defense-in-depth.

---
