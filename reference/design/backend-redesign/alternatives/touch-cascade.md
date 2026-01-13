# Backend Redesign Alternative: DB “Touch Cascade” for `_etag/_lastModifiedDate/ChangeVersion` (No `dms.ReferenceEdge`)

## Status

Draft (alternative design exploration).

## Context

The baseline backend redesign (tables-per-resource + stable `DocumentId` foreign keys) avoids ODS-style rewrite cascades by:

- storing references as `..._DocumentId` FKs (so natural-key changes do not rewrite referencing rows), and
- deriving `_etag/_lastModifiedDate/ChangeVersion` at read time from `dms.Document` local tokens plus dependency tokens, using `dms.ReferenceEdge` as the outbound/inbound dependency index (`reference/design/backend-redesign/update-tracking.md` and `reference/design/backend-redesign/data-model.md`).

This alternative explores a different trade:

- keep the same surrogate-key relational model, but
- make `_etag/_lastModifiedDate/ChangeVersion` **stored** representation metadata, and
- have the **database** perform the indirect-update behavior by **touching referrers** when a referenced document’s *identity projection* changes,
- **without** any persisted edge/cache table like `dms.ReferenceEdge`.

It is conceptually closer to the Ed-Fi ODS “indirect update cascade triggers” pattern, except:
- we are not cascading FK values (we still use `DocumentId` FKs), and
- we are cascading only *representation metadata* (“this representation changed because a referenced identity changed”).

## Goal

Provide ODS-like externally visible semantics:

- If document `D`’s identity projection changes, then any document `P` whose API representation embeds values from `D`’s identity projection must observe updated `_etag/_lastModifiedDate/ChangeVersion`, **even if `P` itself was not rewritten**.

Do this with:
- in-transaction, write-time metadata updates (no read-time derivation), and
- no `dms.ReferenceEdge`.

## High-Level Idea

1. Store representation metadata for each document in the database (e.g., on `dms.Document`).
2. When a document’s identity projection changes (direct update or identity-closure recompute), the DB computes:
   - `Referrers(D)` = all aggregate roots `P` that reference `D` via any `..._DocumentId` FK site (root or child tables),
3. The DB **touches** those `P` rows’ representation metadata (set-based update).

“Touch” means:
- allocate a new global stamp from `dms.ChangeVersionSequence` (or per-row stamps), and
- set stored `_lastModifiedDate`/`ChangeVersion`/`_etag` to new values.

This is a **1-hop** cascade for representation dependencies:
- only identity changes of the referenced document trigger touches,
- a “touch” does *not* trigger further touches (because it is not an identity change of the touched parent).

## Data Model Sketch

The baseline `dms.Document` already has local tokens (`ContentVersion/IdentityVersion` and timestamps). This alternative adds stored **representation** tokens that are directly served by the API.

Conceptually:

- `RepresentationChangeVersion` (`bigint`): the per-document “current ChangeVersion” returned by Change Queries and used for ETag derivation.
- `RepresentationLastModifiedAt` (`timestamp/datetime2`): the API `_lastModifiedDate`.
- Optional: `RepresentationEtag` (stored string) **or** derive ETag from `RepresentationChangeVersion` (application-side or computed column).

Example (illustrative; not final DDL):

```sql
ALTER TABLE dms.Document
  ADD COLUMN RepresentationChangeVersion bigint NOT NULL DEFAULT 1,
  ADD COLUMN RepresentationLastModifiedAt timestamp with time zone NOT NULL DEFAULT now();
```

Notes:
- `dms.ChangeVersionSequence` (already in the redesign) remains the global monotonic stamp source.
- You can keep `ContentVersion/IdentityVersion` for identity correctness and no-op detection, even if the API stops deriving `_etag/_lastModifiedDate/ChangeVersion` from them.

## No-Edge Reverse Lookup: “All Document FK Sites” Projection

Without `dms.ReferenceEdge`, the database needs a way to answer:

> “Which documents reference `ChildDocumentId = X` anywhere in their relational rows?”

Because the tables-per-resource model stores references as `..._DocumentId` columns, the DB can compute referrers by scanning those FK columns.

### Option A (simplest): one global UNION ALL view

Have the DDL generator emit a single view in `dms` that unions every document-reference FK column across every resource table:

```sql
CREATE VIEW dms.AllDocumentReferences AS
    -- One branch per DocumentFk column across all root/child tables.
    SELECT DocumentId AS ParentDocumentId, School_DocumentId AS ChildDocumentId
    FROM edfi.StudentSchoolAssociation
    WHERE School_DocumentId IS NOT NULL
UNION ALL
    SELECT DocumentId AS ParentDocumentId, Student_DocumentId AS ChildDocumentId
    FROM edfi.StudentSchoolAssociation
    WHERE Student_DocumentId IS NOT NULL
UNION ALL
    SELECT DocumentId AS ParentDocumentId, ClassPeriod_DocumentId AS ChildDocumentId
    FROM edfi.SectionClassPeriod
    WHERE ClassPeriod_DocumentId IS NOT NULL
-- ... etc for every DocumentFk site derived from ApiSchema (descriptors excluded)
;
```

Properties:
- **Correct-by-construction**: it is defined from the physical FK columns; there is no separately maintained cache that can drift.
- Handles nested collections: child tables also carry the root `DocumentId` (as the first parent key part).

Indexing requirement:
- every `..._DocumentId` column must have an index to support reverse lookups without scanning.
  - PostgreSQL: `CREATE INDEX ... ON <table>(<fk>) INCLUDE (DocumentId);`
  - SQL Server: `CREATE INDEX ... ON <table>(<fk>) INCLUDE (DocumentId);`

### Option B (fewer branches per query): per-target referrer projections

Instead of one global view, emit per-target projections (view or inline SQL) so “referrers of `ClassPeriod`” doesn’t have to UNION across unrelated FK sites.

This reduces the work per identity change but increases DDL surface area (one view/function per resource/abstract target).

## “Touch” Trigger / Procedure Design

The cascade trigger needs one reliable event: “this document’s identity projection changed”.

In the baseline redesign, that event is represented by updating `dms.Document.IdentityVersion` during:
- direct identity updates (AllowIdentityUpdates), and
- strict identity-closure recompute.

This alternative leverages the same signal:
- identity correctness remains application-driven (or separately redesigned), and
- the database uses `IdentityVersion` changes to drive representation-touch cascades.

### PostgreSQL sketch (statement-level trigger on `dms.Document`)

```sql
CREATE OR REPLACE FUNCTION dms.trg_touch_referrers_on_identity_change()
RETURNS trigger AS
$$
BEGIN
  -- changed children = rows whose IdentityVersion changed in this statement
  WITH changed_children AS (
      SELECT i.DocumentId
      FROM inserted i
      JOIN deleted  d ON d.DocumentId = i.DocumentId
      WHERE i.IdentityVersion IS DISTINCT FROM d.IdentityVersion
  ),
  impacted_parents AS (
      SELECT DISTINCT r.ParentDocumentId
      FROM dms.AllDocumentReferences r
      JOIN changed_children c
        ON c.DocumentId = r.ChildDocumentId
  )
  UPDATE dms.Document p
  SET
      RepresentationChangeVersion = nextval('dms.ChangeVersionSequence'),
      RepresentationLastModifiedAt = now()
  WHERE p.DocumentId IN (SELECT ParentDocumentId FROM impacted_parents);

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS TR_Document_TouchReferrers ON dms.Document;
CREATE TRIGGER TR_Document_TouchReferrers
AFTER UPDATE OF IdentityVersion ON dms.Document
REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_touch_referrers_on_identity_change();
```

Key points:
- The trigger fires only for `IdentityVersion` changes (touching parents does not re-trigger it).
- The update is in-transaction: no “stale window” after commit.

### SQL Server sketch

```sql
CREATE OR ALTER TRIGGER dms.TR_Document_TouchReferrers
ON dms.Document
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH changed_children AS (
        SELECT i.DocumentId
        FROM inserted i
        JOIN deleted  d ON d.DocumentId = i.DocumentId
        WHERE i.IdentityVersion <> d.IdentityVersion
    ),
    impacted_parents AS (
        SELECT DISTINCT r.ParentDocumentId
        FROM dms.AllDocumentReferences r
        JOIN changed_children c ON c.DocumentId = r.ChildDocumentId
    )
    UPDATE p
    SET
        RepresentationChangeVersion = NEXT VALUE FOR dms.ChangeVersionSequence,
        RepresentationLastModifiedAt = sysutcdatetime()
    FROM dms.Document p
    JOIN impacted_parents ip ON ip.ParentDocumentId = p.DocumentId;
END;
```

Note:
- The trigger will execute for any update to `dms.Document`; it short-circuits when `changed_children` is empty.

## Serving API metadata

Under this alternative:

- API `_lastModifiedDate` is read from `dms.Document.RepresentationLastModifiedAt`.
- API per-item `ChangeVersion` is read from `dms.Document.RepresentationChangeVersion`.
- API `_etag` can be:
  - derived from the stored `RepresentationChangeVersion` (simple and stable), or
  - stored as its own opaque string and updated alongside `RepresentationChangeVersion`.

Optimistic concurrency (`If-Match`) becomes “read stored ETag/version and compare”, with no dependency-token reads.

## Operational Characteristics and Risks

### Write amplification (the big trade)

When a high-fan-in document’s identity changes (e.g., a hub referenced by many documents), the touch cascade performs a large fan-out update:

- extra row locks on `dms.Document`
- extra log/WAL volume
- potential deadlocks with concurrent writers to the impacted parent documents

This is the primary reason the baseline design prefers read-time derivation (`update-tracking.md`).

### Reverse lookup cost without an edge table

Even with indexes, the “global UNION ALL view” approach has two costs:

- planning/execution overhead across many UNION branches, and
- sensitivity to optimizer choices (e.g., whether it uses indexes vs scans for `IN (changed_children)` patterns).

Per-target projections reduce branch count per change but increase generated DDL size.

### Correctness dependencies

Correctness depends on:
- completeness of the generated “all FK sites” projection (must include every `DocumentFk` column in every root/child table), and
- the invariant that every table row participating in a document has its root `DocumentId` available for projection (true under the “parent key parts” key scheme described in `reference/design/backend-redesign/flattening-reconstitution.md`).

Unlike `dms.ReferenceEdge`, there is no risk of *runtime drift* between the projection and the persisted FK graph (because the projection is the graph).

### Interaction with identity closure recompute (out of scope, but coupled)

The baseline redesign uses `dms.ReferenceEdge(IsIdentityComponent=true)` to compute the transitive identity-dependency closure for `dms.ReferentialIdentity` recompute.

If you truly remove `dms.ReferenceEdge`, you still need an alternative “who depends on me for identity?” reverse lookup. The same mechanism used here can be reused:

- generate an **identity-component-only** reverse projection (UNION ALL only for FK sites classified as identity components by ApiSchema), and
- use it for closure expansion.

This can eliminate `dms.ReferenceEdge`, but it moves more work into “scan many FK sites” queries and may increase closure recompute cost substantially.

## When this alternative is attractive

- You strongly prefer **stored** metadata over read-time derivation (simpler reads, simpler `If-Match`).
- Identity changes that affect many referrers are **rare** or operationally acceptable.
- You want to reduce application responsibilities and make the DB the “arbiter” of metadata cascades.

## When to avoid it

- You expect frequent identity updates on high-fan-in entities (fan-out touches become a performance/availability risk).
- You want to keep write transactions narrow and predictable.
- You want Change Queries and metadata semantics without write-time fan-out (baseline design).

## Open Questions

1. **Stamping granularity**: should touch updates allocate one new `RepresentationChangeVersion` per impacted parent row (ODS-like), or one per transaction and apply it to all impacted rows?
2. **Indexing strategy**: do we require `INCLUDE (DocumentId)` coverage indexes on every FK site by default, or only on sites that are likely to be high-fan-in?
3. **Partitioning**: should the generator emit optional partitioning guidance (especially for large deployments) to keep reverse-lookups fast without an edge table?
4. **DDL size limits**: how large can a generated “global UNION ALL view” get across all projects/extensions before engine limits or plan cache behavior becomes problematic?
