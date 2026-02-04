# Backend Redesign: Transactions, Concurrency, and Cascades

## Status

Draft.

This document is the transactions/concurrency deep dive for `overview.md`, focusing on:
- reference validation,
- write-time identity propagation and derived maintenance (`dms.ReferentialIdentity`, abstract identity tables),
- update-tracking stamping (`dms.Document`), and
- operational caching patterns.

- Overview: [overview.md](overview.md)
- Update tracking: [update-tracking.md](update-tracking.md)
- Data model: [data-model.md](data-model.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)
- AOT compilation (optional mapping pack distribution): [aot-compilation.md](aot-compilation.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Table of Contents

- [Reference Validation](#reference-validation)
- [How `ApiSchema.json` Drives This Design](#how-apischemajson-drives-this-design)
- [Write Path (POST Upsert / PUT by id)](#write-path-post-upsert--put-by-id)
- [Read Path (GET by id / GET query)](#read-path-get-by-id--get-query)
- [Caching (Low-Complexity Options)](#caching-low-complexity-options)
- [Optional Database Projection: `dms.DocumentCache`](#optional-database-projection-dmsdocumentcache)
- [Delete Path (DELETE by id)](#delete-path-delete-by-id)
- [Schema Validation (EffectiveSchema)](#schema-validation-effectiveschema)
- [Operational Considerations](#operational-considerations)

---

## Reference Validation

Reference validation is provided by **two layers**:

### 1) Write-time validation (application-level)

During POST/PUT processing, the backend:
- Resolves each extracted reference (`DocumentReference` / `DescriptorReference`) to a target `DocumentId` using an ApiSchema-derived “natural-key resolver”.
- Fails the request if any referenced identity does not exist (same semantics as today: descriptor failures vs resource reference failures).

This is required because relational tables store **stable `DocumentId` foreign keys**, and we cannot write those without resolving them.

#### ApiSchema-derived natural-key resolver

The **natural-key resolver** is the backend service that converts a `(Project, Resource, DocumentIdentity)` triple into a persisted `DocumentId` **without per-resource code**.

This redesign keeps `ReferentialId` as the canonical “natural identity key” that the resolver uses for `ReferentialId → DocumentId` lookups; see [overview.md#Why keep ReferentialId](overview.md#why-keep-referentialid).

It is used for:
- resolving extracted `DocumentReference` / `DescriptorReference` instances during writes,
- POST upsert existence detection (always via `dms.ReferentialIdentity`), and
- query-time resolution when a filter targets a referenced identity but does not have a direct column mapping.

**Inputs**
- `QualifiedResourceName` of the target (from `DocumentReference.ResourceInfo` / `DescriptorReference.ResourceInfo`)
- `DocumentIdentity` (ordered `IdentityJsonPath → string value` pairs)
- the request-local `ReferentialId` (already computed by Core for writes; optional for query-time and computable deterministically using the same UUIDv5 algorithm as Core)
- optional “location” info for error reporting (e.g., concrete JSON path instance from Core extraction)

**Outputs**
- `DocumentId` when found
- “not found” when no matching row exists (write-time: validation failure; query-time: empty-match behavior)

##### Resolution algorithm (bulk, request-scoped)

1. Dedupe referential ids across all extracted references.
2. Resolve in bulk via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
3. For descriptor references, validate “is a descriptor” (and optional descriptor type) via `dms.Descriptor`.

##### Caching

The resolver uses layered caching:
- **Per-request memoization** (always): avoids duplicate work within one request.
- Optional L1/L2 caches (after-commit population only):
  - `ReferentialId → DocumentId`

When identity updates occur, any cross-request cache of `ReferentialId → DocumentId` must be updated/evicted for affected keys after commit (or disabled / short-TTL; see [Caching](#caching-low-complexity-options)).

### 2) Database enforcement (FKs + propagation)

Relational tables store references as foreign keys so the database enforces referential integrity **and** keeps stored reference-identity columns synchronized.

#### Document references (`..._DocumentId` + propagated identity columns)

For each document reference site, the referencing table stores:
- the stable `..._DocumentId`, and
- the referenced resource’s identity natural-key fields as local columns (`{RefBaseName}_{IdentityPart}`).

DDL generator requirements (derived from ApiSchema):
- Enforce “all-or-none” nullability for the reference group via a CHECK constraint.
  - Rationale: a composite FK does not enforce anything if *any* referencing column is `NULL`.
- Enforce a composite FK:
  - concrete target: `{schema}.{TargetResource}(DocumentId, <IdentityParts...>)` using `ON UPDATE CASCADE` only when the target has `allowIdentityUpdates=true` (otherwise `ON UPDATE NO ACTION`), or
  - abstract target: `{schema}.{AbstractResource}Identity(DocumentId, <IdentityParts...>)` using `ON UPDATE CASCADE` (identity tables are trigger-maintained).

When a referenced document’s identity changes (allowed only when `allowIdentityUpdates=true`), the database cascades updated identity values into all direct referrers’ stored columns. Those are real row updates on referrers, enabling normal stamping triggers to bump `_etag/_lastModifiedDate/ChangeVersion` without a separate reverse-lookup table.

#### Descriptor references (`..._DescriptorId`)

Descriptor references are stored as `..._DescriptorId` FKs to `dms.Descriptor` for existence enforcement and URI reconstitution. In this redesign, descriptors are treated as immutable reference data and do not participate in propagation.

#### How polymorphic (abstract) references work end-to-end

Polymorphic references are stored as a single `BIGINT` `DocumentId` FK value, but the logical target is an *abstract* resource (e.g., `EducationOrganization`) with one abstract identity shape.

The pieces fit together like this:

1. **API payload uses abstract identity fields** (not a `DocumentId`):
   - e.g., an `educationOrganizationReference` carries `educationOrganizationId`.
2. **Write-time resolution uses `dms.ReferentialIdentity`**:
   - DMS computes the target `ReferentialId` for the abstract resource type + identity values and resolves `ReferentialId → DocumentId` in bulk via `dms.ReferentialIdentity`.
   - This works because each concrete subtype maintains superclass/abstract **alias** referential-id rows, so abstract references can resolve without per-subtype SQL.
3. **Persist `..._DocumentId` plus abstract identity columns**:
   - The referencing row stores both the resolved `DocumentId` and the abstract identity column values provided in the payload.
4. **Database enforces membership + propagation via `{AbstractResource}Identity`**
   - The composite FK targets `{schema}.{AbstractResource}Identity` with `ON UPDATE CASCADE`, so:
     - the reference is guaranteed to target a valid member of the hierarchy, and
     - the stored abstract identity columns are kept correct automatically.
5. **Read-time reference identity projection is local**
   - Reconstitution emits reference objects from the stored propagated identity columns (no join required).
   - A `{schema}.{AbstractResource}_View` union view can still be emitted for diagnostics/integrations, but it is not required for API correctness.

### Delete conflicts

Deletes rely on the FK graph:
- If a document is referenced, `DELETE` fails with an FK violation; DMS maps that to a `409` conflict.
- Because FK names are deterministic, DMS can map the violated constraint back to a referencing resource/table to produce a conflict response.

Optional diagnostics:
- For “who references me?” tooling, generate deterministic inbound-reference queries from the compiled relational model (no runtime-maintained edge table).

---

## How `ApiSchema.json` Drives This Design

Deep dive on derived mapping and the minimal `ApiSchema.json` additions: [flattening-reconstitution.md](flattening-reconstitution.md) (sections 2–4).

### Existing ApiSchema inputs (already present)

- `jsonSchemaForInsert`: authoritative shape, types, formats, maxLength, required (fully dereferenced/expanded;
  no `$ref`, `oneOf`/`anyOf`/`allOf`, `enum`)
- `identityJsonPaths`: natural key extraction and uniqueness
- `documentPathsMapping`: identifies references vs scalars vs descriptor paths, plus reference identity mapping
- `decimalPropertyValidationInfos`: precision/scale for `decimal`
- `arrayUniquenessConstraints`: relational unique constraints for collection tables
- `abstractResources`: abstract identity metadata for polymorphic reference targets (drives `{AbstractResource}Identity` tables and optional union views)
- `isSubclass` + superclass metadata: drives insertion of superclass/abstract alias referential-id rows in `dms.ReferentialIdentity`
- `queryFieldMapping`: defines queryable fields and their JSON paths/types; may map to:
  - root scalar columns, or
  - propagated identity columns for reference-object identity fields (enabling no-subquery predicates)

### Optional ApiSchema additions

This redesign proposes a small, optional `resourceSchema.relational` block to support stable physical naming without enumerating full “flattening metadata”. See [flattening-reconstitution.md](flattening-reconstitution.md) (section 3) for the proposed shape and semantics.

---

## Write Path (POST Upsert / PUT by id)

Deep dive on flattening execution and write-planning: [flattening-reconstitution.md](flattening-reconstitution.md) (section 5).

### Common steps

1. Core validates JSON and extracts:
   - `DocumentIdentity` + `ReferentialId`
   - Document references (with `ReferentialId`s)
   - Descriptor references (with `ReferentialId`s, normalized URI)
2. Backend resolves references in bulk:
   - Use an ApiSchema-derived resolver to turn references into `DocumentId`s via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`), including:
     - self-contained identities
     - reference-bearing identities (kept current via cascades + per-resource triggers)
     - polymorphic/abstract identities via superclass/abstract alias rows in `dms.ReferentialIdentity`
   - Descriptor refs additionally require a `dms.Descriptor` existence/type check (for “is a descriptor” enforcement)
3. Backend writes within a single transaction:
   - Insert/update `dms.Document` (allocate `DocumentId`; persist `DocumentUuid` and `ResourceKeyId`).
   - Write resource root + child + extension tables (replace strategy for collections).
   - For each document reference site, persist both:
     - the stable `..._DocumentId`, and
     - the referenced identity natural-key columns `{RefBaseName}_{IdentityPart}` from the request body.
   - `dms.Descriptor` upsert if the resource is a descriptor.
4. Database enforces propagation and maintains derived artifacts (in-transaction):
   - Composite FKs use `ON UPDATE CASCADE` only for targets with `allowIdentityUpdates=true` (or trigger-based fallback where required) to propagate identity changes into stored reference identity columns.
   - Generated triggers maintain `dms.ReferentialIdentity` (row-local recompute on identity projection changes).
   - Generated triggers stamp `dms.Document` representation/identity versions for `_etag/_lastModifiedDate/ChangeVersion` and emit `dms.DocumentChangeEvent` (see [update-tracking.md](update-tracking.md)).

### Identity propagation and derived maintenance (DB-driven)

This redesign keeps relationships keyed by stable `..._DocumentId`, but also stores referenced identity natural-key fields alongside every document reference and enforces composite FKs with `ON UPDATE CASCADE` only when the target has `allowIdentityUpdates=true` (otherwise `ON UPDATE NO ACTION`).

Key effects:
- **Indirect representation changes are materialized as row updates**: when a referenced identity changes, the database cascades updated identity values into all direct referrers’ stored reference identity columns.
- **Transitive identity effects converge without application traversal**: cascades propagate through chains of references, and row-local triggers recompute derived referential ids where needed.

Engine considerations:
- PostgreSQL supports arbitrary cascades; SQL Server restricts “cycles or multiple cascade paths”. For targets with `allowIdentityUpdates=true`, the DDL generator must use `ON UPDATE CASCADE` where allowed and otherwise emit trigger-based propagation for the restricted edges (still deterministic and set-based), without introducing `dms.ReferenceEdge`.

### Insert vs update detection

- **Upsert (POST)**: detect an existing row by resolving the request’s `ReferentialId` via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
  - The resource root table’s natural-key unique constraint remains a recommended relational guardrail and is still useful for race detection (unique violation → 409) if two writers attempt to create the same natural key concurrently.
- **Update by id (PUT)**: detect by `DocumentUuid`:
  - Find `DocumentId` from `dms.Document` by `DocumentUuid`.

### Identity updates (`AllowIdentityUpdates`)

If identity changes on update:
- Treat `dms.ReferentialIdentity` as a derived index and recompute it **transactionally** (via triggers) for the changed document and any documents whose identity projection changes due to cascaded identity-component updates.
- Relationships stored as `DocumentId` FKs remain valid; no rewrite of `..._DocumentId` columns is required.

Operational guidance:
- Identity updates can fan out broadly (cascaded updates + trigger work). Keep them rare; consider operational guardrails (rate limiting, maintenance window guidance, deadlock retry).

### Cascade scenarios (tables-per-resource)

Tables-per-resource storage removes the need for **relational** cascade rewrites when upstream natural keys change, because relationships are stored as stable `DocumentId` FKs. Cascades still exist for **propagated identity columns** and for **derived artifacts** (referential ids and stamps), and are handled in the database:

- **Identity/URI change on a document itself** (e.g., `StudentUniqueId` update)
  - Cascades update stored identity columns in all direct referrers (identity-component and non-identity references).
  - Referrers’ `dms.Document.ContentVersion` stamps update because their served representation changes (the embedded reference identity changed).
  - For identity-component referrers, triggers also update `dms.Document.IdentityVersion` and `dms.ReferentialIdentity` for the referrer (and this may cascade further).

- **Outgoing reference changes on a document** (`..._DocumentId` value changes)
  - Relational writes update the FK columns and the corresponding propagated identity columns from the request body.
  - Composite FK enforcement guarantees the new `{RefBase}_{IdentityPart}` values match the referenced target.

- **Representation update tracking (`_etag/_lastModifiedDate`, `ChangeVersion`)**
  - Representation metadata is served from stored stamps on `dms.Document`.
  - Because identity propagation is materialized as row updates, the same per-table stamping triggers cover indirect changes (no read-time dependency derivation).

### Concurrency (optimistic `If-Match`)

With stored representation stamps:
- GET returns `_etag` derived from `dms.Document.ContentVersion` (or an equivalent stored representation-stamp column).
- PUT/DELETE `If-Match` validation is row-local:
  - compare the request `_etag` to the current stored stamp for that `DocumentId`;
  - if mismatched, return `412 Precondition Failed`.

Because FK cascades update referrers’ rows and triggers bump their representation stamps, indirect changes correctly cause `If-Match` failures on subsequently stale clients.

### Deadlock + retry policy

Deadlocks are possible under contention, especially for identity updates with large cascades. The correct response is to roll back and retry the **entire** write transaction.

Recommended: bounded retry (e.g., 3 attempts) with jittered backoff. Treat these as retryable:
- PostgreSQL: `40P01` (deadlock detected)
- SQL Server: `1205` (deadlock victim), and optionally `1222` (lock request timeout, if configured)

### SQL Server isolation defaults (recommended)

To reduce reader/writer blocking and deadlocks under concurrent write load, strongly recommend enabling MVCC reads:

- `READ_COMMITTED_SNAPSHOT ON` (recommended)
- optionally `ALLOW_SNAPSHOT_ISOLATION ON` (if a snapshot isolation level is ever used explicitly)

---

## Read Path (GET by id / GET query)

Deep dive on reconstitution execution and read-planning: [flattening-reconstitution.md](flattening-reconstitution.md) (section 6).

### GET by id

1. Resolve `DocumentUuid` → `DocumentId` via `dms.Document`.
2. Reconstitute JSON from relational tables and return it.

The returned JSON representation must preserve:
- Array order (via `Ordinal`)
- Required vs optional properties
- The API surface properties (`id`, `_etag`, `_lastModifiedDate`)

### Query

Filter directly on **resource root table columns** and do not require subqueries for reference-identity fields that have local propagated columns.

Contract/clarification:
- `queryFieldMapping` is constrained in ApiSchema to **root-table** paths (no JSON paths that cross an array boundary like `[*]`). This constraint is enforced by **MetaEd**, so query compilation does not need child-table `EXISTS (...)` / join predicate support.
- Backend model compilation should still fail fast if any `queryFieldMapping` path cannot be mapped to a root-table column.

Ordering/paging contract:
- Collection GET results are ordered by the **resource root table’s** `DocumentId` (ascending).
- Pagination applies to that ordering (`offset` skips N rows in `DocumentId` order; `limit` bounds the page size).

Query compilation patterns:
- **Scalar query fields**: `queryFieldMapping` JSON path → derived root-table column → `r.Column = @value`
- **Descriptor query fields**: normalize URI, compute descriptor `ReferentialId` → resolve `DescriptorId` via `dms.ReferentialIdentity` → `r.DescriptorIdColumn = @descriptorId`
- **Document reference identity query fields**: compile to predicates on local propagated identity columns, e.g.:
  - `r.Student_StudentUniqueId = @StudentUniqueId`
  - `r.School_SchoolId = @SchoolId`

Indexing:
- Ensure a supporting index exists for every foreign key (including composite parent/child FKs and composite “propagated identity” FKs). See [ddl-generation.md](ddl-generation.md) (“FK index policy”).

---

## Caching (Low-Complexity Options)

### Recommended cache targets

1. **Derived relational mapping (from `ApiSchema`)**
   - Cache the derived mapping per `(EffectiveSchemaHash, ProjectName, ResourceName)`.
   - Invalidation: effective schema change + restart (natural).

2. **`dms.ReferentialIdentity` lookups**
   - Cache `ReferentialId → DocumentId` for identity/reference resolution (all identities, including reference-bearing and abstract/superclass aliases).
   - Invalidation:
     - on insert: add cache entry after commit
     - on delete: remove relevant entries (or rely on short TTL)
     - on identity/URI change: identity updates can cascade; if you cannot enumerate impacted referential ids reliably, prefer short TTL or disable this cache for correctness.

3. **Descriptor expansion lookups** (optional)
   - Cache `DescriptorId → Uri` (and optionally `Discriminator`) to reconstitute descriptor strings without repeated joins.

4. **`DocumentUuid → DocumentId`**
   - Cache GET/PUT/DELETE resolution.
   - Invalidation: add on insert, remove on delete (or rely on TTL).

### Cache keying strategy

- Always include `(DmsInstanceId)` and `EffectiveSchemaHash` in cache keys.
- For Redis, prefix keys with `dms:{DmsInstanceId}:{EffectiveSchemaHash}:...`.

### Local-only (per-node) option

Use an in-process `MemoryCache`:
- Lowest complexity; no network hop.
- Good for: derived mapping, `ReferentialId → DocumentId`, descriptor expansion.

### Redis (distributed) option

Add Redis as an optional L2 cache:
- cache-aside reads
- write-through updates after successful commit

Invalidation approaches:
- **TTL-only** (simplest): acceptable for non-critical caches; for `ReferentialId → DocumentId` a long TTL can cause incorrect resolution after identity updates.
- **Best-effort delete on writes**: on identity updates/deletes, delete known affected Redis keys after commit.
- Optional later: pub/sub “invalidate key” messages.

---

## Optional Database Projection: `dms.DocumentCache`

`dms.DocumentCache` is an optional **materialized JSON projection** of GET/query results, intended for:
- accelerating GET/query response assembly (skip reconstitution),
- CDC streaming (e.g., Debezium → Kafka), and
- downstream indexing (e.g., OpenSearch).

Correctness must not depend on this table:
- rows may be missing/stale and rebuilt asynchronously,
- authorization must not use it as a source of truth.

### Freshness contract (recommended)

When serving from `dms.DocumentCache`, treat a row as usable only if it is **fresh**:
- compare the cached representation stamp (e.g., `dms.DocumentCache.Etag`, derived from `dms.Document.ContentVersion`) to the current `dms.Document` stamp,
- if mismatched (or missing), fall back to relational reconstitution and/or enqueue a rebuild.

### Rebuild/invalidation triggers (eventual consistency)

Because indirect representation changes are materialized as local updates (via cascades), `dms.DocumentChangeEvent` already captures:
- direct content changes, and
- indirect reference-identity changes on referrers.

So the projector does not require reverse dependency expansion. A minimal approach:
1. Consume `dms.DocumentChangeEvent` in `ChangeVersion` order.
2. Rebuild `dms.DocumentCache` for `(DocumentId, ChangeVersion)` rows not yet applied.
3. Keep `dms.DocumentCache` rows tagged with the applied representation stamp (e.g., `ContentVersion` and/or the derived `_etag`) to enforce freshness.

---

## Delete Path (DELETE by id)

1. Resolve `DocumentUuid` → `DocumentId`.
2. Attempt delete from `dms.Document` (which cascades to resource tables and identities).
3. Rely on FK constraints from referencing resource tables to prevent deleting referenced records.

Error reporting:
- SQL Server and PostgreSQL will report FK constraint violations. DMS should map the violated constraint name back to the referencing resource (deterministic FK naming) to produce a conflict response comparable to today’s `DeleteFailureReference`.

---

## Schema Validation (EffectiveSchema)

This redesign treats schema changes as an **operational concern outside DMS**. DMS does not define any in-place schema evolution behavior; instead it validates compatibility **per database** on **first use** of that database connection string:

- Schema provisioning is performed by a separate DDL generation utility that builds the same derived relational model as runtime and emits/provisions dialect-specific DDL (see [ddl-generation.md](ddl-generation.md)).
- Each provisioned database records its schema fingerprint in `dms.EffectiveSchema` + `dms.SchemaComponent`.
- `dms.EffectiveSchema` is a singleton current-state row; DMS reads `EffectiveSchemaHash` (and seed fingerprint columns) from that row.
- When a request is routed to a `DmsInstance`/connection string, DMS reads that database’s recorded fingerprint **once** (cached per connection string), and uses `EffectiveSchemaHash` to select the matching compiled mapping set.
- If no mapping set is available for that `EffectiveSchemaHash`, DMS rejects requests for that database (other databases can still be served).

This keeps schema mismatch a **fail-fast** condition while avoiding “one mis-provisioned instance prevents the server from starting” in multi-instance deployments.

---

## Operational Considerations

### Random UUID index behavior (`dms.ReferentialIdentity.ReferentialId`)

`ReferentialId` is a UUID (deterministic UUIDv5) and is effectively randomly distributed for index insertion. The primary concern is **write amplification** (page splits, fragmentation/bloat), not point-lookup speed.

**SQL Server guidance**
- Use a sequential clustered key (recommended: cluster on `(DocumentId, ResourceKeyId)`), and keep the UUID key as a **NONCLUSTERED** PK/unique index.
- Consider a lower `FILLFACTOR` on the UUID index (e.g., 80–90) to reduce page splits; monitor fragmentation and rebuild/reorganize as needed.

**PostgreSQL guidance**
- B-tree point lookups on UUID are fine; manage bloat under high write rates with:
  - index/table `fillfactor` (e.g., 80–90) if insert churn is high
  - healthy autovacuum settings and monitoring
  - periodic `REINDEX` when bloat warrants it
- If sustained ingest is extreme, consider hash partitioning `dms.ReferentialIdentity` by `ReferentialId` (e.g., 8–32 partitions) to reduce contention and make maintenance cheaper.
