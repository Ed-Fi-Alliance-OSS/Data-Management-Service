# Backend Redesign Summary: Relational Primary Store (Tables per Resource)

Status: Draft (summary of the draft design docs in this directory).

This redesign replaces the current three-table JSON document store (`Document`/`Alias`/`Reference`) with a relational primary store using tables per resource, while keeping DMS behavior metadata-driven via `ApiSchema.json`.

Source documents:
- Overview: `reference/design/backend-redesign/design-docs/overview.md`
- Data model: `reference/design/backend-redesign/design-docs/data-model.md`
- Authentication & authorization: `reference/design/backend-redesign/design-docs/auth.md`
- Key unification (canonical columns + generated aliases; presence-gated when optional): `reference/design/backend-redesign/design-docs/key-unification.md`
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
- Relationships are stored as stable `DocumentId` foreign keys, with referenced identity natural-key fields available locally for query/reconstitution and kept consistent via dialect-specific propagation rules (no FK rewrites): PostgreSQL uses `ON UPDATE CASCADE` for abstract targets and concrete targets with `allowIdentityUpdates=true` (`ON UPDATE NO ACTION` otherwise), while SQL Server uses `ON UPDATE NO ACTION` for all reference composite FKs and `DbTriggerKind.IdentityPropagationFallback` triggers for eligible propagation targets (abstract targets and concrete targets with `allowIdentityUpdates=true`). Under key unification, equality-constrained per-site/per-path bindings may be generated/persisted, presence-gated aliases of canonical stored columns (see `key-unification.md`).
- Keep `ReferentialId` (UUIDv5 of `(ProjectName, ResourceName, DocumentIdentity)`) as the uniform natural-identity key for resolution and upserts.
- SQL Server + PostgreSQL parity is required.
- Authentication & authorization are addressed in [auth.md](auth.md), including:
  - token-derived authorization context (EdOrgIds, namespace prefixes, ownership tokens),
  - `auth.*` companion objects, and
  - how authorization is applied to CRUD and paging queries.
- DMS does not hot-reload or auto-migrate schemas in-process; it validates schema compatibility per database on first use of that database connection string (cached) via an effective schema fingerprint and fails fast if no matching mapping is available.

## Core concepts and terms

- `DocumentUuid`: stable external identifier for API `id` (does not change on identity updates).
- `DocumentId`: internal surrogate key (`bigint`) used for FKs and clustering.
- `ReferentialId`: deterministic UUIDv5 used as the canonical “natural identity key”; stored in `dms.ReferentialIdentity`.
- **Identity component**: a reference whose projected identity participates in a document’s identity (`identityJsonPaths`). Identity-component values are stored locally as reference-identity bindings (which may be generated/persisted aliases of canonical stored columns under key unification) so referential ids can be recomputed row-locally.
- **Representation dependency** (1 hop): any referenced non-descriptor document whose identity values are embedded in the returned JSON representation. Indirect representation changes are realized as database-driven propagation updates to canonical stored identity columns that back the local bindings (PostgreSQL FK cascades; SQL Server `DbTriggerKind.IdentityPropagationFallback` triggers for eligible edges), including presence-gated aliases that preserve “absent ⇒ `NULL` at the binding columns”, which trigger normal stamping of stored `_etag/_lastModifiedDate/ChangeVersion`.

## Data model summary

### `dms.*` core tables

`reference/design/backend-redesign/design-docs/data-model.md` defines the baseline core tables, with update tracking extended by `reference/design/backend-redesign/design-docs/update-tracking.md`:

- `dms.ResourceKey`
  - Lookup table mapping `(ProjectName, ResourceName)` to `ResourceKeyId` (small surrogate id).
  - Seeded deterministically by the DDL generation utility for a given `EffectiveSchemaHash` and validated/cached by DMS.

- `dms.Document`
  - One row per persisted resource instance.
  - Holds `DocumentId`, `DocumentUuid`, `ResourceKeyId` (resource type), and update-tracking token columns (see below).
  - Stores ownership-based authorization stamping (`CreatedByOwnershipTokenId`; see `auth.md`).
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
  - `dms.EffectiveSchema` is a singleton current-state row (includes smallint-bounded `ResourceKeyCount` and `ResourceKeySeedHash` for fast `dms.ResourceKey` validation; `ResourceKeySeedHash` is raw SHA-256 bytes, 32 bytes). The count uses the same 32,767-entry ceiling as `dms.ResourceKeyId`, so schema derivation/provisioning fails fast if the effective schema would exceed that supported resource inventory size; `dms.SchemaComponent` rows are keyed by `EffectiveSchemaHash`.
  - On first use of a given database connection string, DMS reads the database fingerprint, caches it, and selects the matching mapping set (rejecting requests for that database if mismatched/unsupported).

### Update tracking additions (unified design)

`reference/design/backend-redesign/design-docs/update-tracking.md` adds representation-sensitive metadata using write-time stamping, with indirect impacts realized via database-driven propagation updates (PostgreSQL FK cascades; SQL Server propagation-fallback triggers for eligible edges) to canonical stored identity columns that back local reference-identity bindings:

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
    - identity elements sourced from reference objects use the corresponding `..._DocumentId` FK columns (stable), with referenced identity values bound at `{RefBaseName}_{IdentityPart}` columns for query/reconstitution (under key unification these may be presence-gated aliases of canonical stored columns; see `key-unification.md`).
  - Reference FK columns:
    - for each document reference site: store `..._DocumentId` and the identity-part bindings, with a composite FK to the target identity key `(DocumentId, <IdentityParts...>)`. PostgreSQL uses `ON UPDATE CASCADE` for abstract targets and concrete targets with `allowIdentityUpdates=true` (`ON UPDATE NO ACTION` otherwise). SQL Server always uses `ON UPDATE NO ACTION` for reference composite FKs; eligible propagation targets are maintained by `DbTriggerKind.IdentityPropagationFallback` trigger fan-out on the referenced table. Under key unification, composite FKs are built over canonical stored identity columns (single source of truth), while per-site/per-path identity-part bindings can remain as generated/persisted aliases.
    - polymorphic targets: composite FK to `{schema}.{AbstractResource}Identity(DocumentId, <AbstractIdentityParts...>)` with the same dialect-specific update behavior (`ON UPDATE CASCADE` on PostgreSQL; `ON UPDATE NO ACTION` + propagation-fallback triggers on SQL Server),
    - descriptors: FK to `dms.Descriptor(DocumentId)` via `..._DescriptorId`.

- Collection tables `{schema}.{Resource}_{CollectionPath}`:
  - Stable internal row identity for every persisted collection item:
    - PK is `CollectionItemId`, allocated from global `dms.CollectionItemIdSequence`.
  - Parent scope columns preserve containment and enable batching:
    - every collection table stores the root `..._DocumentId` for keyset hydration,
    - nested collections additionally store `ParentCollectionItemId`,
    - nested collections keep the denormalized root scope consistent via a composite FK from `(ParentCollectionItemId, ..._DocumentId)` to the parent collection row.
  - `Ordinal` preserves array order for reconstitution and is constrained uniquely within the parent scope.
  - `arrayUniquenessConstraints` are required schema metadata and compile into semantic match keys / unique constraints for collection merges.
  - Every persisted multi-item collection scope MUST compile a non-empty semantic identity; models that do not are outside the supported design and MUST fail validation/compilation rather than falling back at runtime.

- Abstract identity artifacts:
  - `{schema}.{AbstractResource}Identity` tables provide FK targets for polymorphic references with cascade support.
  - `{schema}.{AbstractResource}_View` union views remain useful for query/diagnostics but are no longer required to project reference identity values in responses.

### Extensions (`_ext`) mapping

`reference/design/backend-redesign/design-docs/extensions.md` defines `_ext` mapping rules:

- Extension tables live in the extension project schema (e.g., `sample`, `tpdm`), not in the core schema.
- Resource-level `_ext.{project}` becomes `{projectSchema}.{Resource}Extension` keyed by `DocumentId` (1:1) with FK to the base resource root.
- `_ext` inside common types/collections becomes scope-aligned extension tables keyed to the stable identity of the base scope they extend (`DocumentId` for root scope, `CollectionItemId` for collection/common-type scopes).
- Arrays inside `_ext` become extension child tables using the same `CollectionItemId` + parent-scope strategy as core collections.
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
  - FK/descriptor bindings (including reference-identity bindings, canonical storage columns, and cascade semantics),
  - query compilation mappings (including reference-identity fields mapped to local columns),
  - update-stamping trigger plans (resource-table changes → `dms.Document` stamps/journals).

## Write path (POST upsert / PUT by id)

Combined view from `transactions-and-concurrency.md`, `flattening-reconstitution.md`, and `update-tracking.md`:

1. **Core validation and extraction**
   - Core canonicalizes JSON and produces `DocumentInfo`:
     - resource `ReferentialId`,
     - descriptor references (already include concrete JSON paths),
     - document references with `ReferentialId`s.
   - Required baseline Core extraction-model change for this redesign: add concrete indexed JSON locations to document reference instances (`DocumentReference.Path`) so nested-collection reference FKs can be populated without per-row JSONPath evaluation/hashing.
   - Profile-constrained collection writes additionally require request-scoped profile write shaping:
     - Core supplies `ProfileAppliedWriteRequest.WritableRequestBody`, after writable-profile filtering and canonicalization,
     - backend then loads the current stored document and invokes a Core-owned projector to derive `ProfileAppliedWriteContext.VisibleStoredBody`, and
     - Core MUST reject any writable profile definition that excludes a field required to compute the compiled semantic identity of a persisted multi-item collection scope.

2. **Bulk reference and descriptor resolution**
   - Resolve all referential ids in bulk via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
   - For descriptor references, validate “is a descriptor” via `dms.Descriptor` (and optionally enforce expected discriminator/type in application code).

3. **DB-enforced identity propagation**
   - Composite foreign keys keep canonical stored identity columns consistent when referenced identities change (PostgreSQL `ON UPDATE CASCADE` for abstract targets and concrete targets with `allowIdentityUpdates=true`; SQL Server `ON UPDATE NO ACTION` for all reference composite FKs plus `DbTriggerKind.IdentityPropagationFallback` triggers for eligible edges). Per-site/per-path identity bindings may be generated/persisted (and presence-gated) aliases of those canonical columns under key unification.
   - Identity-changing writes may optionally be serialized (advisory/application lock) as an operational guardrail, but correctness does not depend on an application-managed lock table.

4. **Flatten and write relational rows (single transaction)**
   - Insert/update `dms.Document`.
   - Write root table row (insert/update).
   - Write collection and extension tables using merge semantics:
     - for profile-constrained writes, the write pipeline derives visible persisted collection rows from the profile-projected stored document; backend does not evaluate profile filters itself,
     - match by the compiled semantic identity,
     - allocate new `CollectionItemId`s from `dms.CollectionItemIdSequence` for unmatched rows,
     - preserve hidden profile rows/columns and stable `CollectionItemId`s for matched rows,
     - batch inserts for newly created collection rows and recompute `Ordinal` with the same deterministic post-merge ordering rule used for no-op detection.
   - Write extension tables similarly (root extension rows only when extension values exist; scope-aligned rows for nested extension sites).
   - For each document reference site, write the stable `..._DocumentId` FK column (resolved from `dms.ReferentialIdentity`) and the referenced identity-part values to the table’s canonical stored columns (the per-site binding columns used for query/reconstitution may be generated/persisted aliases under key unification). Composite FKs enforce consistency.

5. **Strict identity maintenance (row-local triggers)**
   - Per-resource triggers recompute `dms.ReferentialIdentity` when a document’s identity projection columns change (directly or via propagated updates to identity-component reference identity columns).
   - Identity changes therefore propagate transitively via DB-driven propagation (PostgreSQL FK cascades; SQL Server propagation-fallback triggers), without application-managed closure traversal.

6. **Update tracking (stored metadata + journal)**
   - Any representation-affecting change (including cascaded updates to canonical stored identity columns backing local bindings) bumps `dms.Document.ContentVersion/ContentLastModifiedAt`.
   - Identity projection changes additionally bump `dms.Document.IdentityVersion/IdentityLastModifiedAt`.
   - Emit `dms.DocumentChangeEvent` rows via triggers when `ContentVersion` changes.

## Read path (GET by id / query)

Combined view from `transactions-and-concurrency.md`, `flattening-reconstitution.md`, and `update-tracking.md`:

- **GET by id**
  1. Resolve `DocumentUuid → DocumentId`.
  2. Authorize the request against stored values (namespace/ownership/relationship/custom-view strategies as configured) using token-derived authorization context; see `auth.md`.
  3. Hydrate relational tables and reconstitute JSON; serve `_etag/_lastModifiedDate/ChangeVersion` from `dms.Document` and reference identity fields from local reference-identity binding columns (which may be presence-gated aliases under key unification).

- **Query**
  - Query compilation is constrained to root-table paths (`queryFieldMapping` does not cross array boundaries).
  - Page selection is done over the resource root table, ordered by `DocumentId` (ascending), and MUST apply authorization filters at the SQL layer so only authorized `DocumentId`s enter the page keyset; see `auth.md`.
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
    - core `auth.*` authorization companion objects and required supporting indexes (see `auth.md`),
    - per-project schemas and per-resource tables,
    - extension schemas/tables,
    - abstract identity tables (and optional union views),
    - update tracking sequences and triggers,
  - records the singleton `dms.EffectiveSchema` row (including smallint-bounded `ResourceKeyCount` and `ResourceKeySeedHash`) and `dms.SchemaComponent` rows keyed by `EffectiveSchemaHash`.
  - provision semantics: create-only (no migrations), optional database creation as a pre-step, and a single transaction for schema + seeds.
- (Optional) ahead-of-time mapping pack compilation and file distribution keyed by `EffectiveSchemaHash` to avoid runtime plan compilation under load (see `reference/design/backend-redesign/design-docs/aot-compilation.md`).
- DMS runtime remains validate-only and fails fast on schema mismatch per database (no in-process migration/hot reload).

## Key risks and mitigations (from the docs)

- **Cascade feasibility and fan-out**
  - SQL Server “multiple cascade paths” / cycle restrictions are the reason SQL Server reference composite FKs are emitted as `ON UPDATE NO ACTION` and eligible propagation is handled via `DbTriggerKind.IdentityPropagationFallback` triggers.
  - Identity updates on “hub” documents can synchronously update many dependent rows; needs guardrails, telemetry, and a deadlock retry policy.

- **Trigger correctness and multi-row stamping**
  - Stamping must produce per-row unique `ChangeVersion` values (especially for SQL Server multi-row propagation-trigger updates) and must cover changes across root + child + extension tables.

- **Read amplification**
  - Reconstitution can be expensive for deep resources (many child tables/result sets); benchmark representative deep resources early and treat read-path performance as a first-class requirement.

- **Very large scale tables**
  - `dms.Document` scale: avoid wide repeated strings in hot tables (use `ResourceKeyId` for `(ProjectName, ResourceName)` and store `ResourceVersion` on `dms.ResourceKey`, denormalizing only where needed for CDC/streaming).
  - `dms.ReferentialIdentity` and `dms.DocumentChangeEvent` require careful indexing and may be large at scale; validate index and clustering choices per engine.
  
