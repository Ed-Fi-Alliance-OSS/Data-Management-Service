# Backend Redesign Summary: Relational Primary Store (Tables per Resource)

Status: Draft (summary of the draft design docs in this directory).

This redesign replaces the current three-table JSON document store (`Document`/`Alias`/`Reference`) with a relational primary store using tables per resource, while keeping DMS behavior metadata-driven via `ApiSchema.json`.

Source documents:
- Overview: `reference/design/backend-redesign/overview.md`
- Data model: `reference/design/backend-redesign/data-model.md`
- Flattening & reconstitution: `reference/design/backend-redesign/flattening-reconstitution.md`
- AOT compilation (optional mapping pack distribution): `reference/design/backend-redesign/aot-compilation.md`
- Mapping pack file format (normative `.mpack` schema): `reference/design/backend-redesign/mpack-format-v1.md`
- Extensions (`_ext`): `reference/design/backend-redesign/extensions.md`
- Transactions & concurrency: `reference/design/backend-redesign/transactions-and-concurrency.md`
- Update tracking (`_etag/_lastModifiedDate`, ChangeVersion): `reference/design/backend-redesign/update-tracking.md`
- DDL generation: `reference/design/backend-redesign/ddl-generation.md`
- DDL generator verification harness: `reference/design/backend-redesign/ddl-generator-testing.md`
- Strengths/risks: `reference/design/backend-redesign/strengths-risks.md`

> Note on update tracking: `update-tracking.md` consolidates and supersedes earlier “stored `Etag/LastModifiedAt` + fan-out bump” drafts referenced in other docs. Where there’s conflict, treat `update-tracking.md` as the normative design for `_etag/_lastModifiedDate` and Change Queries.

## Goals and explicit decisions (high level)

- Canonical storage is relational (root table per resource, child tables per collection) and is the source of truth.
- DMS remains schema/behavior-driven by `ApiSchema.json` (no handwritten per-resource code; no checked-in per-resource SQL artifacts).
- Relationships are stored as stable `DocumentId` foreign keys (no relational rewrite cascades on natural-key changes).
- Keep `ReferentialId` (UUIDv5 of `(ProjectName, ResourceName, DocumentIdentity)`) as the uniform natural-identity key for resolution and upserts.
- SQL Server + PostgreSQL parity is required.
- Authorization is intentionally out of scope for this redesign phase.
- DMS does not hot-reload or auto-migrate schemas in-process; it validates schema compatibility per database on first use of that database connection string (cached) via an effective schema fingerprint and fails fast if no matching mapping is available.

## Core concepts and terms

- `DocumentUuid`: stable external identifier for API `id` (does not change on identity updates).
- `DocumentId`: internal surrogate key (`bigint`) used for FKs and clustering.
- `ReferentialId`: deterministic UUIDv5 used as the canonical “natural identity key”; stored in `dms.ReferentialIdentity`.
- **Identity component**: a document reference whose projected identity participates in a document’s identity (`identityJsonPaths`); used for strict identity-cascade correctness (`dms.ReferenceEdge.IsIdentityComponent=true`). Descriptor values can participate in identity, but descriptors are treated as immutable and do not participate in `dms.ReferenceEdge` closure/cascades.
- **Representation dependency** (1 hop): any referenced non-descriptor document whose identity values are embedded in the returned JSON representation; used for derived `_etag/_lastModifiedDate` and Change Queries (not filtered to identity components). Descriptor URIs are projected into the representation, but descriptors are treated as immutable and excluded from dependency tracking.

## Data model summary

### `dms.*` core tables

`reference/design/backend-redesign/data-model.md` defines the baseline core tables, with update tracking extended by `reference/design/backend-redesign/update-tracking.md`:

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
    - reference-bearing identities (kept correct via strict recompute),
    - descriptor identities (resource type + normalized URI),
    - polymorphic/abstract reference support via superclass/abstract alias rows (documents have ≤ 2 referential ids: primary + optional superclass alias).
  - Physical guidance: do not cluster on random UUID in SQL Server; cluster on a sequential key like `(DocumentId, ResourceKeyId)`.

- `dms.Descriptor` (unified)
  - Unified descriptor table keyed by the descriptor document’s `DocumentId` so descriptor references can FK to `dms.Descriptor(DocumentId)` without per-descriptor tables.
  - Used for “is a descriptor” enforcement and (optionally) type diagnostics/validation.

- `dms.ReferenceEdge`
  - Reverse index of “parent references child”, stored as one row per `(ParentDocumentId, ChildDocumentId)` (collapsed granularity; not per-path).
  - `IsIdentityComponent` is the OR of all reference sites from the parent to that child that are identity components.
  - Correctness-critical: must cover all outgoing non-descriptor resource references, including those stored in child tables / nested collections (descriptor rows are treated as immutable and are excluded from this table).
  - Used for:
    - strict identity closure expansion (`IsIdentityComponent=true`) for `dms.ReferentialIdentity` recompute,
    - outbound dependency enumeration for derived `_etag/_lastModifiedDate` and `If-Match` checks,
    - Change Query indirect expansion,
    - delete diagnostics.

- `dms.IdentityLock`
  - One row per document (`DocumentId` PK/FK) to provide a stable lock target.
  - Used to enforce phantom-safe identity closure recompute and “stale-at-birth” avoidance for reference-bearing identities (see locking invariants below).

- `dms.EffectiveSchema` + `dms.SchemaComponent`
  - Records `EffectiveSchemaHash` (SHA-256 fingerprint) of the effective core+extension `ApiSchema.json` set as it affects relational mapping.
  - `dms.EffectiveSchema` is a singleton current-state row (includes `ResourceKeyCount`/`ResourceKeySeedHash` for fast `dms.ResourceKey` validation); `dms.SchemaComponent` rows are keyed by `EffectiveSchemaHash`.
  - On first use of a given database connection string, DMS reads the database fingerprint, caches it, and selects the matching mapping set (rejecting requests for that database if mismatched/unsupported).

### Update tracking additions (unified design)

`reference/design/backend-redesign/update-tracking.md` adds representation-sensitive metadata without write-time fan-out:

- Global sequence: `dms.ChangeVersionSequence` (`bigint`).
- `dms.Document` token columns:
  - `ContentVersion`, `IdentityVersion` (global monotonic stamps).
  - `ContentLastModifiedAt`, `IdentityLastModifiedAt`.
- Journals (append-only):
  - `dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId, CreatedAt)`
  - `dms.IdentityChangeEvent(ChangeVersion, DocumentId, CreatedAt)`
- Derived at read time:
  - `_etag = Base64(SHA-256(EncodeV1(ContentVersion, IdentityVersion, sorted(deps: (DocumentId, IdentityVersion)))))`
  - `_lastModifiedDate = max(ContentLastModifiedAt, IdentityLastModifiedAt, max(dep.IdentityLastModifiedAt))`
  - per-item `ChangeVersion = max(ContentVersion, IdentityVersion, max(dep.IdentityVersion))`

### Per-project schemas and resource tables

For each project, create a physical schema derived from `ProjectEndpointName` (e.g., `ed-fi` → `edfi`), with:

- Root table `{schema}.{ResourceName}`:
  - PK `DocumentId` (FK to `dms.Document(DocumentId)` ON DELETE CASCADE).
  - Unique constraint for the resource’s natural key derived from `identityJsonPaths`:
    - scalar identity elements become scalar columns,
    - identity elements sourced from reference objects use the corresponding `..._DocumentId` FK columns (avoid denormalizing referenced natural keys).
  - Reference FK columns:
    - concrete targets: FK to `{schema}.{TargetResource}(DocumentId)` (existence + type),
    - polymorphic targets: FK to `dms.Document(DocumentId)` (existence) plus membership validation via `{AbstractResource}_View`,
    - descriptors: FK to `dms.Descriptor(DocumentId)` via `..._DescriptorId`.

- Collection tables `{schema}.{Resource}_{CollectionPath}`:
  - Composite parent+ordinal keys to preserve order and enable batching:
    - PK is `(DocumentId, <ParentOrdinals...>, Ordinal)`.
  - Nested collections add ancestor ordinals into the key and FK (no generated IDs; avoids `RETURNING`/`OUTPUT` capture).
  - Optional unique constraints from `arrayUniquenessConstraints`.

- Abstract identity union views `{schema}.{AbstractResource}_View`:
  - Derived from `abstractResources` + subclass metadata.
  - Provide a single join target for read-time identity projection and for polymorphic membership validation.

### Extensions (`_ext`) mapping

`reference/design/backend-redesign/extensions.md` defines `_ext` mapping rules:

- Extension tables live in the extension project schema (e.g., `sample`, `tpdm`), not in the core schema.
- Resource-level `_ext.{project}` becomes `{projectSchema}.{Resource}Extension` keyed by `DocumentId` (1:1) with FK to the base resource root.
- `_ext` inside common types/collections becomes scope-aligned extension tables keyed exactly like the base scope they extend (DocumentId + ordinals), with FK back to the base scope table.
- Arrays inside `_ext` become extension child tables using the same parent+ordinal key strategy.
- References/descriptors inside extensions follow the same FK rules as core mapping (`..._DocumentId` and `..._DescriptorId`).

## Derived mapping and plan compilation (no codegen)

`reference/design/backend-redesign/flattening-reconstitution.md` describes how DMS derives a full relational mapping from `ApiSchema.json` at startup and compiles it into read/write plans:

- Inputs: `jsonSchemaForInsert`, `documentPathsMapping`, `identityJsonPaths`, `arrayUniquenessConstraints`, `abstractResources`, and optional `resourceSchema.relational` naming overrides.
- Optional `resourceSchema.relational` block provides deterministic name overrides without enumerating full flattening metadata:
  - `rootTableNameOverride`
  - `nameOverrides` keyed by restricted JSONPaths (`$.x.y` for column base names, `$.arr[*]` for collection base names).
- Derived model includes:
  - table/column lists and types,
  - FK/descriptor bindings (with `IsIdentityComponent` classification),
  - identity projection plans for reference reconstitution,
  - query compilation mappings (root-table-only query fields).

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

3. **Locking for identity correctness**
   - Use `dms.IdentityLock` row locks to prevent stale derived identities and to make identity-closure expansion phantom-safe.
   - Invariants (from `transactions-and-concurrency.md`):
     - **Child locked before parent edge**: before inserting/updating any `IsIdentityComponent=true` edge into child `C`, writer must hold a shared lock on `dms.IdentityLock(C)`.
     - **Lock ordering**: acquire shared locks on all identity-component children first (ascending `DocumentId`), then acquire update lock(s) on the parent document(s) (ascending).
     - **Atomic closure recompute**: identity/URI-changing transactions must lock the full closure to a fixpoint and recompute identities transactionally (no stale window).

4. **Flatten and write relational rows (single transaction)**
   - Insert/update `dms.Document` (and create `dms.IdentityLock` row on insert).
   - Write root table row (insert/update).
   - Write child tables using a baseline replace strategy (delete by parent key, bulk insert rows), with batching to respect SQL Server parameter limits.
   - Write extension tables similarly (root extension rows only when extension values exist; scope-aligned rows for nested extension sites).
   - Maintain `dms.ReferenceEdge` for the document’s outgoing non-descriptor references (diff-based upsert recommended; fail the write if edge maintenance fails).

5. **Strict identity maintenance**
   - If the write changes identity/URI projection:
     - run identity-closure expansion via `dms.ReferenceEdge(IsIdentityComponent=true)` to a fixpoint while update-locking each impacted `DocumentId`,
     - recompute and replace `dms.ReferentialIdentity` for all impacted documents set-based (including superclass alias rows),
     - bump `IdentityVersion/IdentityLastModifiedAt` only when identity projection values actually change.

6. **Update tracking (tokens + journals)**
   - Detect content vs identity-projection changes; allocate one stamp from `dms.ChangeVersionSequence` and apply to the appropriate token columns.
   - Emit `dms.DocumentChangeEvent` and `dms.IdentityChangeEvent` rows via database triggers on `dms.Document` token changes.

## Read path (GET by id / query)

Combined view from `transactions-and-concurrency.md`, `flattening-reconstitution.md`, and `update-tracking.md`:

- **GET by id**
  1. Resolve `DocumentUuid → DocumentId`.
  2. Hydrate relational tables and reconstitute JSON; batch-load dependency tokens to compute derived `_etag/_lastModifiedDate/ChangeVersion`.

- **Query**
  - Query compilation is constrained to root-table paths (`queryFieldMapping` does not cross array boundaries).
  - Page selection is done over the resource root table, ordered by `DocumentId` (ascending).
  - Reconstitution is page-based (not “GET by id N times”):
    - materialize a page keyset of `DocumentId`s,
    - hydrate root + child + extension tables by joining each table to the page keyset in one command (multiple result sets),
    - batch identity projection (reference objects) per target resource type (abstract targets via `{AbstractResource}_View`),
    - batch descriptor URI lookups,
    - compute derived `_etag/_lastModifiedDate/ChangeVersion` by batch-loading dependency tokens per page.

## Schema management and DDL generation

`reference/design/backend-redesign/ddl-generation.md` + `data-model.md` define:

- A separate DDL generation utility that:
  - loads the configured core+extension `ApiSchema.json` files,
  - computes `EffectiveSchemaHash`,
  - derives the same relational model as runtime,
  - emits (and optionally provisions) deterministic DDL for PostgreSQL and SQL Server for:
    - core `dms.*` tables,
    - per-project schemas and per-resource tables,
    - extension schemas/tables,
    - abstract union views,
    - update tracking sequences and triggers,
  - records the singleton `dms.EffectiveSchema` row (including `ResourceKeyCount`/`ResourceKeySeedHash`) and `dms.SchemaComponent` rows keyed by `EffectiveSchemaHash`.
  - provision semantics: create-only (no migrations), optional database creation as a pre-step, and a single transaction for schema + seeds.
- (Optional) ahead-of-time mapping pack compilation and file distribution keyed by `EffectiveSchemaHash` to avoid runtime plan compilation under load (see `reference/design/backend-redesign/aot-compilation.md`).
- DMS runtime remains validate-only and fails fast on schema mismatch per database (no in-process migration/hot reload).

## Key risks and mitigations (from the docs)

- **`dms.ReferenceEdge` integrity is correctness-critical**
  - Missing/extra edges can cause silent incorrectness (stale identity resolution, incorrect derived metadata, incomplete Change Queries, incomplete delete diagnostics).
  - Suggested mitigations: by-construction edge extraction from write plans, optional sampling verification on writes, background audit/repair tooling, strong telemetry.

- **IdentityLock orchestration complexity**
  - Requires strict lock ordering and cycle detection in the identity dependency graph (reject identity cycles at schema validation).
  - Needs deadlock retry policy and benchmarking for “hub” contention scenarios.

- **Read amplification**
  - Reconstitution can be expensive for deep resources (many child tables/result sets); benchmark representative deep resources early and treat read-path performance as a first-class requirement.

- **Very large scale tables**
  - `dms.Document` scale: avoid wide repeated strings in hot tables (use `ResourceKeyId` for `(ProjectName, ResourceName)` and store `ResourceVersion` on `dms.ResourceKey`, denormalizing only where needed for CDC/streaming).
  - `dms.ReferenceEdge` at ~1B rows drives partitioning/maintenance concerns; consider partitioning, filtered/partial structures for identity edges, and re-evaluating per-row `CreatedAt` if unused.
