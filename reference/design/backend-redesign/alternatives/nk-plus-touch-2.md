# Backend Redesign Alternative: Natural-Key Propagation (Identity Only) + Touch Cascades (Non-Identity), v2
## Status

Draft (alternative design exploration).

## Context

`alternatives/natural-key-plus-touch-cascades.md` proposes a DB-centric hybrid:

1. **Natural-key propagation for identity components only** (via `ON UPDATE CASCADE` or trigger-based propagation) to keep reference-bearing identities correct without an application-managed identity-closure traversal.
2. **Touch cascades** to keep stored representation metadata (`_etag/_lastModifiedDate/ChangeVersion`) in sync for **non-identity** reference dependencies.

In that doc (and `alternatives/touch-cascade.md`), the DB finds “who references this changed document?” via a generated global `UNION ALL` projection (e.g., `dms.AllDocumentReferences`), because there is no persisted reverse index.

This v2 alternative keeps the same high-level intent, but replaces the global union view with a **persisted adjacency list** (a `dms.ReferenceEdge`-like table) that is **maintained entirely by database triggers**.

Key motivation:
- eliminate the optimizer/DDL-size risks of a very large `UNION ALL` view across all FK sites,
- keep reverse lookups index-driven and predictable (`ChildDocumentId → ParentDocumentId`),
- keep the “push cascade work to the write path” framing, without pushing “scan all FK sites” work into the cascade trigger itself.

## Goals

1. Avoid a global “all FK sites” union view while still supporting ODS-like indirect-update semantics for stored representation metadata.
2. Keep `DocumentId` as the only persisted join/reference key for relational integrity and query performance.
3. Preserve “no double touch” behavior from `alternatives/natural-key-plus-touch-cascades.md`.
4. Maintain cross-engine feasibility (PostgreSQL + SQL Server).
5. Ensure adjacency maintenance is set-based and does not require per-row procedural loops.

## Non-goals

- Define the full identity-cascade implementation (covered by `alternatives/ods-style-natural-key-cascades.md` and `alternatives/natural-key-plus-touch-cascades.md`).
- Replace the baseline redesign’s application-managed edge maintenance; this document is about a DB-managed alternative.

## Core idea

Introduce a persisted reverse index:

- `dms.ReferenceEdge` (adjacency list): “Parent document `P` references child document `C` via at least one `..._DocumentId` FK site”.

Maintain it with **generated triggers on every table that contains document-reference FK columns** (root tables and child tables).

Then implement “touch cascades” using **only** `dms.ReferenceEdge`:

- On identity projection changes (`dms.Document.IdentityVersion` changes for a child `C`), find referrers by scanning `dms.ReferenceEdge` by `ChildDocumentId`, and touch only eligible parents.

This replaces the `dms.AllDocumentReferences` union view.

## Data model

### `dms.ReferenceEdge` (trigger-maintained)

This alternative needs to support:

- “All referrers” for touch cascades (representation dependencies).
- “Identity-component referrers” for the **no-double-touch** rule.

Because multiple FK sites (and multiple child rows) can reference the same child document, an adjacency list that stores only a single boolean `IsIdentityComponent` becomes hard to maintain correctly on deletes/updates without rescanning.

Recommended shape: keep one row per `(ParentDocumentId, ChildDocumentId)` but add **counts** so triggers can maintain it incrementally.

Conceptual columns:
- `IdentityRefCount`: number of identity-component FK occurrences from `ParentDocumentId` to `ChildDocumentId`
- `NonIdentityRefCount`: number of non-identity FK occurrences from `ParentDocumentId` to `ChildDocumentId`

Derived meaning:
- “edge exists” iff `IdentityRefCount + NonIdentityRefCount > 0`
- “child participates in parent identity” iff `IdentityRefCount > 0`

#### DDL sketch (PostgreSQL)

```sql
CREATE TABLE dms.ReferenceEdge (
    ParentDocumentId bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    ChildDocumentId  bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,

    IdentityRefCount integer NOT NULL DEFAULT 0,
    NonIdentityRefCount integer NOT NULL DEFAULT 0,

    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),

    CONSTRAINT PK_ReferenceEdge PRIMARY KEY (ParentDocumentId, ChildDocumentId),
    CONSTRAINT CK_ReferenceEdge_Counts CHECK (
        IdentityRefCount >= 0
        AND NonIdentityRefCount >= 0
    )
);

-- Reverse lookups (touch cascades / diagnostics)
CREATE INDEX IX_ReferenceEdge_ChildDocumentId
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId, IdentityRefCount, NonIdentityRefCount);

-- Optional: identity-only reverse lookups (closure work / no-double-touch fast path)
CREATE INDEX IX_ReferenceEdge_ChildDocumentId_Identity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE IdentityRefCount > 0;

CREATE INDEX IX_ReferenceEdge_ChildDocumentId_NonIdentity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE NonIdentityRefCount > 0;
```

#### DDL sketch (SQL Server)

```sql
CREATE TABLE dms.ReferenceEdge (
    ParentDocumentId bigint NOT NULL,
    ChildDocumentId  bigint NOT NULL,

    IdentityRefCount int NOT NULL CONSTRAINT DF_ReferenceEdge_IdentityRefCount DEFAULT (0),
    NonIdentityRefCount int NOT NULL CONSTRAINT DF_ReferenceEdge_NonIdentityRefCount DEFAULT (0),

    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_ReferenceEdge_CreatedAt DEFAULT (sysutcdatetime()),

    CONSTRAINT PK_ReferenceEdge PRIMARY KEY CLUSTERED (ParentDocumentId, ChildDocumentId),
    CONSTRAINT FK_ReferenceEdge_Parent FOREIGN KEY (ParentDocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT FK_ReferenceEdge_Child FOREIGN KEY (ChildDocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT CK_ReferenceEdge_Counts CHECK (
        IdentityRefCount >= 0
        AND NonIdentityRefCount >= 0
    )
);

CREATE INDEX IX_ReferenceEdge_ChildDocumentId
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId, IdentityRefCount, NonIdentityRefCount);

-- Optional filtered indexes
CREATE INDEX IX_ReferenceEdge_ChildDocumentId_Identity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE IdentityRefCount > 0;

CREATE INDEX IX_ReferenceEdge_ChildDocumentId_NonIdentity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE NonIdentityRefCount > 0;
```

### Why counts?

Counts let triggers update `dms.ReferenceEdge` using only local `inserted/deleted` row images:

- Inserts increment counts.
- Deletes decrement counts.
- Updates do a “decrement old, increment new” when FK values change.

No table scans are required to determine whether an edge still exists.

If a simpler schema is required, an alternative is to keep the baseline `(ParentDocumentId, ChildDocumentId, IsIdentityComponent)` shape and accept a “recompute per parent” procedure on deletes/updates; this document assumes counts to keep maintenance incremental and predictable.

## Trigger-maintained adjacency list

### Trigger generation rule

For every relational table `T` produced by the mapping/DDL generator:

- Identify its **parent document key** column (`ParentDocumentId`), which is the root document’s `DocumentId`.
  - For a resource root table, this is typically `DocumentId`.
  - For a child/collection table, this is the root `DocumentId` included as part of the composite parent key (per `flattening-reconstitution.md` conventions).
- Identify every **document reference FK column** in `T`:
  - columns of the form `..._DocumentId` that reference `dms.Document(DocumentId)` or a concrete resource table’s `DocumentId`.
  - exclude descriptor FKs (`..._DescriptorId`) by design.
- For each such column, the generator knows whether it is an **identity component site** (`IsIdentityComponent=true`) based on ApiSchema identity bindings.

Emit an `AFTER INSERT/UPDATE/DELETE` trigger on `T` that updates `dms.ReferenceEdge` counts based on changes in those FK columns.

### Set-based delta pattern (conceptual)

Each trigger computes deltas as a set of `(ParentDocumentId, ChildDocumentId, IdentityDelta, NonIdentityDelta)` rows:

- For each inserted row: add `(+1)` to `IdentityDelta` or `NonIdentityDelta` per non-null FK value.
- For each deleted row: add `(-1)` similarly.
- For updates: treat as “deleted old + inserted new” (the `inserted`/`deleted` transition tables already provide this), but emit deltas only for FK columns whose value changed to avoid unnecessary writes.

Then:
1. Apply deltas to `dms.ReferenceEdge` (upsert for positives, update for negatives).
2. Delete any edges where counts reach zero (both counts are `0`).

### PostgreSQL sketch (statement-level trigger with transition tables)

The generator emits one function+trigger per table `T` (names illustrative).

```sql
CREATE OR REPLACE FUNCTION dms.trg_refedge_edfi_studentSchoolAssociation()
RETURNS trigger AS
$$
BEGIN
  -- Aggregate deltas for this table and statement.
  WITH deltas AS (
      -- INSERTED rows: non-identity FK site
      SELECT
          i.DocumentId AS ParentDocumentId,
          i.Program_DocumentId AS ChildDocumentId,
          0 AS IdentityDelta,
          1 AS NonIdentityDelta
      FROM inserted i
      WHERE i.Program_DocumentId IS NOT NULL

      UNION ALL
      -- DELETED rows: non-identity FK site
      SELECT
          d.DocumentId,
          d.Program_DocumentId,
          0,
          -1
      FROM deleted d
      WHERE d.Program_DocumentId IS NOT NULL

      UNION ALL
      -- INSERTED rows: identity-component FK site
      SELECT
          i.DocumentId,
          i.Student_DocumentId,
          1,
          0
      FROM inserted i
      WHERE i.Student_DocumentId IS NOT NULL

      UNION ALL
      -- DELETED rows: identity-component FK site
      SELECT
          d.DocumentId,
          d.Student_DocumentId,
          -1,
          0
      FROM deleted d
      WHERE d.Student_DocumentId IS NOT NULL
  ),
  agg AS (
      SELECT
          ParentDocumentId,
          ChildDocumentId,
          SUM(IdentityDelta) AS IdentityDelta,
          SUM(NonIdentityDelta) AS NonIdentityDelta
      FROM deltas
      GROUP BY ParentDocumentId, ChildDocumentId
  ),
  pos AS (
      SELECT * FROM agg
      WHERE IdentityDelta > 0 OR NonIdentityDelta > 0
  ),
  neg AS (
      SELECT * FROM agg
      WHERE IdentityDelta < 0 OR NonIdentityDelta < 0
  )
  -- Apply positive deltas (insert or increment)
  INSERT INTO dms.ReferenceEdge(ParentDocumentId, ChildDocumentId, IdentityRefCount, NonIdentityRefCount)
  SELECT ParentDocumentId, ChildDocumentId, IdentityDelta, NonIdentityDelta
  FROM pos
  ON CONFLICT (ParentDocumentId, ChildDocumentId)
  DO UPDATE SET
      IdentityRefCount = dms.ReferenceEdge.IdentityRefCount + EXCLUDED.IdentityRefCount,
      NonIdentityRefCount = dms.ReferenceEdge.NonIdentityRefCount + EXCLUDED.NonIdentityRefCount;

  -- Apply negative deltas (must hit existing rows; CK ensures non-negative)
  UPDATE dms.ReferenceEdge e
  SET
      IdentityRefCount = e.IdentityRefCount + n.IdentityDelta,
      NonIdentityRefCount = e.NonIdentityRefCount + n.NonIdentityDelta
  FROM neg n
  WHERE e.ParentDocumentId = n.ParentDocumentId
    AND e.ChildDocumentId  = n.ChildDocumentId;

  -- Remove zero edges (counts can reach 0/0 after both decremented to zero)
  DELETE FROM dms.ReferenceEdge e
  WHERE e.IdentityRefCount = 0 AND e.NonIdentityRefCount = 0;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS TR_edfi_StudentSchoolAssociation_ReferenceEdge ON edfi.StudentSchoolAssociation;
CREATE TRIGGER TR_edfi_StudentSchoolAssociation_ReferenceEdge
AFTER INSERT OR UPDATE OR DELETE ON edfi.StudentSchoolAssociation
REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_refedge_edfi_studentSchoolAssociation();
```

Notes:
- The generator should restrict the trigger to fire only when relevant FK columns change (`AFTER UPDATE OF ...`) to reduce overhead.
- The example uses simplified sites and names; the generator emits one `UNION ALL` branch per FK site in the table.
- Prefer set-based statements; avoid row-level triggers.

### SQL Server sketch (statement-level trigger using `inserted`/`deleted`)

```sql
CREATE OR ALTER TRIGGER edfi.TR_StudentSchoolAssociation_ReferenceEdge
ON edfi.StudentSchoolAssociation
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH deltas AS (
        SELECT
            i.DocumentId AS ParentDocumentId,
            i.Student_DocumentId AS ChildDocumentId,
            CAST(1 AS int) AS IdentityDelta,
            CAST(0 AS int) AS NonIdentityDelta
        FROM inserted i
        WHERE i.Student_DocumentId IS NOT NULL

        UNION ALL
        SELECT
            d.DocumentId,
            d.Student_DocumentId,
            CAST(-1 AS int),
            CAST(0 AS int)
        FROM deleted d
        WHERE d.Student_DocumentId IS NOT NULL

        UNION ALL
        SELECT
            i.DocumentId,
            i.Program_DocumentId,
            CAST(0 AS int),
            CAST(1 AS int)
        FROM inserted i
        WHERE i.Program_DocumentId IS NOT NULL

        UNION ALL
        SELECT
            d.DocumentId,
            d.Program_DocumentId,
            CAST(0 AS int),
            CAST(-1 AS int)
        FROM deleted d
        WHERE d.Program_DocumentId IS NOT NULL
    ),
    agg AS (
        SELECT
            ParentDocumentId,
            ChildDocumentId,
            SUM(IdentityDelta) AS IdentityDelta,
            SUM(NonIdentityDelta) AS NonIdentityDelta
        FROM deltas
        GROUP BY ParentDocumentId, ChildDocumentId
    )
    -- Apply deltas via UPDATE then INSERT (avoid MERGE)
    UPDATE e
    SET
        IdentityRefCount = e.IdentityRefCount + a.IdentityDelta,
        NonIdentityRefCount = e.NonIdentityRefCount + a.NonIdentityDelta
    FROM dms.ReferenceEdge e
    JOIN agg a
      ON a.ParentDocumentId = e.ParentDocumentId
     AND a.ChildDocumentId  = e.ChildDocumentId;

    INSERT INTO dms.ReferenceEdge (ParentDocumentId, ChildDocumentId, IdentityRefCount, NonIdentityRefCount)
    SELECT a.ParentDocumentId, a.ChildDocumentId, a.IdentityDelta, a.NonIdentityDelta
    FROM agg a
    LEFT JOIN dms.ReferenceEdge e
      ON e.ParentDocumentId = a.ParentDocumentId
     AND e.ChildDocumentId  = a.ChildDocumentId
    WHERE e.ParentDocumentId IS NULL
      AND (a.IdentityDelta > 0 OR a.NonIdentityDelta > 0);

    DELETE e
    FROM dms.ReferenceEdge e
    WHERE e.IdentityRefCount = 0 AND e.NonIdentityRefCount = 0;
END;
```

Implementation notes:
- The check constraint should fail the transaction if counts go negative (signals a trigger bug or unexpected write path).
- The generator should short-circuit work for updates that do not touch any FK columns (`IF NOT (UPDATE(Student_DocumentId) OR UPDATE(...)) RETURN;`).

## Touch cascade without the union view

Touch cascades still trigger off the same event as in `alternatives/touch-cascade.md`:

- identity projection changes are signaled by `dms.Document.IdentityVersion` changes (from direct identity updates and/or identity cascades).

But referrer discovery uses `dms.ReferenceEdge`.

### No-double-touch rule (unchanged)

As described in `alternatives/natural-key-plus-touch-cascades.md`:

- identity-component referrers already get a local row update via natural-key propagation, and local stamping updates their representation metadata.

Therefore:

```text
TouchTargets(child) =
  NonIdentityReferrers(child)
  \ IdentityReferrers(child)
```

With counts:
- `NonIdentityReferrers(child)` = parents where `NonIdentityRefCount > 0`
- `IdentityReferrers(child)` = parents where `IdentityRefCount > 0`

### Trigger sketch on `dms.Document` (PostgreSQL)

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
  non_identity_referrers AS (
      SELECT DISTINCT e.ParentDocumentId
      FROM dms.ReferenceEdge e
      JOIN changed_children c ON c.DocumentId = e.ChildDocumentId
      WHERE e.NonIdentityRefCount > 0
  ),
  identity_referrers AS (
      SELECT DISTINCT e.ParentDocumentId
      FROM dms.ReferenceEdge e
      JOIN changed_children c ON c.DocumentId = e.ChildDocumentId
      WHERE e.IdentityRefCount > 0
  ),
  touch_targets AS (
      SELECT ParentDocumentId FROM non_identity_referrers
      EXCEPT
      SELECT ParentDocumentId FROM identity_referrers
  )
  UPDATE dms.Document p
  SET
      RepresentationChangeVersion = nextval('dms.ChangeVersionSequence'),
      RepresentationLastModifiedAt = now()
  WHERE p.DocumentId IN (SELECT ParentDocumentId FROM touch_targets);

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;
```

### SQL Server trigger sketch

```sql
;WITH changed_children AS (
    SELECT i.DocumentId
    FROM inserted i
    JOIN deleted  d ON d.DocumentId = i.DocumentId
    WHERE i.IdentityVersion <> d.IdentityVersion
),
non_identity_referrers AS (
    SELECT DISTINCT e.ParentDocumentId
    FROM dms.ReferenceEdge e
    JOIN changed_children c ON c.DocumentId = e.ChildDocumentId
    WHERE e.NonIdentityRefCount > 0
),
identity_referrers AS (
    SELECT DISTINCT e.ParentDocumentId
    FROM dms.ReferenceEdge e
    JOIN changed_children c ON c.DocumentId = e.ChildDocumentId
    WHERE e.IdentityRefCount > 0
),
touch_targets AS (
    SELECT ParentDocumentId FROM non_identity_referrers
    EXCEPT
    SELECT ParentDocumentId FROM identity_referrers
)
UPDATE p
SET
    RepresentationChangeVersion = NEXT VALUE FOR dms.ChangeVersionSequence,
    RepresentationLastModifiedAt = sysutcdatetime()
FROM dms.Document p
JOIN touch_targets t ON t.ParentDocumentId = p.DocumentId;
```

## Interaction with identity-only natural-key propagation

This v2 alternative assumes the same identity-only propagation model as `alternatives/natural-key-plus-touch-cascades.md`:

- identity-component sites may materialize referenced identity values and use `ON UPDATE CASCADE` to propagate those values locally (or emulate via triggers where required).

Important coupling point:
- `dms.ReferenceEdge` maintenance triggers should be driven only by **`..._DocumentId` FK columns** (the persisted relationship), not by duplicated natural-key identity columns.
  - Natural-key propagation updates the identity columns, not the `DocumentId` FK, so the edge set is stable and should not churn from those cascades.

## Benefits vs a union view

- **Predictable reverse lookup**: `ChildDocumentId → ParentDocumentId` is a single indexed table scan, not a multi-branch union plan.
- **Bounded DDL**: no single global view that grows with every extension and FK site.
- **Reuse**: adjacency can support touch cascades, diagnostics, and (optionally) future change query selection patterns without additional projections.

## Costs / risks

- **Write overhead**: every resource-table write that touches FK columns also writes `dms.ReferenceEdge` (and indexes).
- **Trigger surface area**: DDL generator must emit and validate many triggers (one per table with document FK sites) across engines.
- **High-fan-in touch events remain the tail risk**: even with fast reverse lookup, touching millions of parents is still operationally dangerous (locks/log volume/deadlocks).
- **Constraint/trigger correctness is critical**: a bug can create negative counts or orphan/missing edges; the design relies on DB constraints to fail-fast.

## Required operational tooling

Even with triggers:

1. **Backfill/rebuild**: a job/procedure to rebuild `dms.ReferenceEdge` from the relational FK graph (offline or targeted).
2. **Guardrails**: configurable limits (“max parents to touch per statement/transaction”) with a safe failure mode (abort with clear operator guidance).
3. **Telemetry**: emit per-transaction metrics (number of identity changes, number of touched parents, time spent in touch trigger, deadlock retries).

## Open questions

1. **Stamping granularity**: should touch assign one `RepresentationChangeVersion` per touched row (ODS-like) or one per triggering transaction?
2. **Counts vs recompute**: is incremental count maintenance worth the schema/trigger complexity, or is “recompute edges for affected parents” acceptable given expected write patterns?
3. **Index strategy defaults**: do we require filtered/partial indexes for identity/non-identity edges, or only the general reverse index?
4. **SQL Server cascade feasibility**: if `ON UPDATE CASCADE` is constrained by “multiple cascade paths” rules, does the identity-only propagation still hold, or do we need trigger-based propagation (reintroducing ODS-like trigger complexity)?
