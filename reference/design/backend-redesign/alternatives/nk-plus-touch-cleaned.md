# Backend Redesign Alternative: Identity-Only Natural-Key Propagation + Write-Time Touch Cascades (Cleaned)

## Status

Draft (alternative design exploration).

## Executive summary

This document proposes an alternative to the baseline backend redesign that moves **indirect-update cascades** to the **write path** and pushes more cascade work into the **database**.

The alternative keeps the baseline relational storage model (tables-per-resource, stable `DocumentId` foreign keys), but changes how we keep these derived artifacts correct:

- **Identity correctness** (`dms.ReferentialIdentity` for reference-bearing identities) is maintained by **identity-only natural-key propagation** using `ON UPDATE CASCADE` (or triggers where required), plus per-resource triggers that recompute referential ids row-locally.
- **Representation metadata** (`_etag`, `_lastModifiedDate`, Change Queries `ChangeVersion`) is maintained by a **touch cascade** that updates stored per-document metadata when a referenced document’s **identity projection** changes.
- **Reverse lookups for touch targeting** use a persisted adjacency list `dms.ReferenceEdge`, maintained by a **recompute/diff** routine invoked once per document write transaction.

This trade is attractive only if:
- identity/URI updates are enabled but operationally rare, and
- the system is willing to accept potentially large synchronous fan-out during those rare events (with guardrails).

## How this changes the baseline redesign (explicit deltas)

This alternative intentionally diverges from these baseline design points:

1. **Update tracking is no longer read-time derived**
   - Baseline: `reference/design/backend-redesign/update-tracking.md` derives `_etag/_lastModifiedDate/ChangeVersion` at read time from local tokens plus dependency tokens to avoid write-time fan-out.
   - Alternative: store representation metadata on `dms.Document` and keep it correct by write-time **touch cascades**.
   - Also update: the “avoid write-time fan-out” constraint language in `reference/design/backend-redesign/overview.md` (“ETag/LastModified are representation metadata (required)”).

2. **Reference objects are not purely `..._DocumentId` for identity-component sites**
   - Baseline: `reference/design/backend-redesign/flattening-reconstitution.md` (Derivation algorithm → “Apply `documentPathsMapping`”) suppresses scalar-column derivation for reference-object identity descendants (“a reference object is represented by one FK, not duplicated natural-key columns”).
   - Alternative: for **identity-component reference sites**, also materialize the referenced identity value columns needed for the parent identity, enabling database cascade propagation.

3. **Identity closure recompute is no longer application-managed**
   - Baseline: `reference/design/backend-redesign/transactions-and-concurrency.md` maintains `dms.ReferentialIdentity` for reference-bearing identities via application-managed closure traversal and explicit locking (`dms.IdentityLock`).
   - Alternative: use database cascades (`ON UPDATE CASCADE`) + per-resource triggers to maintain `dms.ReferentialIdentity` row-locally (no closure traversal in application code).

4. **`dms.ReferenceEdge` maintenance moves to “recompute from persisted FK state”**
   - Baseline: `reference/design/backend-redesign/transactions-and-concurrency.md` recommends “by-construction” edge extraction from compiled write plans, with optional in-transaction verification.
   - Alternative: make recompute-from-FKs the default maintenance mechanism (single call after writing root+children), to reduce drift risk and avoid a global `UNION ALL` reverse-lookup view.

## Goals

1. Preserve baseline’s relational-first storage and use of stable `DocumentId` FKs for referential integrity and query performance.
2. Make identity/URI updates and their dependent effects fully correct at commit time (no stale window).
3. Provide ODS-like “indirect update” semantics for representation metadata using write-time updates:
   - if a referenced identity changes, referrers’ `_etag/_lastModifiedDate/ChangeVersion` must change.
4. Avoid “double touch”: documents updated locally by identity-only propagation should not also be touched.
5. Remain feasible on PostgreSQL and SQL Server with the same logical algorithms (engine-specific SQL where necessary).

## Non-goals

- Define the entire read path and query semantics (use baseline documents for flattening/reconstitution and query execution).
- Solve authorization storage/filtering (out of scope as in the baseline redesign).

## Core concepts

### 1) Two dependency types

For a parent document `P` that references document `C`:

1. **Identity component reference**
   - `C`’s projected identity values participate in `P`’s identity (`identityJsonPaths`).
   - If those projected values change, `P`’s `ReferentialId` must change.

2. **Non-identity representation dependency**
   - `C`’s projected identity values appear in `P`’s API representation (as a `{...}Reference` object), but do not participate in `P`’s identity.
   - If they change, `P`’s representation metadata must change, but `P`’s `ReferentialId` does not.

### 2) Two cascade mechanisms (different purposes)

1. **Identity-only natural-key propagation (DB cascade)**
   - Propagates upstream identity changes into only the minimal downstream columns needed to recompute downstream referential ids row-locally.

2. **Touch cascade (DB-driven metadata updates)**
   - When a document’s identity projection changes, update stored representation metadata for **non-identity referrers**.

## Data model changes

### A) Identity-component natural-key columns + composite cascading FKs

For each **identity-component** document reference site (derived from ApiSchema):

1. Keep the stable FK:
   - `<RefName>_DocumentId bigint NOT NULL` (as in the baseline redesign).
2. Add only the referenced identity value columns required by the parent identity, e.g.:
   - `StudentUniqueId`, `SchoolId`, etc.
3. Add a unique constraint on the referenced root table to support a composite FK:
   - `UNIQUE (DocumentId, <IdentityCols...>)`
4. Add a composite FK on the referencing table:
   - `FOREIGN KEY (<RefName>_DocumentId, <IdentityCols...>) REFERENCES <RefTable>(DocumentId, <IdentityCols...>) ON UPDATE CASCADE`

Result:
- when the referenced identity value changes, the DB rewrites only the minimal identity-component columns on dependent rows;
- downstream referential-id recompute triggers become row-local (no identity projection joins, no closure expansion).

### B) Non-identity document references stay `DocumentId`-only

For non-identity references:
- keep only `<RefName>_DocumentId`;
- do not duplicate referenced identity values.

### C) Abstract/polymorphic reference targets require “abstract identity” tables (if used in identities)

`ON UPDATE CASCADE` requires a concrete target table with the required identity columns.

For abstract targets (e.g., `EducationOrganizationReference`), introduce an **identity table per abstract resource**:

- One row per concrete member document
- Columns: `DocumentId` + abstract identity fields + discriminator (optional but useful)
- Maintained by triggers on each concrete member root table

Referencing tables then use composite FKs to the abstract identity table with `ON UPDATE CASCADE`.

This replaces baseline’s “membership validation via `{Abstract}_View` on the write path” for cascaded abstract identities, because the FK implies membership.

### D) Stored representation metadata on `dms.Document` (served by the API)

This alternative serves representation metadata directly from persisted columns and updates them in-transaction:

- `_lastModifiedDate` is read from a stored timestamp.
- `ChangeVersion` is read from a stored monotonic stamp.
- `_etag` is derived from that stamp (or stored alongside it).

To minimize schema width and align with the existing token columns, repurpose the baseline token meanings:

- `dms.Document.ContentVersion` / `ContentLastModifiedAt` become **representation** version/timestamp:
  - bump on local content changes
  - bump on local identity projection changes
  - bump on touch cascades (indirect identity changes)
- `dms.Document.IdentityVersion` / `IdentityLastModifiedAt` remain **identity projection** change signals and are used to trigger touch cascades.

Change Query journals (`dms.DocumentChangeEvent`, `dms.IdentityChangeEvent`) can remain as in the baseline redesign, emitted by triggers on `dms.Document`, and will naturally include touched updates if they update `ContentVersion`.

### E) `dms.ReferenceEdge` (persisted reverse index; touch targeting + diagnostics)

Keep a single adjacency list table:

- one row per `(ParentDocumentId, ChildDocumentId)`
- `IsIdentityComponent` is the OR across all reference sites from the parent to the child

This table is used for:
- touch cascade targeting (`ChildDocumentId → ParentDocumentId`) for **non-identity** referrers
- diagnostics (“who references me?”)
- operational audit/rebuild targeting

## `dms.ReferenceEdge` maintenance: recompute + diff (single call per write)

### Why recompute/diff (vs statement deltas)

The baseline relational write strategy for collections is “replace” (delete existing child rows, insert current rows). If edge maintenance reacts to statement-level deltas, an idempotent update can still:
- delete many edges, then re-insert them, causing high churn and log/WAL pressure.

Recompute/diff instead:
- reads the parent’s **final** FK state once per write, and
- writes only net changes (often 0 rows on idempotent updates).

### Orchestration contract (hard requirement)

Within the document write transaction, after writing the resource root + all child/extension rows:

1. Persist relational rows (root + children + extensions).
2. Call `dms.RecomputeReferenceEdges(@ParentDocumentId)` exactly once.
3. Commit.

If the recompute fails, the whole transaction fails.

SQL Server cannot defer triggers to transaction end, so this must be an explicit call (either from application code or by encapsulating the whole write in a stored procedure). For parity, use the same explicit call on PostgreSQL.

### Projection rule (generated per resource type)

For every derived table belonging to resource `R` (root + child tables):

- identify the root-document id column in that table (the “parent document key” column; see baseline key conventions in `reference/design/backend-redesign/flattening-reconstitution.md`)
- identify every **document-reference FK** column (`..._DocumentId`) in that table
  - exclude descriptor FKs (`..._DescriptorId`) by design
- for each FK column, the generator knows `IsIdentityComponent` from ApiSchema bindings

The generator emits a per-resource “edge projection query” that, given `@ParentDocumentId`, returns distinct `(ChildDocumentId, IsIdentityComponent)` pairs by:

- `UNION ALL` selecting each FK column as `ChildDocumentId` with a constant `IsIdentityComponent`,
- filtering to `ParentDocumentId = @ParentDocumentId` and `ChildDocumentId IS NOT NULL`,
- grouping by `ChildDocumentId` and OR-ing `IsIdentityComponent`.

### Recompute/diff algorithm (conceptual)

Given `ParentDocumentId = P`:

1. Stage expected edges into a temp table `expected(ParentDocumentId, ChildDocumentId, IsIdentityComponent)`.
2. Apply a diff to `dms.ReferenceEdge` scoped to `P`:
   - insert missing
   - update `IsIdentityComponent` changes
   - delete stale

Avoid `MERGE` on SQL Server; use explicit `INSERT ... WHERE NOT EXISTS`, `UPDATE ... FROM`, `DELETE ... WHERE NOT EXISTS`.

## Touch cascade (non-identity referrers only)

### No-double-touch rule (normative)

If a parent references a child via any **identity-component** site, then identity-only natural-key propagation will already cause a local row update on the parent during the cascade, and local stamping will already update representation metadata.

Therefore touch only targets:

```text
TouchTargets(child) =
  { Parent | ReferenceEdge(Parent, child) exists AND IsIdentityComponent=false }
```

Because `IsIdentityComponent` is stored as an OR across all sites for a given `(Parent, Child)` pair, this also handles “same child referenced in multiple places”: if any identity-component site exists, the parent is excluded from touch.

### Trigger event

Touch cascades are triggered by **identity projection changes**, signaled by:

- `dms.Document.IdentityVersion` changing for a child document.

This accommodates:
- direct identity changes (the document itself changed identity values), and
- identity-only propagation (dependents’ identity values changed and their identity projection versions were stamped by triggers).

### Representation stamping granularity (ODS compatibility)

Touched parents must receive **unique per-row** representation versions (not “one stamp per statement/transaction”) so watermark-only clients (`minChangeVersion = last+1`) do not miss rows when many parents are touched.

### PostgreSQL touch trigger sketch

```sql
CREATE OR REPLACE FUNCTION dms.trg_touch_referrers_on_identity_change()
RETURNS trigger AS
$$
BEGIN
  WITH changed_children AS (
      SELECT i.DocumentId
      FROM inserted i
      JOIN deleted  d ON d.DocumentId = i.DocumentId
      WHERE i.IdentityVersion IS DISTINCT FROM d.IdentityVersion
  ),
  touch_targets AS (
      SELECT DISTINCT e.ParentDocumentId
      FROM dms.ReferenceEdge e
      JOIN changed_children c ON c.DocumentId = e.ChildDocumentId
      WHERE e.IsIdentityComponent = false
  ),
  versioned_targets AS (
      SELECT
          t.ParentDocumentId,
          nextval('dms.ChangeVersionSequence') AS NewContentVersion
      FROM touch_targets t
  )
  UPDATE dms.Document p
  SET
      ContentVersion = v.NewContentVersion,
      ContentLastModifiedAt = now()
  FROM versioned_targets v
  WHERE p.DocumentId = v.ParentDocumentId;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;
```

### SQL Server touch trigger sketch

```sql
;WITH changed_children AS (
    SELECT i.DocumentId
    FROM inserted i
    JOIN deleted  d ON d.DocumentId = i.DocumentId
    WHERE i.IdentityVersion <> d.IdentityVersion
),
touch_targets AS (
    SELECT DISTINCT e.ParentDocumentId
    FROM dms.ReferenceEdge e
    JOIN changed_children c ON c.DocumentId = e.ChildDocumentId
    WHERE e.IsIdentityComponent = 0
)
IF OBJECT_ID('tempdb..#touch_targets') IS NOT NULL DROP TABLE #touch_targets;
CREATE TABLE #touch_targets (ParentDocumentId bigint NOT NULL PRIMARY KEY);

INSERT INTO #touch_targets (ParentDocumentId)
SELECT ParentDocumentId FROM touch_targets;

DECLARE @n int = (SELECT COUNT(*) FROM #touch_targets);
IF @n = 0 RETURN;

DECLARE @first bigint;
EXEC sys.sp_sequence_get_range
    @sequence_name      = N'dms.ChangeVersionSequence',
    @range_size         = @n,
    @range_first_value  = @first OUTPUT;

;WITH ordered AS (
    SELECT ParentDocumentId,
           ROW_NUMBER() OVER (ORDER BY ParentDocumentId) - 1 AS rn0
    FROM #touch_targets
),
versioned AS (
    SELECT ParentDocumentId,
           @first + rn0 AS NewContentVersion
    FROM ordered
)
UPDATE p
SET
    ContentVersion = v.NewContentVersion,
    ContentLastModifiedAt = sysutcdatetime()
FROM dms.Document p
JOIN versioned v ON v.ParentDocumentId = p.DocumentId;

DROP TABLE #touch_targets;
```

## Identity maintenance: DB triggers (row-local) + natural-key propagation

### Referential id recompute (row-local triggers)

Per resource root table `{schema}.{R}`:

- `AFTER INSERT`: compute and insert the primary referential id row in `dms.ReferentialIdentity`
- `AFTER UPDATE` (identity columns changed): recompute and replace the row(s)
- `AFTER DELETE`: delete the row(s) (or rely on `ON DELETE CASCADE` via `DocumentId`)

The trigger must match Core’s referential-id computation exactly:

- `ResourceInfoString = ProjectName + ResourceName`
- `DocumentIdentityString = join("#", "$" + IdentityJsonPath + "=" + IdentityValue)` (ordered by `identityJsonPaths`)
- `ReferentialId = UUIDv5(namespace, ResourceInfoString + DocumentIdentityString)`

This implies the DDL generator must also provision a deterministic UUIDv5 function per engine (or rely on an approved extension where available), and emit per-resource trigger expressions for the ordered identity concatenation.

Subclass/superclass alias rows (polymorphic identity behavior) are maintained the same way (up to two rows per document).

### IdentityVersion stamping

When a document’s identity projection changes (direct or cascaded), triggers must:
- update `dms.Document.IdentityVersion` (monotonic stamp from `dms.ChangeVersionSequence`)
- update `dms.Document.IdentityLastModifiedAt`
- also update `dms.Document.ContentVersion/ContentLastModifiedAt` because the representation changed

Touch cascades are triggered by the `IdentityVersion` change.

## Concurrency and operational hardening

This alternative makes cascades synchronous and write-amplifying, so it must have an explicit “bounded failure” story.

### Database isolation defaults

- SQL Server: strongly recommend MVCC reads (`READ_COMMITTED_SNAPSHOT ON`, optionally `ALLOW_SNAPSHOT_ISOLATION ON`) to reduce deadlocks from readers of `dms.ReferenceEdge` during concurrent writes.
- PostgreSQL: default MVCC is sufficient.

### Lock ordering rules (deadlock minimization)

- Any operation updating multiple `dms.Document` rows (touch cascades, rebuild jobs) should lock/update in ascending `DocumentId` order.
- Any operation updating multiple `dms.ReferenceEdge` rows should apply deltas in ascending `(ParentDocumentId, ChildDocumentId)` order.

### Guardrail: cap synchronous touch fan-in

Because a single identity update can be a “hub” event, cap the number of distinct parents that can be touched in one transaction:

- `MaxTouchParents` (default `5_000`)
- Fail closed: if `touch_count > MaxTouchParents`, abort the transaction with a deterministic error code and operator guidance (include `touch_count`, `MaxTouchParents`, and triggering child ids).

### Retry + backpressure (application contract)

The API layer should implement bounded retries with jittered exponential backoff for:
- SQL Server deadlock victim (`1205`)
- PostgreSQL deadlock (`40P01`)
- (optional) serialization failures if any path uses stronger isolation

### Optional serialization of identity-changing writes

To reduce risk of “two large cascades overlap and deadlock each other”, optionally serialize only identity-changing writes using:

- PostgreSQL: `pg_advisory_xact_lock(...)`
- SQL Server: `sp_getapplock` (exclusive, transaction-owned)

## Required operational tooling

1. **Rebuild/backfill**: procedures/jobs to rebuild `dms.ReferenceEdge` from FK sites (full and targeted).
2. **Audit/verify**: procedures/jobs to compare `dms.ReferenceEdge` to the FK projection for sampled parents.
3. **Telemetry**: metrics for edge projection time, edges changed, touched parents, touch duration, guardrail aborts, and deadlock retries.

## Correctness invariants (must-hold)

1. `dms.ReferenceEdge` equals the FK projection of all non-descriptor `..._DocumentId` columns for the parent document (root + child tables), aggregated to `(Parent, Child)` with OR’d `IsIdentityComponent`.
2. Touch targeting is exactly “edges where `IsIdentityComponent=false`” for children whose `dms.Document.IdentityVersion` changed.
3. `dms.ReferentialIdentity` is never stale after commit (row-local triggers + natural-key propagation must fully converge in the same transaction).
4. Every representation change (local or indirect) bumps stored representation metadata (served `_etag/_lastModifiedDate/ChangeVersion`), with per-row unique stamps for touched updates.

## Open questions

1. SQL Server feasibility: do cascade path/cycle restrictions require trigger-based propagation instead of `ON UPDATE CASCADE`?
2. UUIDv5 implementation: do we accept an engine extension dependency (Postgres) and a custom implementation (SQL Server), or do we implement UUIDv5 in pure SQL for both engines?
3. Recompute call placement: always recompute edges after every write, or only when `..._DocumentId` columns may have changed?
4. Index defaults: do we require filtered/partial structures for high-cardinality hubs or `IsIdentityComponent=true` edges to reduce touch and audit cost?

