# Backend Redesign Summary: Relational Primary Store (Tables per Resource)

Status: Draft (summary of the draft design docs in this directory).

This redesign replaces the current three-table JSON document store (`Document`/`Alias`/`Reference`) with a relational primary store using tables per resource, while keeping DMS behavior metadata-driven via `ApiSchema.json`.

Source documents:
- Overview: `reference/design/backend-redesign/design-docs/overview.md`
- Data model: `reference/design/backend-redesign/design-docs/data-model.md`
- Flattening & reconstitution: `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- Unified mapping models (in-memory shape): `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`
- AOT compilation (optional mapping pack distribution): `reference/design/backend-redesign/design-docs/aot-compilation.md`
- Mapping pack file format (normative `.mpack` schema): `reference/design/backend-redesign/design-docs/mpack-format-v1.md`
- Extensions (`_ext`): `reference/design/backend-redesign/design-docs/extensions.md`
- Transactions & concurrency: `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- Update tracking (`_etag/_lastModifiedDate`, ChangeVersion): `reference/design/backend-redesign/design-docs/update-tracking.md`
- DDL generation: `reference/design/backend-redesign/design-docs/ddl-generation.md`
- DDL generator verification harness: `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`
- Strengths/risks: `reference/design/backend-redesign/design-docs/strengths-risks.md`

> Note on update tracking: `update-tracking.md` is the normative design for `_etag/_lastModifiedDate` and Change Queries. Where other docs describe read-time derivation or reverse-edge expansion, treat them as superseded.

## Goals and explicit decisions (high level)

- Canonical storage is relational (root table per resource, child tables per collection) and is the source of truth.
- DMS remains schema/behavior-driven by `ApiSchema.json` (no handwritten per-resource code; no checked-in per-resource SQL artifacts).
- Relationships are stored as stable `DocumentId` foreign keys, with referenced identity natural-key fields co-stored alongside each `..._DocumentId` and kept consistent via composite FKs with `ON UPDATE CASCADE` (no FK rewrites).
- Keep `ReferentialId` (UUIDv5 of `(ProjectName, ResourceName, DocumentIdentity)`) as the uniform natural-identity key for resolution and upserts.
- SQL Server + PostgreSQL parity is required.
- Authorization is intentionally out of scope for this redesign phase.
- DMS does not hot-reload or auto-migrate schemas in-process; it validates schema compatibility per database on first use of that database connection string (cached) via an effective schema fingerprint and fails fast if no matching mapping is available.

## Core concepts and terms

- `DocumentUuid`: stable external identifier for API `id` (does not change on identity updates).
- `DocumentId`: internal surrogate key (`bigint`) used for FKs and clustering.
- `ReferentialId`: deterministic UUIDv5 used as the canonical “natural identity key”; stored in `dms.ReferentialIdentity`.
- **Identity component**: a reference whose projected identity participates in a document’s identity (`identityJsonPaths`). Identity-component values are stored locally (as propagated reference identity columns) so referential ids can be recomputed row-locally.
- **Representation dependency** (1 hop): any referenced non-descriptor document whose identity values are embedded in the returned JSON representation. Indirect representation changes are realized as FK-cascade updates to stored reference identity columns, which trigger normal stamping of stored `_etag/_lastModifiedDate/ChangeVersion`.

## Data model summary

### `dms.*` core tables

`reference/design/backend-redesign/design-docs/data-model.md` defines the baseline core tables, with update tracking extended by `reference/design/backend-redesign/design-docs/update-tracking.md`:

- `dms.ResourceKey`
  - Lookup table mapping `(ProjectName, ResourceName)` to `ResourceKeyId` (small surrogate id).
  - Seeded deterministically by the DDL generation utility for a given `EffectiveSchemaHash` and validated/cached by DMS.

- `dms.Document`
  - One row per persisted resource instance.
  - Holds `DocumentId`, `DocumentUuid`, `ResourceKeyId` (resource type), and update-tracking token columns (see below).
  - `DocumentUuid` is unique and stable across identity updates.

- `dms.ReferentialIdentity`
  - Maps `ReferentialId → DocumentId` for all identities:
    - self-contained identities,
    - reference-bearing identities (kept correct transactionally via DB cascades + per-resource triggers),
    - descriptor identities (resource type + normalized URI),
    - polymorphic/abstract reference support via superclass/abstract alias rows (documents have ≤ 2 referential ids: primary + optional superclass alias).
  - Physical guidance: do not cluster on random UUID in SQL Server; cluster on a sequential key like `(DocumentId, ResourceKeyId)`.

- `dms.Descriptor` (unified)
  - Unified descriptor table keyed by the descriptor document’s `DocumentId` so descriptor references can FK to `dms.Descriptor(DocumentId)` without per-descriptor tables.
  - Used for “is a descriptor” enforcement and (optionally) type diagnostics/validation.

- `dms.EffectiveSchema` + `dms.SchemaComponent`
  - Records `EffectiveSchemaHash` (SHA-256 fingerprint) of the effective core+extension `ApiSchema.json` set as it affects relational mapping.
  - `dms.EffectiveSchema` is a singleton current-state row (includes `ResourceKeyCount` and `ResourceKeySeedHash` for fast `dms.ResourceKey` validation; `ResourceKeySeedHash` is raw SHA-256 bytes, 32 bytes); `dms.SchemaComponent` rows are keyed by `EffectiveSchemaHash`.
  - On first use of a given database connection string, DMS reads the database fingerprint, caches it, and selects the matching mapping set (rejecting requests for that database if mismatched/unsupported).

### Update tracking additions (unified design)

`reference/design/backend-redesign/design-docs/update-tracking.md` adds representation-sensitive metadata using write-time stamping, with indirect impacts realized via FK cascades to stored reference identity columns:

- Global sequence: `dms.ChangeVersionSequence` (`bigint`).
- `dms.Document` token columns:
  - `ContentVersion` (global monotonic stamp for representation changes; also serves as `ChangeVersion`).
  - `IdentityVersion` (global monotonic stamp for identity projection changes).
  - `ContentLastModifiedAt`, `IdentityLastModifiedAt`.
- Journal (append-only):
  - `dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId, CreatedAt)` emitted when `ContentVersion` changes.
- Served metadata:
  - `_etag` derived from (or stored alongside) `ContentVersion`.
  - `_lastModifiedDate` served from `ContentLastModifiedAt`.

### Per-project schemas and resource tables

For each project, create a physical schema derived from `ProjectEndpointName` (e.g., `ed-fi` → `edfi`), with:

- Root table `{schema}.{ResourceName}`:
  - PK `DocumentId` (FK to `dms.Document(DocumentId)` ON DELETE CASCADE).
  - Unique constraint for the resource’s natural key derived from `identityJsonPaths`:
    - scalar identity elements become scalar columns,
    - identity elements sourced from reference objects use the corresponding `..._DocumentId` FK columns (stable), with referenced identity values additionally stored in `{RefBaseName}_{IdentityPart}` columns for propagation and query.
  - Reference FK columns:
    - for each document reference site: store both `..._DocumentId` and `{RefBaseName}_{IdentityPart}` columns, with a composite FK to the target identity key `(DocumentId, <IdentityParts...>)` using `ON UPDATE CASCADE`,
    - polymorphic targets: composite FK to `{schema}.{AbstractResource}Identity(DocumentId, <AbstractIdentityParts...>)` using `ON UPDATE CASCADE`,
    - descriptors: FK to `dms.Descriptor(DocumentId)` via `..._DescriptorId`.

- Collection tables `{schema}.{Resource}_{CollectionPath}`:
  - Composite parent+ordinal keys to preserve order and enable batching:
    - PK is `(DocumentId, <ParentOrdinals...>, Ordinal)`.
  - Nested collections add ancestor ordinals into the key and FK (no generated IDs; avoids `RETURNING`/`OUTPUT` capture).
  - Optional unique constraints from `arrayUniquenessConstraints`.

- Abstract identity artifacts:
  - `{schema}.{AbstractResource}Identity` tables provide FK targets for polymorphic references with cascade support.
  - `{schema}.{AbstractResource}_View` union views remain useful for query/diagnostics but are no longer required to project reference identity values in responses.

### Extensions (`_ext`) mapping

`reference/design/backend-redesign/design-docs/extensions.md` defines `_ext` mapping rules:

- Extension tables live in the extension project schema (e.g., `sample`, `tpdm`), not in the core schema.
- Resource-level `_ext.{project}` becomes `{projectSchema}.{Resource}Extension` keyed by `DocumentId` (1:1) with FK to the base resource root.
- `_ext` inside common types/collections becomes scope-aligned extension tables keyed exactly like the base scope they extend (DocumentId + ordinals), with FK back to the base scope table.
- Arrays inside `_ext` become extension child tables using the same parent+ordinal key strategy.
- References/descriptors inside extensions follow the same FK rules as core mapping (`..._DocumentId` and `..._DescriptorId`).

## Derived mapping and plan compilation (no codegen)

`reference/design/backend-redesign/design-docs/flattening-reconstitution.md` describes how DMS derives a full relational mapping from `ApiSchema.json` at startup and compiles it into read/write plans:

- Inputs: `jsonSchemaForInsert` (fully dereferenced/expanded; no `$ref`, `oneOf`/`anyOf`/`allOf`, `enum`),
  `documentPathsMapping`, `identityJsonPaths`, `arrayUniquenessConstraints`, `abstractResources`, and optional
  `resourceSchema.relational` naming overrides.
- Optional `resourceSchema.relational` block provides deterministic name overrides without enumerating full flattening metadata:
  - `rootTableNameOverride`
  - `nameOverrides` keyed by restricted JSONPaths (`$.x.y` for column base names, `$.arr[*]` for collection base names).
- Derived model includes:
  - table/column lists and types,
  - FK/descriptor bindings (including propagated reference identity columns and cascade semantics),
  - query compilation mappings (including reference-identity fields mapped to local columns),
  - update-stamping trigger plans (resource-table changes → `dms.Document` stamps/journals).

## Write path (POST upsert / PUT by id)

Combined view from `transactions-and-concurrency.md`, `flattening-reconstitution.md`, and `update-tracking.md`:

1. **Core validation and extraction**
   - Core canonicalizes JSON and produces `DocumentInfo`:
     - resource `ReferentialId`,
     - descriptor references (already include concrete JSON paths),
     - document references with `ReferentialId`s.
   - Required Core change for this redesign: add concrete indexed JSON locations to document reference instances (`DocumentReference.Path`) so nested-collection reference FKs can be populated without per-row JSONPath evaluation/hashing.

2. **Bulk reference and descriptor resolution**
   - Resolve all referential ids in bulk via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
   - For descriptor references, validate “is a descriptor” via `dms.Descriptor` (and optionally enforce expected discriminator/type in application code).

3. **DB-enforced identity propagation**
   - Composite foreign keys with `ON UPDATE CASCADE` keep stored reference identity columns consistent when referenced identities change.
   - Identity-changing writes may optionally be serialized (advisory/application lock) as an operational guardrail, but correctness does not depend on an application-managed lock table.

4. **Flatten and write relational rows (single transaction)**
   - Insert/update `dms.Document`.
   - Write root table row (insert/update).
   - Write child tables using a baseline replace strategy (delete by parent key, bulk insert rows), with batching to respect SQL Server parameter limits.
   - Write extension tables similarly (root extension rows only when extension values exist; scope-aligned rows for nested extension sites).
   - For each document reference site, persist both:
     - the stable `..._DocumentId` FK column (resolved from `dms.ReferentialIdentity`), and
     - the referenced identity natural-key columns `{RefBaseName}_{IdentityPart}` (from the request body),
     enforced by composite FKs.

5. **Strict identity maintenance (row-local triggers)**
   - Per-resource triggers recompute `dms.ReferentialIdentity` when a document’s identity projection columns change (directly or via cascaded updates to identity-component reference identity columns).
   - Identity changes therefore propagate transitively via FK cascades, without application-managed closure traversal.

6. **Update tracking (stored metadata + journal)**
   - Any representation-affecting change (including cascaded updates to stored reference identity columns) bumps `dms.Document.ContentVersion/ContentLastModifiedAt`.
   - Identity projection changes additionally bump `dms.Document.IdentityVersion/IdentityLastModifiedAt`.
   - Emit `dms.DocumentChangeEvent` rows via triggers when `ContentVersion` changes.

## Read path (GET by id / query)

Combined view from `transactions-and-concurrency.md`, `flattening-reconstitution.md`, and `update-tracking.md`:

- **GET by id**
  1. Resolve `DocumentUuid → DocumentId`.
  2. Hydrate relational tables and reconstitute JSON; serve `_etag/_lastModifiedDate/ChangeVersion` from `dms.Document` and reference identity fields from stored reference identity columns.

- **Query**
  - Query compilation is constrained to root-table paths (`queryFieldMapping` does not cross array boundaries).
  - Page selection is done over the resource root table, ordered by `DocumentId` (ascending).
  - Reconstitution is page-based (not “GET by id N times”):
    - materialize a page keyset of `DocumentId`s,
    - hydrate root + child + extension tables by joining each table to the page keyset in one command (multiple result sets),
    - batch descriptor URI lookups,
    - serve `_etag/_lastModifiedDate/ChangeVersion` from `dms.Document` without dependency-token expansion.

## Schema management and DDL generation

`reference/design/backend-redesign/design-docs/ddl-generation.md` + `data-model.md` define:

- A separate DDL generation utility that:
  - loads the configured core+extension `ApiSchema.json` files,
  - computes `EffectiveSchemaHash`,
  - derives the same relational model as runtime,
  - emits (and optionally provisions) deterministic DDL for PostgreSQL and SQL Server for:
    - core `dms.*` tables,
    - per-project schemas and per-resource tables,
    - extension schemas/tables,
    - abstract identity tables (and optional union views),
    - update tracking sequences and triggers,
  - records the singleton `dms.EffectiveSchema` row (including `ResourceKeyCount` and `ResourceKeySeedHash`) and `dms.SchemaComponent` rows keyed by `EffectiveSchemaHash`.
  - provision semantics: create-only (no migrations), optional database creation as a pre-step, and a single transaction for schema + seeds.
- (Optional) ahead-of-time mapping pack compilation and file distribution keyed by `EffectiveSchemaHash` to avoid runtime plan compilation under load (see `reference/design/backend-redesign/design-docs/aot-compilation.md`).
- DMS runtime remains validate-only and fails fast on schema mismatch per database (no in-process migration/hot reload).

## Key risks and mitigations (from the docs)

- **Cascade feasibility and fan-out**
  - `ON UPDATE CASCADE` can hit SQL Server “multiple cascade paths” / cycle restrictions; some sites may require trigger-based propagation.
  - Identity updates on “hub” documents can synchronously update many dependent rows; needs guardrails, telemetry, and a deadlock retry policy.

- **Trigger correctness and multi-row stamping**
  - Stamping must produce per-row unique `ChangeVersion` values (especially for SQL Server multi-row cascade updates) and must cover changes across root + child + extension tables.

- **Read amplification**
  - Reconstitution can be expensive for deep resources (many child tables/result sets); benchmark representative deep resources early and treat read-path performance as a first-class requirement.

- **Very large scale tables**
  - `dms.Document` scale: avoid wide repeated strings in hot tables (use `ResourceKeyId` for `(ProjectName, ResourceName)` and store `ResourceVersion` on `dms.ResourceKey`, denormalizing only where needed for CDC/streaming).
  - `dms.ReferentialIdentity` and `dms.DocumentChangeEvent` require careful indexing and may be large at scale; validate index and clustering choices per engine.
  
