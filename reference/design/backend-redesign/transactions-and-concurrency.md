# Backend Redesign: Transactions, Concurrency, and Cascades

## Status

Draft.

This document is the transactions/concurrency deep dive for `overview.md`, including transactional cascades (ETag/LastModifiedAt) and operational caching/projections.

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)
- Authorization: [auth.md](auth.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Table of Contents

- [Reference Validation](#reference-validation)
- [How `ApiSchema.json` Drives This Design](#how-apischemajson-drives-this-design)
- [Write Path (POST Upsert / PUT by id)](#write-path-post-upsert--put-by-id)
- [Read Path (GET by id / GET query)](#read-path-get-by-id--get-query)
- [Caching (Low-Complexity Options)](#caching-low-complexity-options)
- [Delete Path (DELETE by id)](#delete-path-delete-by-id)
- [Schema Validation (EffectiveSchema)](#schema-validation-effectiveschema)
- [Risks / Open Questions](#risks--open-questions)
- [Suggested Implementation Phases](#suggested-implementation-phases)
- [Operational Considerations](#operational-considerations)

---

## Reference Validation

Reference validation is provided by **two layers** (mirroring what the current `Reference` table + FK does, but in a relational way):

### 1) Write-time validation (application-level)

During POST/PUT processing, the backend:
- Resolves each extracted reference (`DocumentReference` / `DescriptorReference`) to a target `DocumentId` using an ApiSchema-derived “natural-key resolver”:
  - resolve by `dms.ReferentialIdentity` (`ReferentialId → DocumentId`) for **all** identities (self-contained, reference-bearing, and polymorphic/abstract via superclass alias rows).
- Fails the request if any referenced identity does not exist (same semantics as today: descriptor failures vs resource reference failures).

This is required because the relational tables store **`DocumentId` foreign keys**, and we cannot write those without resolving them.

#### ApiSchema-derived natural-key resolver

The **natural-key resolver** is the backend service that converts a `{Project, Resource, DocumentIdentity}` into a persisted `DocumentId` **without per-resource code**.

This design keeps `ReferentialId` as the canonical “natural identity key” that the resolver uses for `ReferentialId → DocumentId` lookups; see [overview.md#Why keep ReferentialId](overview.md#why-keep-referentialid).

It is used for:
- resolving extracted `DocumentReference` / `DescriptorReference` instances during writes
- POST upsert existence detection (always via `dms.ReferentialIdentity`)
- query-time resolution when a filter targets a referenced identity

**Inputs**
- `QualifiedResourceName` of the target (from `DocumentReference.ResourceInfo` / `DescriptorReference.ResourceInfo`)
- `DocumentIdentity` (ordered `IdentityJsonPath → string value` pairs)
- the request-local `ReferentialId` (already computed by Core for writes; optional for query-time and computable deterministically using the same UUIDv5 algorithm as Core)
- optional “location” info for error reporting (e.g., concrete JSON path instance from Core extraction)

**Outputs**
- `DocumentId` when found
- “not found” when no matching row exists (write-time: validation failure; query-time: empty-match behavior)

##### Plan compilation (per resource)

No resource-specific SQL is required for identity resolution: all lookups are via `dms.ReferentialIdentity`.

ApiSchema is still used to:
- compute `ReferentialId` deterministically (Core does this for writes; backend can do it for query-time), and
- determine polymorphic/superclass targets so `dms.ReferentialIdentity` alias rows are present and used correctly.

##### Resolution algorithm (bulk, request-scoped)

1. Dedupe referential ids across all extracted references.
2. Resolve in bulk via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
3. For descriptor references, validate “is a descriptor” (and optional descriptor type) via `dms.Descriptor`.

##### Caching

The resolver uses layered caching:
- **Per-request memoization** (always): avoids duplicate work within one request.
- Optional L1/L2 caches (after-commit population only):
  - `ReferentialId → DocumentId`

When identity updates occur, any cross-request cache of `ReferentialId → DocumentId` must be updated/evicted for all affected keys after commit.

### 2) Database enforcement (FKs)

Relational tables store references as FKs so the database enforces referential integrity:

- **Concrete non-descriptor references**: FK to the **target resource table**, e.g. `FK(StudentSchoolAssociation.School_DocumentId → edfi.School.DocumentId)`. This validates both existence and type.
- **Descriptor references**: FK directly to `dms.Descriptor`. This validates existence (“is a descriptor”); `Discriminator` supports type validation in the application (or via optional triggers).
- **Polymorphic references** (e.g., `EducationOrganization`): a single FK to a concrete table is not possible. Baseline enforcement is:
  - FK to `dms.Document(DocumentId)` (existence)
  - Application validation uses `{AbstractResource}_View` (a union view derived from `ApiSchema.json` `abstractResources`) to ensure the referenced `DocumentId` is a member of the allowed hierarchy and to obtain a discriminator for diagnostics (see [data-model.md](data-model.md))

Optional: add a trigger to enforce the same membership check in the database by validating `EXISTS (SELECT 1 FROM {AbstractResource}_View WHERE DocumentId = <fk>)` on insert/update.

### Delete conflicts

Deletes rely on the same FK graph:
- If a document is referenced, `DELETE` fails with an FK violation; DMS maps that to a `409` conflict.
- Baseline: because FK names are deterministic, DMS can map the violated constraint back to a referencing resource/table to produce a conflict response.
- `dms.ReferenceEdge` can optionally be used for diagnostics:
  - `SELECT ParentDocumentId FROM dms.ReferenceEdge WHERE ChildDocumentId = @deletedDocumentId`
  - join `dms.Document` to report referencing resource types
  - this avoids scanning all resource tables and produces more consistent diagnostics across engines.



## How `ApiSchema.json` Drives This Design

The design uses existing metadata and adds minimal new hints to avoid embedding full “flattening metadata” if we can derive it.

Deep dive on derived mapping and the minimal `ApiSchema.json` additions: [flattening-reconstitution.md](flattening-reconstitution.md) (sections 2–4).

### Existing ApiSchema inputs (already present)

- `jsonSchemaForInsert`: authoritative shape, types, formats, maxLength, required
- `identityJsonPaths`: natural key extraction and uniqueness
- `documentPathsMapping`: identifies references vs scalars vs descriptor paths, plus reference identity mapping
- `decimalPropertyValidationInfos`: precision/scale for `decimal`
- `arrayUniquenessConstraints`: relational unique constraints for collection tables
- `abstractResources`: abstract identity metadata for polymorphic reference targets (used for union-view identity projection on reads and polymorphic membership validation)
- `isSubclass` + superclass metadata: drives insertion of one superclass/abstract alias referential id (documents have ≤ 2 referential ids)
- `queryFieldMapping`: defines queryable fields and their JSON paths/types, and is constrained to paths that map to root-table columns (no array/child-table predicates). **MetaEd enforces this** (no `[*]` in `queryFieldMapping` paths).

### Optional ApiSchema additions

This redesign proposes a small, optional `resourceSchema.relational` block to support stable physical naming without enumerating full “flattening metadata”. See [flattening-reconstitution.md](flattening-reconstitution.md) (section 3) for the proposed shape and semantics.



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
     - reference-bearing identities (required; kept current via cascading recompute)
     - polymorphic/abstract identities via superclass/abstract alias rows in `dms.ReferentialIdentity`
   - Descriptor refs additionally require a `dms.Descriptor` existence/type check (for “is a descriptor” enforcement)
3. Backend acquires identity locks:
   - Acquire **shared locks** on `dms.IdentityLock` rows for any **identity-component** referenced `ChildDocumentId`s (from ApiSchema-derived bindings) *before* acquiring the parent document’s update lock. This prevents “stale-at-birth” derived identities and avoids deadlocks with concurrent identity-cascade transactions.
   - Acquire an **update lock** on this document’s `dms.IdentityLock` row (insert the row first for inserts).
   - See [Phantom-safe impacted-set locking](#phantom-safe-impacted-set-locking) for the normative lock ordering + algorithms.
4. Backend writes within a single transaction:
   - `dms.Document` insert/update (sets `Etag`, `LastModifiedAt`, etc.)
   - `dms.ReferentialIdentity` upsert (primary + superclass aliases) for all resource identities (self-contained and reference-bearing)
   - Resource root + child tables (using replace strategy for collections)
   - `dms.Descriptor` upsert if the resource is a descriptor
   - Maintain `dms.ReferenceEdge` rows for this document (outgoing references and descriptor references):
     - required to drive `dms.ReferentialIdentity` cascades and representation-version (`Etag`/`LastModifiedAt`) cascades
     - used for diagnostics (“who references me?”) and targeted async cache rebuild/invalidation
   - If `dms.DocumentCache` is enabled, treat it as an eventual-consistent **projection** (CDC/indexing):
     - enqueue/mark the written document (and any impacted dependents) for background materialization

#### Maintaining `dms.ReferenceEdge` (identity cascades, diagnostics, cache rebuild; low-churn)

`dms.ReferenceEdge` is maintained as a **reverse lookup index** for:
- targeted async cache rebuild/invalidation (when `dms.DocumentCache` is enabled)
- `dms.ReferentialIdentity` cascading recompute when upstream identity components change (`IsIdentityComponent=true`)
- delete conflict diagnostics (“who references me?”)

**Write strategy**

Two correct approaches:

1) **Simple replace** (initial implementation)
   - `DELETE FROM dms.ReferenceEdge WHERE ParentDocumentId=@parent`
   - bulk insert the current edges

2) **Diff-based upsert** (recommended to address churn concerns)
   - Stage the current edges into a temp table
   - Insert missing edges
   - Delete stale edges
   - If the reference set did not change, this performs **zero** writes to `dms.ReferenceEdge`

**PostgreSQL example (diff-based)**

```sql
-- Per-session staging table
CREATE TEMP TABLE IF NOT EXISTS reference_edge_stage (
  ChildDocumentId bigint NOT NULL PRIMARY KEY,
  IsIdentityComponent boolean NOT NULL
) ON COMMIT DELETE ROWS;

-- For each invocation (within the same transaction as the resource write):
DELETE FROM reference_edge_stage;

-- Stage current edges (already deduped by ChildDocumentId; IsIdentityComponent is OR'd in application code)
INSERT INTO reference_edge_stage (ChildDocumentId, IsIdentityComponent)
VALUES
  (@ChildDocumentId1, @IsIdentityComponent1),
  (@ChildDocumentId2, @IsIdentityComponent2);

-- Insert missing edges
INSERT INTO dms.ReferenceEdge (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
SELECT @ParentDocumentId, s.ChildDocumentId, s.IsIdentityComponent
FROM reference_edge_stage s
LEFT JOIN dms.ReferenceEdge e
  ON e.ParentDocumentId = @ParentDocumentId
 AND e.ChildDocumentId = s.ChildDocumentId
WHERE e.ParentDocumentId IS NULL;

-- Update IsIdentityComponent when it changes (collapsed edge granularity)
UPDATE dms.ReferenceEdge e
SET IsIdentityComponent = s.IsIdentityComponent
FROM reference_edge_stage s
WHERE e.ParentDocumentId = @ParentDocumentId
  AND e.ChildDocumentId  = s.ChildDocumentId
  AND e.IsIdentityComponent IS DISTINCT FROM s.IsIdentityComponent;

-- Delete stale edges
DELETE FROM dms.ReferenceEdge e
WHERE e.ParentDocumentId = @ParentDocumentId
  AND NOT EXISTS (
    SELECT 1
    FROM reference_edge_stage s
    WHERE s.ChildDocumentId = e.ChildDocumentId
  );
```

**SQL Server sketch (diff-based)**
- Use a `#reference_edge_stage` temp table or a table-valued parameter.
- Use `INSERT ... WHERE NOT EXISTS` + `UPDATE ... FROM` + `DELETE ... WHERE NOT EXISTS` (avoid `MERGE` unless you have strong operational confidence in its behavior under concurrency).

##### Correctness requirement: `dms.ReferenceEdge` must be complete

This redesign makes `dms.ReferenceEdge` a **strict** dependency index:

- `dms.ReferenceEdge` is the only reverse index used to compute:
  - the **identity-dependency closure** for `dms.ReferentialIdentity` recompute (`IsIdentityComponent=true`), and
  - the impacted set for **representation-version** (`dms.Document.Etag` / `LastModifiedAt`) cascades when reference identities or descriptor URIs change.
- If an outgoing reference is missing an edge, then upstream changes will silently miss dependents:
  - stale `dms.ReferentialIdentity` rows (identity-based upsert/reference resolution becomes incorrect), and
  - stale `_etag` / `_lastModifiedDate` (representation-sensitive optimistic concurrency becomes incorrect).

**Definition of completeness (for one parent document)**

For a given `ParentDocumentId`, the set of edges in `dms.ReferenceEdge` must equal the set implied by relational storage:

- For every distinct non-null referenced `ChildDocumentId` produced by any document-reference or descriptor FK column in any root/child table row belonging to the parent document, there exists a corresponding `(ParentDocumentId, ChildDocumentId)` row.
- No extras: `dms.ReferenceEdge` must not contain a `(ParentDocumentId, ChildDocumentId)` row where `ChildDocumentId` does not appear in any FK column for the parent document.
- `IsIdentityComponent` must match the ApiSchema-derived classification **aggregated by child**:
  - it is `true` iff at least one FK binding that produced this `ChildDocumentId` is an identity component.

**Design ideas to make completeness provable**

1) **By-construction edge extraction (preferred default)**
   - Make edge extraction *structural*: derive edges from the same `ResourceWritePlan` column bindings used to populate FK columns.
   - Avoid ad-hoc `edges.Add(...)` calls scattered across code paths.
   - Recommended implementation pattern:
     - Each `DocumentFk` / `DescriptorFk` column in the compiled model carries `IsIdentityComponent`.
     - During row materialization, when a FK column value is produced (non-null), merge an edge keyed by `ChildDocumentId`, OR-ing `IsIdentityComponent` when multiple FK sites reference the same child.
   - Plan compilation/startup validation fails fast if:
     - any ApiSchema reference/descriptor path cannot be mapped to exactly one FK column + `IsIdentityComponent` classification.

2) **Optional in-transaction verification (provable correctness mode, but slow)**
   - In strict environments (or in CI/tests), add a verification step that compares:
     - the staged edge set for this write (what we intend to persist), vs
     - the expected edge set derived from **reading the persisted FK columns** for this `ParentDocumentId`.
   - If there is any mismatch, fail the transaction (no “best effort” window).

   A cross-engine sketch:
   - Stage intended edges: `reference_edge_stage(ChildDocumentId, IsIdentityComponent)`.
   - Populate `reference_edge_expected(ChildDocumentId, IsIdentityComponent)` via a compiled “edge projection query”:
     - one `SELECT` per FK column, filtered to `ParentDocumentId`, with a constant `IsIdentityComponent`,
     - `UNION ALL` across all FK columns in all tables for that resource, then `GROUP BY ChildDocumentId` with `bool_or`/`MAX` to aggregate.
   - Detect mismatch with set differences (`EXCEPT` works on both PostgreSQL and SQL Server):
     - missing edges: `expected EXCEPT stage`
     - extra edges: `stage EXCEPT expected`

   Notes:
   - This verification is expensive if done on every write for very large documents, so it should be configurable (e.g., on in test/CI; sampling or “debug mode” in prod).
   - Even without full verification, a cheap invariant check is to ensure that the number of edges is within an expected range per resource type (guardrail telemetry), but only set-equality gives provable completeness.

3) **Background auditing + repair tooling**
   - Provide an admin job/tool that can:
     - recompute `dms.ReferenceEdge` from relational FK columns for a document (or for all documents), and
     - optionally trigger the corresponding recompute of derived artifacts (`dms.ReferentialIdentity` and representation-version bumps) for impacted closures.
   - This is essential as a recovery mechanism if a bug ever shipped that produced incomplete edges.

### Insert vs update detection

- **Upsert (POST)**: detect an existing row by resolving the request’s `ReferentialId` via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
  - The resource root table’s natural-key unique constraint remains a recommended relational guardrail and is still useful for race detection (unique violation → 409) if two writers attempt to create the same natural key concurrently.
- **Update by id (PUT)**: detect by `DocumentUuid`:
  - Find `DocumentId` from `dms.Document` by `DocumentUuid`.

### Identity updates (AllowIdentityUpdates)

If identity changes on update:
- Treat `dms.ReferentialIdentity` as a derived index and recompute it **transactionally**:
  - update this document’s own referential id mappings (primary + superclass alias), and
  - cascade recompute to any documents whose identities depend on this document (transitively) via `dms.ReferenceEdge` where `IsIdentityComponent=true`.
  - Use `dms.IdentityLock` row-lock orchestration (no global lock) to prevent phantoms and to ensure referential ids are never stale after commit.
- References stored as FKs (`DocumentId`) remain valid; no cascading rewrite needed.
  - If `dms.DocumentCache` is enabled, identity/URI changes of *this* document can enqueue or mark dependent documents for eventual cache rebuild

### Cascade scenarios (tables-per-resource)

Tables-per-resource storage removes the need for **relational** cascade rewrites when upstream natural keys change, because relationships are stored as stable `DocumentId` FKs. Cascades still exist for **derived artifacts** (identity keys, caches, diagnostics), and should be handled explicitly:

- **Identity/URI change on the document itself** (e.g., `StudentUniqueId` update, descriptor `namespace#codeValue` update)
  - Do not rewrite referencing rows: FKs remain correct.
  - Compute and lock the **impacted identity set** (this document, plus all transitive parents over `dms.ReferenceEdge` where `IsIdentityComponent=true`) using `dms.IdentityLock` row-lock orchestration (expand-and-lock to a fixpoint to prevent phantoms).
  - Recompute and upsert `dms.ReferentialIdentity` for every impacted document (primary + superclass/abstract alias rows) in the same transaction, so `ReferentialId → DocumentId` lookups are never stale after commit (even for reference-bearing identities).
  - If `dms.DocumentCache` is enabled, enqueue/mark affected documents for eventual materialization (background projector), rather than performing a transactional cache-update cascade.
  - No rewrite of `dms.ReferenceEdge` rows is required for identity/URI changes: edges store `DocumentId`s and only change when outgoing references change.
  - Update/evict any caching after commit (e.g., `ReferentialId → DocumentId`, `DocumentUuid → DocumentId`, descriptor expansion).

- **Outgoing reference set changes on a document** (FK values change)
  - Relational writes update the FK columns as usual.
  - Maintain edges for the parent with a diff-based upsert (avoid churn on no-op updates).
  - If `dms.DocumentCache` is enabled, refresh the parent’s cached JSON (sync write-through or async rebuild); dependent rebuild remains eventual.

- **ETag / LastModified semantics under derived reference identities (required)**
  - Treat `dms.Document.Etag` and `dms.Document.LastModifiedAt` as **representation** metadata: they must change when referenced identity/descriptor URI changes would alter the reconstituted JSON.
  - Use an opaque representation version token for `Etag` (monotonic `bigint`) that supports cheap freshness checks like `dms.DocumentCache.Etag = dms.Document.Etag`
  - `_lastModifiedDate` must be derived from `dms.Document.LastModifiedAt` (formatted as UTC), not generated on reads or from projection timestamps.
  - When an identity/URI change occurs, update `Etag`/`LastModifiedAt` for the same impacted set used for identity correctness:
    - `IdentityClosure`: this document plus transitive parents over `dms.ReferenceEdge` where `IsIdentityComponent=true`
    - `CacheTargets`: the set of DocumentIds whose API representation metadata must be updated (Etag / LastModifiedAt / DocumentCache) when an identity/URI change happens
      - `IdentityClosure union Parents(IdentityClosure)` where `Parents(...)` is 1-hop over **all** `dms.ReferenceEdge` rows
  - Apply the update with a set-based statement (same transaction as `dms.ReferentialIdentity` recompute):
    - PostgreSQL: `UPDATE dms.Document SET Etag = Etag + 1, LastModifiedAt = now() WHERE DocumentId IN (...CacheTargets...)`
    - SQL Server: `UPDATE dms.Document SET Etag = Etag + 1, LastModifiedAt = sysutcdatetime() WHERE DocumentId IN (...CacheTargets...)`
  - Strictness: computing `Parents(IdentityClosure)` must be **phantom-safe** w.r.t. concurrent writes to `dms.ReferenceEdge` so `CacheTargets` is complete; this design uses **SERIALIZABLE semantics** on the edge-scan (PostgreSQL: SERIALIZABLE transaction; SQL Server: SERIALIZABLE key-range locks on the edge scan). See [Set-based representation-version bump (ETag/LastModifiedAt) — strict and phantom-safe (SERIALIZABLE)](#set-based-representation-version-bump-etaglastmodifiedat--strict-and-phantom-safe-serializable).
  - If `dms.DocumentCache` is enabled, rebuild/refresh cached JSON for `CacheTargets` **eventually** (background projector). The version bump provides a cheap, correct signal that dependent representations changed and drives which rows are stale.

- **Deletes**
  - Correctness is enforced by the FK graph (delete cascades or fails with FK violation).
  - For richer “who references me?” diagnostics, use `dms.ReferenceEdge` (instead of deterministic FK-name mapping).

### Concurrency (ETag)

- Compare `If-Match` header to `dms.Document.Etag` (representation-version token).
- `dms.Document.Etag` is an opaque token (monotonic `bigint`) that changes whenever the representation changes, including due to identity cascades.
- This can cause `If-Match` failures when upstream identity changes alter the representation, which is intentional
- Implement optimistic concurrency as a conditional update so the bump is atomic (e.g., `... WHERE DocumentId=@id AND Etag=@expected`).
- Implement row-level locking per engine (`XLOCK`/`HOLDLOCK` in SQL Server, `FOR UPDATE` in PostgreSQL) when needed.



## Read Path (GET by id / GET query)

Deep dive on reconstitution execution and read-planning: [flattening-reconstitution.md](flattening-reconstitution.md) (section 6).

### GET by id

1. Resolve `DocumentUuid` → `DocumentId` via `dms.Document`.
2. If `dms.DocumentCache` is enabled and a row exists **and is fresh**, return it.
   - Freshness rule (recommended): `dms.DocumentCache.Etag = dms.Document.Etag` (or, as a fallback, `dms.DocumentCache.ComputedAt >= dms.Document.LastModifiedAt`).
3. Otherwise, reconstitute JSON from relational tables and return it (and optionally enqueue/mark for background materialization).

The returned JSON representation must preserve:
- Array order (via `Ordinal`)
- Required vs optional properties
- The API surface properties (`id`, `_etag`, `_lastModifiedDate`)

### Query

Filter directly on **resource root table columns** and (when needed) resolve reference/descriptor query terms to `DocumentId`s.

Contract/clarification:
- `queryFieldMapping` is constrained in ApiSchema to **root-table** paths (no JSON paths that cross an array boundary like `[*]`). This constraint is enforced by **MetaEd** at schema generation time, so query compilation does not need child-table `EXISTS (...)` / join predicate support.
- Backend model compilation should still fail fast if any `queryFieldMapping` path cannot be mapped to a root-table column.

Ordering/paging contract:
- Collection GET results are ordered by the **resource root table’s** `DocumentId` (ascending).
- This is an acceptable ordering contract because `DocumentId` is a monotonic identity value allocated at insert time and therefore roughly correlates with created order.
- Pagination applies to that ordering (`offset` skips N rows in `DocumentId` order; `limit` bounds the page size).
- Paging queries are executed against the **resource root table** (and any required authorization joins), not against `dms.Document`.
- Response materialization joins the page’s `DocumentId`s to `dms.Document` to obtain API fields (`id`, `_etag`, `_lastModifiedDate`). If `dms.DocumentCache` is enabled, it can be used as a best-effort projection; otherwise reconstitute the page from relational tables.

1. Translate query parameters to typed filters using Core’s canonicalization rules (`ValidateQueryMiddleware` → `QueryElement[]`).
2. Build a SQL predicate plan from `ApiSchema`:
   - **Scalar query fields**: `queryFieldMapping` JSON path → derived root-table column → `r.Column = @value`
   - **Descriptor query fields** (e.g., `schoolTypeDescriptor`): normalize URI, compute descriptor `ReferentialId` (descriptor resource type from `ApiSchema` + normalized URI) → resolve `DocumentId` via `dms.ReferentialIdentity` → `r.DescriptorIdColumn = @descriptorDocumentId`
   - **Document reference query fields** (e.g., `studentUniqueId` on `StudentSchoolAssociation`): resolve the referenced resource identity → referenced `DocumentId` (or set of `DocumentId`s) → `r.Student_DocumentId = @documentId` (or `IN (...)`)
3. Execute a `DocumentId` page query:
   - `SELECT r.DocumentId FROM {schema}.{Resource} r`
   - Apply compiled predicates (intersection/AND across query terms; OR within a single query field if it maps to multiple JSON paths/columns)
   - Apply ordering and paging (`ORDER BY r.DocumentId OFFSET @offset LIMIT @limit`)

Example (PostgreSQL) - filter Students by `lastSurname`, return the page starting at offset 50:

```sql
SELECT r.DocumentId
FROM edfi.Student AS r
WHERE r.LastSurname = @LastSurname
ORDER BY r.DocumentId
OFFSET 50
LIMIT @Limit;
```

Then join the page to `dms.Document` (for API metadata). If `dms.DocumentCache` is enabled, it can optionally be joined/used as a best-effort projection for faster response materialization:

```sql
WITH page AS (
  SELECT r.DocumentId
  FROM edfi.Student AS r
  WHERE r.LastSurname = @LastSurname
  ORDER BY r.DocumentId
  OFFSET @Offset
  LIMIT @Limit
)
SELECT p.DocumentId, d.DocumentUuid, d.Etag, d.LastModifiedAt
FROM page p
JOIN dms.Document d ON d.DocumentId = p.DocumentId
ORDER BY p.DocumentId;
```

4. Fetch documents:
   - Return cached JSON when available (if enabled), otherwise reconstitute.

Reference/descriptor resolution is metadata-driven (no per-resource code):
- For **descriptor** query params: compute the descriptor `ReferentialId` (descriptor resource type from `ApiSchema` + normalized URI) and resolve `DocumentId` via `dms.ReferentialIdentity`.
- For **document reference** query params: use `documentPathsMapping.referenceJsonPaths` to group query terms by reference, and:
  - If all referenced identity components are present, resolve a single referenced `DocumentId` via `dms.ReferentialIdentity` by computing the referenced resource’s `ReferentialId`.
  - If only a subset is present, resolve a *set* of referenced `DocumentId`s by filtering the referenced root table using the available identity components (including resolving any referenced identity components that are present), then filter the FK column with `IN (subquery)` (or an equivalent join).

Indexing:
- Create B-tree indexes on the resource root columns used by `queryFieldMapping` (scalar columns and FK columns).
- Rely on existing unique/identity indexes on referenced resource natural-key columns (and on `dms.ReferentialIdentity.ReferentialId`) to make reference resolution fast.



## Caching (Low-Complexity Options)

### Recommended cache targets

1. **Derived relational mapping (from `ApiSchema`)**
   - Cache the derived mapping per `(EffectiveSchemaHash, ProjectName, ResourceName)`.
   - Includes: JsonPath→(table,column), collection table names, query param→column/type plan, and prepared SQL templates.
   - Invalidation: effective schema change + restart (natural).

2. **`dms.ReferentialIdentity` lookups**
   - Cache `ReferentialId → DocumentId` for identity/reference resolution (all identities, including reference-bearing and abstract/superclass aliases).
   - Invalidation:
     - on insert: add cache entry after commit
     - on identity/URI change: evict old `ReferentialId` keys and write new mappings after commit (note: identity cascades can touch many documents; if you cannot evict/update impacted keys reliably, disable this cache or treat it as best-effort and fall back to DB for correctness)
     - on delete: remove relevant entries (or rely on short TTL)

3. **Descriptor expansion lookups** (optional)
   - Cache `DescriptorDocumentId → Uri` (and optionally `Discriminator`) to reconstitute descriptor strings without repeated joins.
   - Descriptor resolution/validation itself is via `dms.ReferentialIdentity` and is covered by cache #2 (descriptor referential ids are regular referential ids).

4. **`DocumentUuid → DocumentId`**
   - Cache GET/PUT/DELETE resolution.
   - Invalidation: add on insert, remove on delete (or rely on TTL).

5. **`dms.DocumentCache` (optional materialization; preferred eventual consistency)**
   - Materialize API JSON as a convenience projection for faster reads and CDC/indexing integrations (e.g., Debezium → Kafka).
   - Maintenance (preferred):
     - write-driven/background projector (queue or sweep) so rows exist for CDC consumers
     - optional read-triggered “rebuild hint” (but not the only trigger)
     - no transactional cross-document cache cascades by default


### Cache keying strategy

- Always include `(DmsInstanceId)` in cache keys.
- For Redis, prefix keys with `dms:{DmsInstanceId}:...`.

### Local-only (per-node) option

Use an in-process `MemoryCache`:
- Lowest complexity; no network hop.
- Good for: derived mapping, `ReferentialId → DocumentId` (including descriptor referential ids), descriptor expansion.


### Redis (distributed) option

Add Redis as an optional L2 cache:
- Reduces DB round-trips across nodes for hot lookups.
  - Keep it simple:
    - cache-aside reads
    - write-through updates after successful commit

Invalidation approaches (choose one):
- **TTL-only** (simplest): acceptable for non-critical caches; for `ReferentialId → DocumentId` TTL-only staleness can cause incorrect resolution due to identity updates/cascades
- **Best-effort delete on writes**: on identity updates/deletes, delete affected Redis keys after commit.
- Optional later: pub/sub “invalidate key” messages to reduce staleness for local L1 caches.

### Invalidation rules (recommended)

- **Successful write transaction commit**:
  - update local/Redis caches for the touched identity/descriptor/uuid keys
  - do not populate caches before commit
- **Identity updates** (`AllowIdentityUpdates=true`):
  - explicitly evict old `ReferentialId` keys and write new mappings after commit (including any cascade-updated documents’ identities, not just the directly-updated document)
  - if caching “reference identity fragments” (`DocumentId → natural key values`), evict those for any resource whose identity fields are affected by the cascade

### ReferentialIdentity maintenance via `dms.ReferenceEdge` (transactional cascade; strict)

Because this redesign uses `ReferentialId → DocumentId` resolution for **all** identities (including reference-bearing and abstract/superclass aliases), `dms.ReferentialIdentity` must be maintained as a **strict derived index**:

- Any identity/URI-affecting write updates the changed document’s own `dms.ReferentialIdentity` rows (primary + optional superclass alias). This includes changes to:
  - scalar identity values on the document itself,
  - identity-component FK values (changing which document the identity depends on), and
  - identity-component descriptor URIs (because the identity includes the descriptor string).
- If the changed identity/URI is an **identity component** of other resources’ identities, the backend must synchronously recompute those dependent documents’ referential ids as well.

The dependency graph is `dms.ReferenceEdge` filtered by `IsIdentityComponent=true` (reverse direction: `ChildDocumentId → ParentDocumentId`).

#### Phantom-safe impacted-set locking

This section is a concrete, implementation-ready locking spec for the strict invariants in this redesign:
- `dms.ReferentialIdentity` is a **strict derived index** (never stale after commit), and
- identity/URI-changing writes must be safe under concurrency (no missed dependents, no stale-at-birth referential ids).

The system accomplishes this with per-document row locks in `dms.IdentityLock` plus a strict lock ordering contract.

##### Definitions

- **Identity component edge**: a `dms.ReferenceEdge` row where `IsIdentityComponent=true`. This is derived from ApiSchema (`identityJsonPaths` + `documentPathsMapping`) and indicates “the parent’s identity depends on the child’s identity/URI”.
- **Seed**: a `DocumentId` whose identity/URI is changing in the current transaction (the document being written, and any other document whose identity/URI is changed as part of the same transaction).
- **Identity closure**: the transitive set of documents that must have their `dms.ReferentialIdentity` recomputed when a seed document’s identity/URI changes:  
  the seed document(s) plus every parent document reachable by repeatedly following identity component edges in reverse (`ChildDocumentId → ParentDocumentId`).
- **Shared identity lock**: a transaction-held lock on `dms.IdentityLock(DocumentId)` that blocks other transactions from taking an update/exclusive lock on that same row. Used by writers of parents to prevent identity-component children from changing while computing derived identities.
- **Update identity lock**: a transaction-held lock on `dms.IdentityLock(DocumentId)` used by the transaction that is allowed to change the derived identity (and/or representation-version) for that document.

##### Example

Assume:
- `Student` has `DocumentId=100` and its identity is changing (it's a seed).
- `School` has `DocumentId=200`
- `StudentSchoolAssociation` (SSA) has `DocumentId=300` and is in the `IdentityClosure` because its identity depends on Student.
- SSA’s identity depends on Student + School, so it will maintain identity component edges:
  - `dms.ReferenceEdge(ParentDocumentId=300, ChildDocumentId=100, IsIdentityComponent=true)`
  - `dms.ReferenceEdge(ParentDocumentId=300, ChildDocumentId=200, IsIdentityComponent=true)`

**Tx A (identity update): update Student identity (seed = 100)**

1. Acquire update identity lock on `dms.IdentityLock(100)`.
2. Expand identity closure by querying `dms.ReferenceEdge` for `ChildDocumentId=100 AND IsIdentityComponent=true`:
   - finds parent `DocumentId=300` (SSA).
3. Acquire update identity lock on `dms.IdentityLock(300)`.
4. Recompute and replace `dms.ReferentialIdentity` rows for impacted documents (`100` and `300`) in the same transaction, then commit.

**Tx B (normal write): write SSA (parent = 300)**

1. Resolve references to `DocumentId`s in bulk → `{ Student=100, School=200 }`.
2. Acquire shared identity locks on `dms.IdentityLock(100)` and `dms.IdentityLock(200)` (ascending `DocumentId`) **before** acquiring the parent update lock.
3. Acquire update identity lock on `dms.IdentityLock(300)`.
4. Write SSA rows + maintain identity component edges, then commit.

Optional optimization:
- If the written resource type is known (from the effective ApiSchema + configuration) to (a) never be in an identity closure and (b) never allow identity/URI updates, the writer may omit the parent update identity lock; and if `IdentityComponentChildren` is empty for this write (no ApiSchema-derived `IsIdentityComponent=true` bindings), the shared identity locks on children can be omitted as well. This does not apply to SSA in this example because SSA participates in identity closures (its identity depends on Student/School).

**Why this avoids deadlocks and is phantom-safe**

- If Tx A is changing `100`, Tx B blocks at step 2 (shared-locking `100`) and therefore never holds the parent update lock while waiting on the child, preventing the deadlock pattern “Tx B holds `300` and waits on `100` while Tx A holds `100` and waits on `300`”.
- While Tx A holds the update lock on `100`, no other transaction can commit a *new* identity component edge into child `100` (Invariant A requires that writer to hold a shared lock on `dms.IdentityLock(100)`), so Tx A’s closure expansion cannot miss dependents that appear “behind its back”.

##### Required invariants

**Invariant A — child locked before parent identity edge**

Before inserting/updating any identity component edge row:
- `dms.ReferenceEdge(ParentDocumentId=P, ChildDocumentId=C, IsIdentityComponent=true)`

the writer **MUST** hold a **shared identity lock** on `dms.IdentityLock(C)` for the duration of the transaction.

Rationale:
- Prevents “stale-at-birth” referential ids when a child’s identity/URI is concurrently changing.
- Makes closure expansion phantom-safe: while a seed document `C` is update-locked, no other transaction can commit a *new* identity component edge into `C`.

**Invariant B — lock ordering (deadlock avoidance)**

Any write transaction that acquires both:
- shared identity locks on child documents, and
- an update identity lock on the parent document

**MUST** acquire locks in this order:
1) shared locks for all required child `DocumentId`s (sorted ascending), then  
2) update lock(s) for the parent `DocumentId`(s) (sorted ascending).

This prevents a classic deadlock where:
- Tx1 (identity update) holds update lock on `C` and tries to update-lock `P`, while
- Tx2 (write to `P`) holds update lock on `P` and tries to shared-lock `C`.

**Invariant C — closure recompute is atomic**

If a transaction changes the identity/URI of any seed document, it **MUST**:
- lock the full identity closure to a fixpoint (Algorithm 2 below), and
- recompute and replace `dms.ReferentialIdentity` rows for every document in that closure

in the **same database transaction** as the write. If the recompute fails, the transaction must roll back.

##### Lock primitives (by engine)

All multi-row lock acquisitions **MUST** lock in ascending `DocumentId` order.

**PostgreSQL**
- Shared identity lock:  
  `SELECT 1 FROM dms.IdentityLock WHERE DocumentId = ANY(@ids) ORDER BY DocumentId FOR SHARE;`
- Update identity lock:  
  `SELECT 1 FROM dms.IdentityLock WHERE DocumentId = ANY(@ids) ORDER BY DocumentId FOR UPDATE;`

**SQL Server**
- Shared identity lock (hold to end of transaction):  
  `SELECT DocumentId FROM dms.IdentityLock WITH (HOLDLOCK) WHERE DocumentId IN (...) ORDER BY DocumentId;`
- Update identity lock (must conflict with shared):  
  `SELECT DocumentId FROM dms.IdentityLock WITH (XLOCK, HOLDLOCK) WHERE DocumentId IN (...) ORDER BY DocumentId;`

Notes:
- For large `IN (...)` sets, use a temp table or table-valued parameter for `@ids` and join to it.
- The SQL Server “update identity lock” intentionally uses `XLOCK` to ensure it blocks shared locks; `UPDLOCK` does not reliably provide that blocking relationship.

##### Algorithm 1 — Normal write: identity-dependent locking (POST/PUT)

This algorithm applies to any write where the document’s identity depends on referenced identities (reference-bearing identities) and/or where the document will write any identity component edges.

1) Resolve references → `DocumentId`s in bulk (via `dms.ReferentialIdentity`).
2) Compute `IdentityComponentChildren`:
   - the set of referenced `ChildDocumentId`s whose bindings are `IsIdentityComponent=true` for this resource (including descriptor references when the descriptor URI participates in identity).
3) Acquire shared identity locks on `IdentityComponentChildren` (ascending `DocumentId`).  
   - If this blocks, it is because an identity-affecting transaction is in progress for one of the children; wait (or let the DB deadlock detector choose a victim).
4) Resolve / allocate this document’s `DocumentId`:
   - insert path: insert `dms.Document`, then insert `dms.IdentityLock(DocumentId)` for the new `DocumentId`
   - update path: lookup `DocumentId` first (by `DocumentUuid` or by referential-id upsert resolution)
5) Acquire update identity lock on `dms.IdentityLock(DocumentId)` for the document being written.
   - Optional optimization: if the resource type is known (from the effective ApiSchema + configuration) to (a) never be in an identity closure and (b) never allow identity/URI updates, this update identity lock can be omitted; and if `IdentityComponentChildren` is empty for this write (no ApiSchema-derived `IsIdentityComponent=true` bindings), step 3’s shared child locks can be omitted as well.
6) Write the relational rows (root + children) and maintain `dms.ReferenceEdge` rows.
7) Detect whether this write changed the document’s identity/URI.
   - If yes, run Algorithm 2 (closure expansion + locks), then recompute `dms.ReferentialIdentity` (Set-based recompute section).

##### Algorithm 2 — Identity closure expansion + locking (fixpoint)

This algorithm is used inside an identity/URI-affecting transaction to lock the full identity closure (no global lock).

Inputs:
- `SeedIds`: one or more `DocumentId`s whose identity/URI changed in this transaction (typically the written document’s `DocumentId`)

Procedure:
1) Acquire update identity locks on `SeedIds` (ascending).
2) Initialize:
   - `Impacted = SeedIds`
   - `Frontier = SeedIds`
3) Repeat until fixpoint:
   1. Query parents of the current frontier:

      ```sql
      SELECT DISTINCT ParentDocumentId
      FROM dms.ReferenceEdge
      WHERE ChildDocumentId IN (@Frontier)
        AND IsIdentityComponent = true;
      ```

   2. Let `NewParents = Parents - Impacted`.
   3. If `NewParents` is empty, stop.
   4. Acquire update identity locks on `NewParents` (ascending).
   5. Set:
      - `Impacted = Impacted ∪ NewParents`
      - `Frontier = NewParents`

Correctness argument (why fixpoint is phantom-safe):
- While a document `C` is update-locked in `dms.IdentityLock`, any writer that would insert an identity component edge into `C` must first acquire a shared identity lock on `C` (Invariant A), and therefore cannot commit during the cascade. This prevents the impacted set from growing “behind our back”.

##### Deadlock + retry policy

- Deadlocks are still possible under contention (e.g., overlapping closures). The correct response is to roll back and retry the **entire** identity-affecting transaction.
- Recommended: bounded retry (e.g., 3 attempts) with jittered backoff.
- Treat these as retryable (identity/URI-changing transactions + strict representation-version cascades):
  - PostgreSQL: `40P01` (deadlock detected), `40001` (serialization failure)
  - SQL Server: `1205` (deadlock victim), `1222` (lock request timeout, if configured)

This spec assumes the DMS resilience policy retries the full write transaction on these failures.

##### Isolation level guidance

- Default: use **READ COMMITTED + explicit `dms.IdentityLock` row locks** for normal writes and for identity correctness (Algorithms 1–2).
- For **strict, phantom-safe representation-version cascades** (`Etag`/`LastModifiedAt` over `CacheTargets`), use **SERIALIZABLE semantics** for the `Parents(IdentityClosure)` edge scan so the computed `CacheTargets` set is complete:
  - PostgreSQL: run the identity/URI-changing write transaction at **SERIALIZABLE** and retry on `40001`.
  - SQL Server: either run the transaction at **SERIALIZABLE**, or (recommended) keep the transaction `READ COMMITTED` and take **SERIALIZABLE key-range locks** on `dms.ReferenceEdge` during the `Parents(IdentityClosure)` read (via `WITH (HOLDLOCK, SERIALIZABLE)`).

##### Cycle safety (MUST)

Startup schema validation must reject ApiSchema identity graphs with cycles at the **resource-type** level (edge `R → T` when `R`’s identity includes an identity-component reference/descriptor that depends on `T`). Cycles make Algorithm 2 unsafe and can lead to deadlocks or non-termination.

#### Set-based recompute

Within the same transaction (and while holding the impacted-set locks):

1) For each impacted document, compute the identity element values from relational storage using ApiSchema-derived `IdentityProjectionPlan`s (see section 7.5 in [flattening-reconstitution.md](flattening-reconstitution.md)), including joins through FK columns for reference-bearing identities and descriptor URI projection.
2) Compute new `ReferentialId` values in application code using the existing UUIDv5 algorithm (same canonical identity element ordering and path strings as Core).
3) Stage and replace `dms.ReferentialIdentity` rows for the impacted set (delete-by-`DocumentId`+`{ProjectName, ResourceName}` then insert staged rows). Include superclass/abstract alias rows for subclass resources.

If the recompute fails (identity conflict/unique violation, deadlock without successful retry, etc.), the transaction must roll back (no stale window). Identity conflicts should map to a 409 (same as other natural-key conflicts).

#### Set-based representation-version bump (ETag/LastModifiedAt) — strict and phantom-safe (SERIALIZABLE)

When a document’s identity/URI changes, other documents’ API representations can change without any FK changes (because references are stored as stable `DocumentId`s and reference identity values are reconstituted at read time). To keep representation-sensitive optimistic concurrency correct, DMS must bump `dms.Document(Etag, LastModifiedAt)` not only for the `IdentityClosure`, but also for 1-hop referrers:

- `IdentityClosure`: `Seeds ∪ Parents*(Seeds)` over `dms.ReferenceEdge(IsIdentityComponent=true)`
- `CacheTargets`: `IdentityClosure ∪ Parents(IdentityClosure)` over **all** `dms.ReferenceEdge` rows

**Phantom problem**

If the transaction computes `Parents(IdentityClosure)` under plain `READ COMMITTED` without any additional protection, it can miss a concurrently-committed `(ParentDocumentId, ChildDocumentId)` edge. That produces a missing bump (stale `_etag` / `_lastModifiedDate`) for a document whose representation changed, breaking the design’s strictness goals.

**Why not “lock every referenced child”**

A fully strict alternative is to extend Invariant A so *every* write that inserts/updates a `dms.ReferenceEdge` row (including non-identity edges) must acquire a shared lock on `dms.IdentityLock(ChildDocumentId)`. That prevents phantoms, but it pushes extra locks onto the hot write path and can create high-fanout lock acquisition (many children per document) and increased deadlock risk.

This design instead uses **SERIALIZABLE semantics** on the `Parents(IdentityClosure)` edge read:
- PostgreSQL: SERIALIZABLE transaction + retry (SSI detects phantoms and forces a retry via `40001`)
- SQL Server: SERIALIZABLE key-range locks (blocks concurrent inserts into the scanned key ranges)

This concentrates the coordination cost on the *rarer* identity/URI-changing transactions and on the edge-index ranges that matter, and it benefits directly from the compact `dms.ReferenceEdge` model (one row per `(ParentDocumentId, ChildDocumentId)`).

##### Same example as above but now includes collecting CacheTargets (why SERIALIZABLE matters)

Assume:
- `Student` has `DocumentId=100` and its identity/URI is changing (it's a seed).
- `StudentSchoolAssociation` (SSA) has `DocumentId=300` and is in the `IdentityClosure` because its identity depends on Student.
- `GraduationPlan` has `DocumentId=400` (it exists already) and can reference a Student but its own identity does **not** depend on the Student. However, if Student's identity changes, the `GraduationPlan` document necessarily changes, which needs to be captured by a change to etag/lastModifiedDate.

**Tx A (identity/URI-changing, must bump representation versions)**

1. Update Student (`DocumentId=100`) identity/URI.
2. Lock the `IdentityClosure` and recompute `dms.ReferentialIdentity` for impacted docs (e.g., `100` and `300`).
3. Compute `CacheTargets` by scanning `dms.ReferenceEdge` to find 1-hop referrers of the `IdentityClosure` (`Parents(IdentityClosure)`), then bump `dms.Document(Etag, LastModifiedAt)` for `CacheTargets`.

**Tx B (normal write, adds a new reference edge)**

1. Update `GraduationPlan` (`DocumentId=400`) to add a reference to `Student` (`DocumentId=100`).
2. Maintain `dms.ReferenceEdge(ParentDocumentId=400, ChildDocumentId=100, IsIdentityComponent=false)` and bump `dms.Document(Etag, LastModifiedAt)` for `400`, then commit.

**What can go wrong under READ COMMITTED**

- If Tx B commits the new edge while Tx A is computing `Parents(IdentityClosure)`, Tx A can miss `(400 → 100)` and therefore omit `400` from `CacheTargets`.
- After both commit, the API representation of `GraduationPlan(400)` changes (its Student reference object now reconstitutes from Student’s new identity/URI), but `400`’s representation metadata may not reflect that extra change (stale `_etag` / `_lastModifiedDate` relative to the returned JSON).

**What SERIALIZABLE changes**

- PostgreSQL: Tx A runs at SERIALIZABLE for the edge scan + bump; if Tx B’s insert would create a phantom in Tx A’s read, PostgreSQL will raise `40001` for one of the transactions and DMS retries. On retry, Tx A sees the new edge and includes `400` in `CacheTargets` (or Tx B retries after Tx A, serializing the outcomes).
- SQL Server: Tx A’s SERIALIZABLE edge scan takes key-range locks on the relevant `ChildDocumentId` ranges, so Tx B’s insert into those ranges blocks (or times out/deadlocks and retries). The commits serialize so there is no “missed edge” window: either Tx B commits after Tx A (and its own write bumps `400`), or Tx A retries.

##### PostgreSQL (SERIALIZABLE transaction + retry)

Run identity/URI-changing writes at `SERIALIZABLE` and retry on `40001`.

**SQL sketch**

```sql
-- Precondition: IdentityClosure has already been computed/locked (Algorithms 1–2).
-- Use a temp table so both the edge scan and the update are set-based.
CREATE TEMP TABLE IF NOT EXISTS identity_closure (
  DocumentId bigint PRIMARY KEY
) ON COMMIT DROP;

CREATE TEMP TABLE IF NOT EXISTS cache_targets (
  DocumentId bigint PRIMARY KEY
) ON COMMIT DROP;

-- Populate identity_closure (implementation choice: insert as you lock, or bulk insert at the end).
-- INSERT INTO identity_closure (DocumentId) VALUES (...);

INSERT INTO cache_targets (DocumentId)
SELECT DocumentId FROM identity_closure
ON CONFLICT DO NOTHING;

-- Phantom-safe parent-of-closure read under SERIALIZABLE (SSI).
INSERT INTO cache_targets (DocumentId)
SELECT DISTINCT e.ParentDocumentId
FROM dms.ReferenceEdge e
JOIN identity_closure c
  ON c.DocumentId = e.ChildDocumentId
ON CONFLICT DO NOTHING;

-- Representation-version bump for all cache targets (deduped).
UPDATE dms.Document d
SET Etag = d.Etag + 1,
    LastModifiedAt = now()
FROM cache_targets t
WHERE d.DocumentId = t.DocumentId;
```

**C# sketch (retry loop)**

```csharp
const int maxAttempts = 3;

for (var attempt = 1; attempt <= maxAttempts; attempt++)
{
    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
        // 1) Lock/compute IdentityClosure (Algorithms 1–2).
        // 2) Recompute dms.ReferentialIdentity for the closure (Set-based recompute).
        // 3) Compute CacheTargets (Parents(IdentityClosure)) and bump dms.Document(Etag, LastModifiedAt) using the SQL above.

        await tx.CommitAsync(ct);
        break;
    }
    catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01")
    {
        await tx.RollbackAsync(ct);
        if (attempt == maxAttempts) throw;
        await Task.Delay(JitteredBackoff(attempt), ct);
    }
}
```

Notes:
- Keep the SERIALIZABLE transaction as short as possible: do not perform network calls or long-running work inside it.
- Ensure the parent-of-closure edge scan uses `IX_ReferenceEdge_ChildDocumentId` to keep predicate tracking narrow.

##### SQL Server (SERIALIZABLE key-range locks on the edge scan)

SQL Server can apply SERIALIZABLE semantics narrowly to the edge scan using lock hints, avoiding SERIALIZABLE range locking for the entire transaction.

**T-SQL sketch**

```sql
-- Precondition: #IdentityClosure (DocumentId bigint PRIMARY KEY) is populated.
CREATE TABLE #CacheTargets (DocumentId bigint NOT NULL PRIMARY KEY);

INSERT INTO #CacheTargets (DocumentId)
SELECT DocumentId FROM #IdentityClosure;

-- Phantom-safe parent-of-closure read: take key-range locks on the ChildDocumentId ranges we scan.
INSERT INTO #CacheTargets (DocumentId)
SELECT DISTINCT e.ParentDocumentId
FROM dms.ReferenceEdge e WITH (HOLDLOCK, SERIALIZABLE, INDEX(IX_ReferenceEdge_ChildDocumentId))
JOIN #IdentityClosure c
  ON c.DocumentId = e.ChildDocumentId
WHERE NOT EXISTS (
    SELECT 1 FROM #CacheTargets t WHERE t.DocumentId = e.ParentDocumentId
);

UPDATE d
SET d.Etag = d.Etag + 1,
    d.LastModifiedAt = SYSUTCDATETIME()
FROM dms.Document d
JOIN #CacheTargets t ON t.DocumentId = d.DocumentId;
```

Notes:
- This blocks concurrent inserts into `dms.ReferenceEdge` for the scanned `ChildDocumentId` ranges until the transaction commits, preventing phantoms without requiring locks on every referenced child document.
- Under contention, expect deadlocks (`1205`) or lock timeouts (`1222` if configured); treat these as retryable for identity/URI-changing transactions.

### `dms.DocumentCache` (optional; preferred eventual consistency)

`dms.DocumentCache` is a convenience **projection** of the API JSON representation. The canonical store is relational; correctness does not depend on this table.

One primary purpose is **CDC streaming** (e.g., Debezium → Kafka) of fully materialized documents:
- when enabled, documents should be materialized here via a write-driven/background projector, not only on API reads
- a document may never be read via the API, but downstream consumers still need it to appear in `dms.DocumentCache`

**Preferred maintenance mode: eventual consistency**

- No transactional cross-document cache cascades.
- Projection rows may be missing or stale and can be rebuilt asynchronously by a background projector (reads may also enqueue a rebuild hint, but should not be the only trigger).

Reasons to prefer eventual consistency over strict transactional cache maintenance:
1. Avoids high-fanout write-time work on identity/descriptor changes.
2. Reduces deadlocks/lock contention (bounded write transactions).
3. Decouples JSON projection from cache/edge strictness: cache rebuild can be throttled/retried independently because the canonical store is relational.
4. Simplifies operations: caches can be dropped/rebuilt and throttled independently.
5. Enables deployment-specific tuning (disable during bulk ingest; enable for read-heavy/indexing).

**Write behavior (recommended)**
- For the document being written: enqueue/mark for background materialization (optionally write-through if the deployment can afford it).
- For dependent documents (when referenced identities/descriptor URIs change): enqueue/mark for background rebuild instead of rebuilding inside the write transaction.
- For CDC/backfill: provide an operator-triggered “rebuild all” job that materializes every `dms.Document` row into `dms.DocumentCache` before enabling Debezium snapshot/streaming.

**Read behavior (recommended)**
- If a projection row exists and is fresh, return it.
  - Freshness rule (recommended): `dms.DocumentCache.Etag = dms.Document.Etag` (or fallback to timestamp comparison).
- On projection miss/stale (or when projection is disabled), reconstitute from relational tables; optionally enqueue/mark for background materialization.

**Targeted rebuild using `dms.ReferenceEdge` (recommended)**
- Use `dms.ReferenceEdge` (via the same `CacheTargets` computation described above) to enqueue/mark a targeted rebuild set when identities/descriptors change.
- Unlike the strict mode, this rebuild is not required to complete before commit; it can be throttled/retried.

**Strict mode (optional; not preferred)**
- A strict, transactional “no stale window” cache cascade is possible, but it intentionally is not the baseline because it can reintroduce large fanout and deadlock risk.

#### DocumentCache rebuilder (relational → JSON → upsert)

Whether invoked by a background worker (preferred) or by a read-triggered rebuild hint, the rebuilder:
1. Loads `(DocumentId, ProjectName, ResourceName)` for the target set from `dms.Document` and groups by `(ProjectName, ResourceName)`.
2. Uses compiled `ResourceReadPlan`s to hydrate root/child tables and reconstitute JSON in-memory.
3. Upserts `dms.DocumentCache` rows for the target set, including:
   - `DocumentUuid`, `ProjectName`, `ResourceName`, `ResourceVersion` (copied from `dms.Document`)
   - `Etag`, `LastModifiedAt` (copied from `dms.Document`; drives freshness checks)
   - `DocumentJson` (reconstituted API JSON including `id`, `_etag`, `_lastModifiedDate`)
   - `ComputedAt` (projection timestamp)

Rebuild failures should be retried (with backoff) and should not fail unrelated writes in eventual mode.

### Pseudocode (versioned cache keys)

```csharp
// Cache key namespace: dms:{dmsInstanceId}:{effectiveSchemaHash}:{kind}:{key}
string CacheKey(string kind, string key) =>
    $"dms:{dmsInstanceId}:{effectiveSchemaHash}:{kind}:{key}";

async Task<long?> ResolveReferentialIdAsync(Guid referentialId, CancellationToken ct)
{
    var cacheKey = CacheKey("refid", referentialId.ToString("N"));

    if (localCache.TryGetValue<long>(cacheKey, out var documentId))
        return documentId;

    if (redis is not null && await redis.TryGetAsync<long>(cacheKey, ct) is { } redisDocId)
    {
        localCache.Set(cacheKey, redisDocId, ttl: TimeSpan.FromMinutes(10));
        return redisDocId;
    }

    var dbDocId = await db.QuerySingleOrDefaultAsync<long?>(
        "select DocumentId from dms.ReferentialIdentity where ReferentialId = @ReferentialId",
        new { ReferentialId = referentialId },
        ct);

    if (dbDocId is null)
        return null;

    localCache.Set(cacheKey, dbDocId.Value, ttl: TimeSpan.FromMinutes(10));
    if (redis is not null)
        await redis.SetAsync(cacheKey, dbDocId.Value, ttl: TimeSpan.FromMinutes(10), ct);

    return dbDocId.Value;
}

// After a successful commit:
Task OnIdentityUpsertCommitted(Guid referentialId, long documentId) =>
    CacheWriteThroughAsync(CacheKey("refid", referentialId.ToString("N")), documentId, ttl: TimeSpan.FromMinutes(10));

Task OnIdentityUpdateCommitted(Guid oldReferentialId, Guid newReferentialId, long documentId) =>
    Task.WhenAll(
        CacheDeleteAsync(CacheKey("refid", oldReferentialId.ToString("N"))),
        CacheWriteThroughAsync(CacheKey("refid", newReferentialId.ToString("N")), documentId, ttl: TimeSpan.FromMinutes(10))
    );
```

### Mermaid diagrams

**Write (Upsert) with reference + descriptor caches**

```mermaid
sequenceDiagram
  participant Core as DMS Core
  participant Backend as Backend Store
  participant L1 as Local Cache (L1)
  participant Redis as Redis (L2, optional)
  participant DB as PostgreSQL/SQL Server

  Core->>Backend: Upsert(resourceJson, extracted Referentials)
  Backend->>L1: Resolve Referentials/Descriptors?
  alt L1 hit
    L1-->>Backend: DocumentId/DescriptorId
  else L1 miss
    Backend->>Redis: GET refid/descriptor (optional)
    alt Redis hit
      Redis-->>Backend: value
      Backend->>L1: SET (ttl)
    else Redis miss / disabled
      Backend->>DB: SELECT dms.ReferentialIdentity / dms.Descriptor
      DB-->>Backend: ids
      Backend->>L1: SET (ttl)
      Backend->>Redis: SET (ttl) (optional)
    end
  end
  Backend->>DB: BEGIN; write dms.Document + resource tables; maintain dms.ReferenceEdge; COMMIT
  Backend->>DB: (when enabled) enqueue/mark DocumentCache materialization
  Backend->>L1: write-through identity/uuid/descriptor keys
  Backend->>Redis: write-through keys (optional)
  Backend-->>Core: success
```

**Read (GET by id) with minimal caches**

```mermaid
sequenceDiagram
  participant API as API GET
  participant Backend as Backend Store
  participant DB as PostgreSQL/SQL Server

  API->>Backend: GET /{resource}/{id}
  Backend->>DB: SELECT DocumentId, Etag FROM dms.Document WHERE DocumentUuid=@id
  DB-->>Backend: DocumentId, Etag

  Backend->>DB: SELECT DocumentJson FROM dms.DocumentCache WHERE DocumentId=@DocumentId AND Etag=@Etag (optional)
  alt projection hit (fresh)
    DB-->>Backend: DocumentJson
    Backend-->>API: JSON
  else projection missing/stale / disabled
    Backend->>DB: hydrate resource tables; reconstitute JSON
    Backend->>DB: (optional) enqueue/mark DocumentCache materialization
    Backend-->>API: JSON
  end
```



## Delete Path (DELETE by id)

1. Resolve `DocumentUuid` → `DocumentId`.
2. Attempt delete from `dms.Document` (which cascades to resource tables and identities).
3. Rely on FK constraints from referencing resource tables to `dms.Document` / `dms.Descriptor` to prevent deleting referenced records.

Error reporting:
- SQL Server and PostgreSQL will report FK constraint violations. DMS should map the violated constraint name back to the referencing resource (deterministic FK naming) to produce a conflict response comparable to today’s `DeleteFailureReference`.
- Prefer `dms.ReferenceEdge` for diagnostics (and to address “what references me?” consistently across engines):
  - query `dms.ReferenceEdge` by `ChildDocumentId` to find referencing `ParentDocumentId`s
  - join to `dms.Document` to report referencing resource types

Example (PostgreSQL) conflict diagnostic query:

```sql
SELECT
  p.DocumentUuid         AS ReferencingDocumentUuid,
  p.ProjectName          AS ReferencingProjectName,
  p.ResourceName         AS ReferencingResourceName
FROM dms.ReferenceEdge e
JOIN dms.Document p
  ON p.DocumentId = e.ParentDocumentId
WHERE e.ChildDocumentId = @DeletedDocumentId
ORDER BY p.ProjectName, p.ResourceName, p.DocumentUuid;
```

Example (C#) mapping to today’s `DeleteFailureReference` shape:

```csharp
catch (DbException ex) when (IsForeignKeyViolation(ex))
{
    const string sql = @"
        SELECT DISTINCT p.ResourceName
        FROM dms.ReferenceEdge e
        JOIN dms.Document p
          ON p.DocumentId = e.ParentDocumentId
        WHERE e.ChildDocumentId = @DeletedDocumentId;";

    var resourceNames = (await connection.QueryAsync<string>(
            new CommandDefinition(
                sql,
                new { DeletedDocumentId = documentId },
                transaction: tx,
                cancellationToken: ct)))
        .ToArray();

    if (resourceNames.Length > 0)
        return new DeleteResult.DeleteFailureReference(resourceNames);

    // Defensive fallback: map the violated constraint name when edge maintenance is broken.
    return MapFkConstraintToDeleteFailure(ex);
}
```



## Schema Validation (EffectiveSchema)

This redesign treats schema changes as an **operational concern outside DMS**. DMS does not define any in-place schema evolution behavior; instead it validates compatibility at startup:

- Schema creation/updates are performed by a separate DDL generation utility that builds the same derived relational model as runtime and emits/applies dialect-specific DDL (see [ddl-generation.md](ddl-generation.md)).
- DMS loads the configured core + extension `ApiSchema.json` files and computes `EffectiveSchemaHash` (see [data-model.md](data-model.md) “EffectiveSchemaHash Calculation”).
- DMS reads the database’s recorded schema fingerprint from `dms.EffectiveSchema` + `dms.SchemaComponent`.
- If the fingerprints (or expected schema components) do not match, DMS refuses to start/serve.

This makes schema mismatch a **fail-fast** condition and avoids any automatic in-place schema update behavior in the server.

## Risks / Open Questions

See [overview.md#Risks / Open Questions](overview.md#risks--open-questions).

## Suggested Implementation Phases

See [overview.md#Suggested Implementation Phases](overview.md#suggested-implementation-phases).

## Operational Considerations

### Random UUID index behavior (`dms.ReferentialIdentity.ReferentialId`)

`ReferentialId` is a UUID (deterministic UUIDv5) and is effectively randomly distributed for index insertion. The primary concern is **write amplification** (page splits, fragmentation/bloat), not point-lookup speed.

**SQL Server guidance**
- Use a sequential clustered key (recommended above: cluster on `(DocumentId, ProjectName, ResourceName)`), and keep the UUID key as a **NONCLUSTERED** PK/unique index.
- Consider a lower `FILLFACTOR` on the UUID index (e.g., 80–90) to reduce page splits; monitor fragmentation and rebuild/reorganize as needed.

**PostgreSQL guidance**
- B-tree point lookups on UUID are fine; manage bloat under high write rates with:
  - index/table `fillfactor` (e.g., 80–90) if insert churn is high
  - healthy autovacuum settings and monitoring
  - periodic `REINDEX` when bloat warrants it
- If sustained ingest is extreme, consider hash partitioning `dms.ReferentialIdentity` by `ReferentialId` (e.g., 8–32 partitions) to reduce contention and make maintenance cheaper.

---
