# Backend Redesign Alternative: Natural-Key Propagation (Identity Only) + Touch Cascades (Non-Identity), v2
## Status

Draft (alternative design exploration).

## Context

`alternatives/natural-key-plus-touch-cascades.md` proposes a DB-centric hybrid:

1. **Natural-key propagation for identity components only** (via `ON UPDATE CASCADE` or trigger-based propagation) to keep reference-bearing identities correct without an application-managed identity-closure traversal.
2. **Touch cascades** to keep stored representation metadata (`_etag/_lastModifiedDate/ChangeVersion`) in sync for **non-identity** reference dependencies.

In that doc (and `alternatives/touch-cascade.md`), the DB finds “who references this changed document?” via a generated global `UNION ALL` projection (e.g., `dms.AllDocumentReferences`), because there is no persisted reverse index.

This v2 alternative keeps the same high-level intent, but replaces the global union view with a **persisted adjacency list** (a `dms.ReferenceEdge`-like table) that is maintained by a **per-parent recompute/diff routine** invoked once per document write transaction (same approach for PostgreSQL and SQL Server).

Key motivation:
- eliminate the optimizer/DDL-size risks of a very large `UNION ALL` view across all FK sites,
- keep reverse lookups index-driven and predictable (`ChildDocumentId → ParentDocumentId`),
- reduce edge churn under the baseline “replace” write strategy (delete+insert of child rows),
- keep the “push cascade work to the write path” framing, without pushing “scan all FK sites” work into the touch-cascade trigger itself.

## Goals

1. Avoid a global “all FK sites” union view while still supporting ODS-like indirect-update semantics for stored representation metadata.
2. Keep `DocumentId` as the only persisted join/reference key for relational integrity and query performance.
3. Preserve “no double touch” behavior from `alternatives/natural-key-plus-touch-cascades.md`.
4. Maintain cross-engine feasibility (PostgreSQL + SQL Server) with the **same logical maintenance algorithm**.
5. Minimize `dms.ReferenceEdge` churn, especially under “replace” collection writes.
6. Preserve strict ODS change-tracking semantics: touched parents must receive **unique per-row** `dms.Document.ContentVersion` values (used as `ChangeVersion`) so watermark-only clients (`minChangeVersion = last+1`) do not miss rows.

## Non-goals

- Define the full identity-cascade implementation (covered by `alternatives/ods-style-natural-key-cascades.md` and `alternatives/natural-key-plus-touch-cascades.md`).
- Replace the baseline redesign’s application-managed edge maintenance; this document is about a DB-managed alternative for touch cascades.

## Core idea

Introduce a persisted reverse index:

- `dms.ReferenceEdge` (adjacency list): “Parent document `P` references child document `C` via at least one `..._DocumentId` FK site”.

Maintain it by recomputing the parent’s edge set from its **final persisted FK state** and applying a **diff**:

- After a document write finishes writing the resource root + all child/extension rows (still inside the transaction), run `dms.RecomputeReferenceEdges(P)`.
- The routine projects the parent’s current `..._DocumentId` FK values (root + children), dedupes, ORs identity-component classification, then upserts/deletes edges so `dms.ReferenceEdge` equals the projection.

Then implement “touch cascades” using **only** `dms.ReferenceEdge`:

- On identity projection changes (`dms.Document.IdentityVersion` changes for a child `C`), find referrers by scanning `dms.ReferenceEdge` by `ChildDocumentId`, and touch only eligible parents.

This replaces the `dms.AllDocumentReferences` union view.

## Data model

### `dms.ReferenceEdge` (recompute/diff-maintained)

This alternative needs to support:

- “All referrers” for touch cascades (representation dependencies).
- “Identity-component referrers” for the **no-double-touch** rule.

Shape:

- One row per `(ParentDocumentId, ChildDocumentId)`.
- `IsIdentityComponent` means: the parent has **any** identity-component reference site to this child (OR across sites/rows).
  - `IsIdentityComponent=true` ⇒ “do not touch this parent in the touch cascade” (it is assumed to be locally stamped by identity-only natural-key propagation).
  - `IsIdentityComponent=false` ⇒ the edge exists only via non-identity reference sites and is eligible for touch.

#### DDL sketch (PostgreSQL)

```sql
CREATE TABLE dms.ReferenceEdge (
    ParentDocumentId bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    ChildDocumentId  bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    IsIdentityComponent boolean NOT NULL,
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_ReferenceEdge PRIMARY KEY (ParentDocumentId, ChildDocumentId)
);

-- Reverse lookups (touch cascades / diagnostics)
CREATE INDEX IX_ReferenceEdge_ChildDocumentId
    ON dms.ReferenceEdge (ChildDocumentId, IsIdentityComponent)
    INCLUDE (ParentDocumentId);

-- Optional partial indexes (only if needed after measurement)
CREATE INDEX IX_ReferenceEdge_ChildDocumentId_Identity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE IsIdentityComponent;

CREATE INDEX IX_ReferenceEdge_ChildDocumentId_NonIdentity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE NOT IsIdentityComponent;
```

#### DDL sketch (SQL Server)

```sql
CREATE TABLE dms.ReferenceEdge (
    ParentDocumentId bigint NOT NULL,
    ChildDocumentId  bigint NOT NULL,
    IsIdentityComponent bit NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_ReferenceEdge_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_ReferenceEdge PRIMARY KEY CLUSTERED (ParentDocumentId, ChildDocumentId),
    CONSTRAINT FK_ReferenceEdge_Parent FOREIGN KEY (ParentDocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT FK_ReferenceEdge_Child FOREIGN KEY (ChildDocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);

CREATE INDEX IX_ReferenceEdge_ChildDocumentId
    ON dms.ReferenceEdge (ChildDocumentId, IsIdentityComponent)
    INCLUDE (ParentDocumentId);

-- Optional filtered indexes (only if needed after measurement)
CREATE INDEX IX_ReferenceEdge_ChildDocumentId_Identity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE IsIdentityComponent = 1;

CREATE INDEX IX_ReferenceEdge_ChildDocumentId_NonIdentity
    ON dms.ReferenceEdge (ChildDocumentId)
    INCLUDE (ParentDocumentId)
    WHERE IsIdentityComponent = 0;
```

### Update tracking + Change Queries (write-time model; repurpose existing tables)

This alternative is a **write-time** representation-tracking model:

- `dms.Document.ContentVersion` is treated as the persisted **representation change stamp** (the `ChangeVersion` served by Change Queries and used for ETag derivation).
- `dms.Document.ContentLastModifiedAt` is treated as the persisted API `_lastModifiedDate` for the resource representation.
- `dms.Document.IdentityVersion` remains the **identity-projection change signal** (direct identity updates and identity cascades) and is the trigger point for the touch cascade.

Implications:

- Any write that changes a document’s identity projection (i.e., bumps `IdentityVersion`) MUST also bump that same document’s `ContentVersion/ContentLastModifiedAt`, because the representation changed.
- “Touch cascades” update **referrers’** `ContentVersion/ContentLastModifiedAt` so indirect representation changes appear as local stamps on the affected parents.

#### Change Query journal (reuse `dms.DocumentChangeEvent`)

Use **only** `dms.DocumentChangeEvent` to drive Change Query selection:

- A trigger on `dms.Document` inserts a `dms.DocumentChangeEvent` row whenever `ContentVersion` changes (including touch updates).
- `dms.DocumentChangeEvent.ChangeVersion` should equal the document’s new `ContentVersion` in this alternative.

#### Drop `dms.IdentityChangeEvent` completely

This alternative drops `dms.IdentityChangeEvent` entirely.

Rationale: `dms.IdentityChangeEvent` exists in the baseline (read-time derivation) design to expand indirect impacts via `dms.ReferenceEdge` at query time. In this write-time touch model, indirect impacts are already materialized by updating each impacted parent’s `ContentVersion`, so Change Query selection no longer needs an identity-change journal.

#### Deletes require a tombstone journal (not designed here)

Change Queries need a durable “delete marker” journal because `dms.DocumentChangeEvent` rows are tied to `dms.Document` rows and cannot represent deletions after the row is gone. This document notes the requirement but does not design the tombstone schema/API.

## Recompute/diff-maintained adjacency list

This section replaces the trigger+counts approach from earlier drafts with a recompute/diff approach that is identical in both PostgreSQL and SQL Server.

### Why recompute/diff (reduce churn under “replace” writes)

The baseline relational write strategy for collections is “replace” (delete existing child rows, then insert current rows). If edge maintenance is incremental (statement deltas), a replace will:

- decrement all edges (child-row deletes), often deleting the edge rows,
- then increment all edges again (child-row inserts), re-inserting edge rows,

even when the net reference set did not change. That produces high WAL/log volume and heavy `dms.ReferenceEdge` index churn.

Recompute/diff instead:

- reads the parent’s **final** FK graph once, and
- writes **only** net changes to `dms.ReferenceEdge` (often 0 rows on idempotent updates).

### Where the recompute happens (single call per document write)

Within the document write transaction, after all writes to the resource root table + child/extension tables have completed:

1. Persist relational rows (root + children + extensions).
2. Call `dms.RecomputeReferenceEdges(@ParentDocumentId)` once.
3. Commit.

If `dms.RecomputeReferenceEdges` fails, the whole transaction fails (no “best effort” edge window).

### Why SQL Server can’t do this as “pure triggers”

PostgreSQL supports deferrable constraint triggers that can run at transaction end, but **SQL Server does not**:

- SQL Server triggers run **per statement**, not at commit, and cannot be deferred until the end of the transaction.
- A DMS write spans **multiple statements across multiple tables** (root + many child tables). Any trigger-based recompute would:
  - run multiple times per write (edge churn), and/or
  - run before the final child-table state exists (incorrect intermediate edge sets), and/or
  - require complex “dirty queue + session state” signaling to detect “end of document write”, which SQL Server still cannot reliably hook to “transaction commit”.

Therefore, on SQL Server the recompute/diff must be invoked explicitly by the document write path (application code or a stored procedure that encapsulates the whole write). For cross-engine parity, this v2 alternative uses the same explicit call model on PostgreSQL.

### Projection rule (generated, per resource)

For every relational table `T` produced by the mapping/DDL generator:

- Identify its **parent document key** column (`ParentDocumentId`), which is the root document’s `DocumentId`.
  - For a resource root table, this is typically `DocumentId`.
  - For a child/collection table, this is the root `DocumentId` included as part of the composite parent key (per `flattening-reconstitution.md` conventions).
- Identify every **document reference FK column** in `T`:
  - columns of the form `..._DocumentId` that reference `dms.Document(DocumentId)` or a concrete resource table’s `DocumentId`.
  - exclude descriptor FKs (`..._DescriptorId`) by design.
- For each such column, the generator knows whether it is an **identity component site** (`IsIdentityComponent=true`) based on ApiSchema identity bindings.

The generator emits a compiled “edge projection query” for the resource that, given `@ParentDocumentId`, returns distinct `(ChildDocumentId, IsIdentityComponent)` pairs by:

- `UNION ALL` selecting each FK column as `ChildDocumentId` with a constant `IsIdentityComponent`,
- filtering to `ParentDocumentId = @ParentDocumentId` and `ChildDocumentId IS NOT NULL`,
- collapsing duplicates with `GROUP BY ChildDocumentId` and `bool_or`/`MAX` to compute the OR of `IsIdentityComponent`.

### Diff algorithm (conceptual)

Given `ParentDocumentId = P`:

1. Stage expected edges into a temp/staging table:
   - `expected(ParentDocumentId, ChildDocumentId, IsIdentityComponent)`
2. Apply the diff:
   - insert edges in `expected` that are missing in `dms.ReferenceEdge`
   - update `IsIdentityComponent` where it changed
   - delete edges in `dms.ReferenceEdge` for `P` that are not in `expected`

Avoid `MERGE` on SQL Server; use explicit `INSERT ... WHERE NOT EXISTS`, `UPDATE ... FROM`, `DELETE ... WHERE NOT EXISTS`.

#### PostgreSQL sketch (`dms.RecomputeReferenceEdges`)

```sql
CREATE OR REPLACE PROCEDURE dms.RecomputeReferenceEdges(parent_document_id bigint)
LANGUAGE plpgsql
AS $$
BEGIN
  CREATE TEMP TABLE reference_edge_expected (
      ParentDocumentId bigint NOT NULL,
      ChildDocumentId  bigint NOT NULL,
      IsIdentityComponent boolean NOT NULL,
      PRIMARY KEY (ParentDocumentId, ChildDocumentId)
  ) ON COMMIT DROP;

  -- Generated projection for this resource type (union across FK sites), collapsed by OR:
  INSERT INTO reference_edge_expected (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
  SELECT parent_document_id, ChildDocumentId, bool_or(IsIdentityComponent)
  FROM (
      -- UNION ALL branches generated per FK site:
      -- SELECT r.Program_DocumentId AS ChildDocumentId, false AS IsIdentityComponent
      -- FROM edfi.StudentSchoolAssociation r
      -- WHERE r.DocumentId = parent_document_id AND r.Program_DocumentId IS NOT NULL
      --
      -- UNION ALL
      -- SELECT r.Student_DocumentId, true
      -- FROM edfi.StudentSchoolAssociation r
      -- WHERE r.DocumentId = parent_document_id AND r.Student_DocumentId IS NOT NULL
  ) x
  GROUP BY ChildDocumentId;

  -- Insert missing
  INSERT INTO dms.ReferenceEdge (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
  SELECT e.ParentDocumentId, e.ChildDocumentId, e.IsIdentityComponent
  FROM reference_edge_expected e
  LEFT JOIN dms.ReferenceEdge re
    ON re.ParentDocumentId = e.ParentDocumentId
   AND re.ChildDocumentId  = e.ChildDocumentId
  WHERE re.ParentDocumentId IS NULL;

  -- Update changed
  UPDATE dms.ReferenceEdge re
  SET IsIdentityComponent = e.IsIdentityComponent
  FROM reference_edge_expected e
  WHERE re.ParentDocumentId = e.ParentDocumentId
    AND re.ChildDocumentId  = e.ChildDocumentId
    AND re.IsIdentityComponent IS DISTINCT FROM e.IsIdentityComponent;

  -- Delete stale
  DELETE FROM dms.ReferenceEdge re
  WHERE re.ParentDocumentId = parent_document_id
    AND NOT EXISTS (
      SELECT 1
      FROM reference_edge_expected e
      WHERE e.ParentDocumentId = re.ParentDocumentId
        AND e.ChildDocumentId  = re.ChildDocumentId
    );
END;
$$;
```

#### SQL Server sketch (`dms.RecomputeReferenceEdges`)

```sql
CREATE OR ALTER PROCEDURE dms.RecomputeReferenceEdges
    @ParentDocumentId bigint
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #expected (
        ParentDocumentId bigint NOT NULL,
        ChildDocumentId  bigint NOT NULL,
        IsIdentityComponent bit NOT NULL,
        CONSTRAINT PK_expected PRIMARY KEY (ParentDocumentId, ChildDocumentId)
    );

    -- Generated projection for this resource type (union across FK sites), collapsed by OR:
    INSERT INTO #expected (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
    SELECT @ParentDocumentId,
           x.ChildDocumentId,
           CAST(MAX(CAST(x.IsIdentityComponent AS int)) AS bit) AS IsIdentityComponent
    FROM (
        -- UNION ALL branches generated per FK site:
        -- SELECT r.Program_DocumentId AS ChildDocumentId, CAST(0 AS bit) AS IsIdentityComponent
        -- FROM edfi.StudentSchoolAssociation r
        -- WHERE r.DocumentId = @ParentDocumentId AND r.Program_DocumentId IS NOT NULL
        --
        -- UNION ALL
        -- SELECT r.Student_DocumentId, CAST(1 AS bit)
        -- FROM edfi.StudentSchoolAssociation r
        -- WHERE r.DocumentId = @ParentDocumentId AND r.Student_DocumentId IS NOT NULL
    ) x
    GROUP BY x.ChildDocumentId;

    -- Insert missing
    INSERT INTO dms.ReferenceEdge (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
    SELECT e.ParentDocumentId, e.ChildDocumentId, e.IsIdentityComponent
    FROM #expected e
    WHERE NOT EXISTS (
        SELECT 1
        FROM dms.ReferenceEdge re WITH (UPDLOCK, HOLDLOCK)
        WHERE re.ParentDocumentId = e.ParentDocumentId
          AND re.ChildDocumentId  = e.ChildDocumentId
    );

    -- Update changed
    UPDATE re
    SET IsIdentityComponent = e.IsIdentityComponent
    FROM dms.ReferenceEdge re
    JOIN #expected e
      ON e.ParentDocumentId = re.ParentDocumentId
     AND e.ChildDocumentId  = re.ChildDocumentId
    WHERE re.IsIdentityComponent <> e.IsIdentityComponent;

    -- Delete stale
    DELETE re
    FROM dms.ReferenceEdge re
    WHERE re.ParentDocumentId = @ParentDocumentId
      AND NOT EXISTS (
        SELECT 1
        FROM #expected e
        WHERE e.ParentDocumentId = re.ParentDocumentId
          AND e.ChildDocumentId  = re.ChildDocumentId
      );
END;
```

## Touch cascade without the union view

Touch cascades still trigger off the same event as in `alternatives/touch-cascade.md`:

- identity projection changes are signaled by `dms.Document.IdentityVersion` changes (from direct identity updates and/or identity cascades).

But referrer discovery uses `dms.ReferenceEdge`.

### No-double-touch rule (unchanged, simplified by `IsIdentityComponent`)

As described in `alternatives/natural-key-plus-touch-cascades.md`:

- identity-component referrers already get a local row update via natural-key propagation, and local stamping updates their representation metadata.

Therefore, the touch cascade targets only **non-identity** referrers:

```text
TouchTargets(child) =
  { Parent | ReferenceEdge(Parent, child) exists AND IsIdentityComponent=false }
```

### Trigger sketch on `dms.Document` (PostgreSQL)

ODS compatibility note:
- Use **per-row** `ContentVersion` allocation for touched parents (not “one stamp per statement/transaction”). This preserves the “single watermark” client contract used by ODS Change Queries (`minChangeVersion = last+1`) and avoids missing rows when many documents change in one cascade.
- Reduce contention by (a) ensuring touch targets are distinct (each parent is updated once per triggering statement), and (b) using a sequence cache sized for your write volume (gaps on restart are acceptable under ODS semantics).

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
      -- Allocate one version per touched parent (unique per row).
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

### SQL Server trigger sketch

```sql
-- ODS compatibility: allocate a unique change version per touched parent row.
--
-- Contention reduction: reserve a contiguous sequence range once per statement with sys.sp_sequence_get_range,
-- then assign first + row_number - 1 per parent (much less overhead than NEXT VALUE FOR per row).
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

-- Optional guardrail (recommended): abort if fan-in is too large for the configured instance.
-- IF @n > @MaxTouchedParents THROW 51000, 'Touch cascade too large; see operator guidance', 1;

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

## Interaction with identity-only natural-key propagation

This v2 alternative assumes the same identity-only propagation model as `alternatives/natural-key-plus-touch-cascades.md`:

- identity-component sites may materialize referenced identity values and use `ON UPDATE CASCADE` to propagate those values locally (or emulate via triggers where required).

Important coupling point:
- `dms.ReferenceEdge` recompute uses only **`..._DocumentId` FK columns** (the persisted relationship), not duplicated natural-key identity columns.
  - Natural-key propagation updates identity columns, not the `DocumentId` FKs, so the edge set is stable and should not churn from those cascades.

## Benefits vs a union view

- **Predictable reverse lookup**: `ChildDocumentId → ParentDocumentId` is a single indexed table scan, not a multi-branch union plan.
- **Bounded DDL**: no single global view that grows with every extension and FK site; edge projection queries are per resource type.
- **Lower edge churn**: recompute/diff writes only net changes to `dms.ReferenceEdge` (even under replace writes).

## Costs / risks

- **Read work on writes**: each write pays to project the parent’s FK graph (root + children). For very large documents, this can be non-trivial even when the net edge set doesn’t change.
- **Orchestration requirement (DB correctness boundary)**: `dms.ReferenceEdge` is only correct if all relational writes go through a path that calls `dms.RecomputeReferenceEdges` before commit (application or stored proc). Ad-hoc DML without the recompute is unsupported and requires rebuild tooling.
- **DDL generator complexity**: generator must emit per-resource edge projection SQL and the recompute routine(s) for both engines.
- **High-fan-in touch events remain the tail risk**: even with fast reverse lookup, touching millions of parents is still operationally dangerous (locks/log volume/deadlocks).
- **Correctness is critical**: if edge projection or recompute is wrong, touch cascades can miss parents (stale `_etag/_lastModifiedDate/ChangeVersion` for indirect identity changes).

## Required operational tooling

Even with recompute/diff:

1. **Backfill/rebuild**: a job/procedure to rebuild `dms.ReferenceEdge` from the relational FK graph (offline or targeted).
2. **Guardrails** (optional, strongly recommended): configurable limits (“max parents to touch per statement/transaction”) with a safe failure mode (abort with clear operator guidance).

   ### Optional guardrail: max parents to touch (default 5,000)

   Even if identity cascades are infrequent, *when they happen* they can be extreme “hub” events (a single changed child referenced by many parents). Because this design performs the touch in-transaction, an unbounded touch can create an operational incident:
   - long-running write transactions (timeouts, blocking),
   - lock contention/deadlocks (especially under concurrent writes),
   - large log/WAL volume and replication/HA pressure,
   - SQL Server lock escalation risk on large update sets,
   - and “retry storms” if the API layer retries deadlocked transactions.

   A simple, engine-agnostic guardrail is to cap the touch fan-in:

   - **`MaxTouchParents`**: maximum number of **distinct** `ParentDocumentId`s in `TouchTargets(...)` for a single write/transaction.
   - **Default**: `5_000`.

   **Why 5,000?**
   - It is already a very large synchronous fan-out for a single OLTP write; above this, the probability of unacceptable lock/log impact rises sharply.
   - It is high enough to avoid tripping on typical fan-in patterns (most updates touch 0 or a small number of parents) while still preventing “hub” updates from silently degrading the whole system.
   - It provides a clear operational boundary: if a change would touch >5k parents, treat it as an operator event (maintenance window and/or a temporarily raised limit) rather than a normal API write.

   **Failure mode (fail-closed)**
   - Before performing the `UPDATE dms.Document ... WHERE DocumentId IN (TouchTargets)`, compute `touch_count` (ideally short-circuiting at `MaxTouchParents + 1`).
   - If `touch_count > MaxTouchParents`, **raise an error and abort the transaction**.
   - The API maps this to a deterministic client error (e.g., `409 Conflict` or `422 Unprocessable Entity`) with a stable error code (e.g., `touchCascadeLimitExceeded`) and operator guidance (include `touch_count`, `MaxTouchParents`, and the triggering child `DocumentId`s).

   Notes:
   - This cap should be configurable per environment. A “maintenance override” mode can temporarily raise it, accepting the operational impact explicitly.
   - The cap should be on **distinct parents**, not edges/rows, because multiple FK sites/rows can collapse to one parent touch.
3. **Telemetry**: emit per-transaction metrics (number of identity changes, number of touched parents, time spent in touch trigger, deadlock retries).

## Open questions

1. **Stamping granularity**: resolved for strict ODS compatibility — touch assigns one `ContentVersion` per touched row (not one per triggering transaction/statement).
2. **When to recompute**: always recompute after every write (simplest), or only when the write plan touched any `..._DocumentId` columns (optimization; requires a reliable “did any FK change?” signal).
3. **Index strategy defaults**: do we require filtered/partial indexes by default, or only the general reverse index?
4. **SQL Server cascade feasibility**: if `ON UPDATE CASCADE` is constrained by “multiple cascade paths” rules, does the identity-only propagation still hold, or do we need trigger-based propagation (reintroducing ODS-like trigger complexity)?

## Recommended proof artifacts

- **Make invariants explicit (spec artifact):** Add a short “Correctness invariants” section that precisely defines what `ReferenceEdge` must equal (the FK projection for the parent), what “no-double-touch” means, and what should *never* happen (missing edges, touch on identity referrers).
- **Ship a rebuild + audit kit (operational SQL artifact):** Generate and version 4 routines for both Postgres and SQL Server: (1) full rebuild of `dms.ReferenceEdge` from FK sites, (2) targeted rebuild for one `ParentDocumentId` (scan only that resource’s root+child tables), (3) audit/verify for one `ParentDocumentId` (expected vs actual diffs), (4) audit sampling job (random parents per hour/day).
- **Add recompute contract tests (verification artifact):** A DB-backed test suite that runs multi-row `INSERT/UPDATE/DELETE` on root+child tables (including the replace pattern) and asserts `dms.ReferenceEdge` equals the FK projection and that touch targeting matches `IsIdentityComponent=false`. Run it against both engines as part of CI/provision smoke.
- **Add concurrency/deadlock proof (stress artifact):** A small stress harness that runs concurrent writers updating parents + concurrent identity updates, verifying: edge recompute is correct after retries, and touch time is bounded. Document the required retry policy and the expected deadlock surface.
- **Add a failure-mode runbook (ops artifact):** Clear operator steps for: guardrail exceeded, audit mismatch detected, rebuild procedure usage, expected lock/impact, and how to re-run safely.
- **Add telemetry + alert thresholds (proof-in-prod artifact):** Counters/histograms for “edge projection time”, “edges changed”, “touch targets”, “touch duration”, “guardrail aborts”, “audit mismatches”, and “rebuild invoked”, with alerting guidance.
