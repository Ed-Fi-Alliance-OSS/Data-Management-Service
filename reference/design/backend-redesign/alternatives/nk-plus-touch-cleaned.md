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
- Identity updates are operationally rare, and
- The system is willing to accept potentially large synchronous fan-out during those events.

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
2. Make identity updates and their dependent effects fully correct at commit time (no stale window).
3. Provide ODS-like “indirect update” semantics for representation metadata using write-time updates:
   - if a referenced identity changes, referrers’ `_etag/_lastModifiedDate/ChangeVersion` must change.
4. Avoid “double touch”: documents updated locally by identity-only propagation should not also be touched.
5. Remain feasible on PostgreSQL and SQL Server with the same logical algorithms (engine-specific SQL where necessary).

## Non-goals

- Define the entire read path and query semantics (use baseline documents for flattening/reconstitution and query execution).

## Core concepts

### 1) Two dependency types

For a parent document that references a child document :

1. **Identity component reference**
   - Child’s projected identity values participate in Parent’s identity (`identityJsonPaths`).
   - If those projected values change, Parent’s `ReferentialId` must change.

2. **Non-identity representation dependency**
   - Child’s projected identity values appear in Parent’s API representation (as a `{...}Reference` object), but do not participate in Parent’s identity.
   - If they change, Parent’s representation metadata must change, but Parent’s `ReferentialId` does not.

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

#### Write-path compatibility (flattening + minimal round-trips)

Including these propagated natural-key columns is compatible with the baseline “flatten then insert” write path as long as the incoming API payload continues to carry the reference identity fields (normal Ed-Fi `{...}Reference` objects).

- The flattener can set both:
  - `<RefName>_DocumentId` (from the existing bulk `ReferentialId → DocumentId` resolution), and
  - the propagated identity columns (directly from the request JSON at the reference-object paths),
  without additional database reads.
- If DMS ever accepts “DocumentId-only” or “link-only” references for identity-component sites (i.e., the natural-key fields are absent), these propagated columns cannot be populated without an additional database lookup and would break the “almost no round-trips” property. This alternative should either fail the request for that shape or explicitly accept write-path lookups to backfill the missing identity values.

#### Column naming convention (avoid collisions)

This alternative must extend the baseline naming rules in `reference/design/backend-redesign/data-model.md` (Naming Rules → Column names) for the new propagated identity columns.

- Naming rule: for each propagated identity value sourced from a reference object, use:
  - `{ReferenceBaseName}_{IdentityFieldBaseName}` (PascalCase components; underscore separator to match existing `{ReferenceBaseName}_DocumentId` convention).
  - Example: `Student_DocumentId` + `Student_StudentUniqueId`; `School_DocumentId` + `School_SchoolId`.
- Rationale: avoids collisions with:
  - local scalar fields (e.g., a resource may already have `SchoolId`),
  - multiple references that share identity field names (`EducationOrganizationId`, etc.), and
  - name shortening/truncation effects on long paths.
- Use `resourceSchema.relational.nameOverrides` on full JSON paths (e.g., `$.studentReference.studentUniqueId`) to resolve any remaining collisions, and apply the baseline truncation+hash rule when dialect identifier limits are exceeded.

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

#### Example: abstract identity table + maintenance triggers (`EducationOrganization`)

This example shows one possible shape for an abstract identity table, and one concrete member (`School`) maintaining it.

**PostgreSQL (sketch)**

```sql
CREATE TABLE edfi.EducationOrganizationIdentity (
    DocumentId bigint NOT NULL PRIMARY KEY
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    EducationOrganizationId integer NOT NULL,
    Discriminator varchar(256) NOT NULL,
    CONSTRAINT UX_EdOrgIdentity_DocumentId_EdOrgId UNIQUE (DocumentId, EducationOrganizationId),
    CONSTRAINT UX_EdOrgIdentity_EdOrgId UNIQUE (EducationOrganizationId)
);

CREATE OR REPLACE FUNCTION edfi.trg_school_edorg_identity_upsert()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO edfi.EducationOrganizationIdentity (DocumentId, EducationOrganizationId, Discriminator)
  VALUES (NEW.DocumentId, NEW.SchoolId, 'School')
  ON CONFLICT (DocumentId) DO UPDATE
    SET EducationOrganizationId = EXCLUDED.EducationOrganizationId,
        Discriminator = EXCLUDED.Discriminator;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER TR_School_EdOrgIdentity_Upsert
AFTER INSERT OR UPDATE OF SchoolId ON edfi.School
FOR EACH ROW
EXECUTE FUNCTION edfi.trg_school_edorg_identity_upsert();
```

**SQL Server (sketch)**

```sql
CREATE TABLE edfi.EducationOrganizationIdentity (
    DocumentId bigint NOT NULL,
    EducationOrganizationId int NOT NULL,
    Discriminator nvarchar(256) NOT NULL,
    CONSTRAINT PK_EdOrgIdentity PRIMARY KEY CLUSTERED (DocumentId),
    CONSTRAINT FK_EdOrgIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_EdOrgIdentity_DocumentId_EdOrgId UNIQUE (DocumentId, EducationOrganizationId),
    CONSTRAINT UX_EdOrgIdentity_EdOrgId UNIQUE (EducationOrganizationId)
);
GO

CREATE OR ALTER TRIGGER edfi.TR_School_EdOrgIdentity_Upsert
ON edfi.School
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Update existing rows
    UPDATE t
    SET
        t.EducationOrganizationId = i.SchoolId,
        t.Discriminator = N'School'
    FROM edfi.EducationOrganizationIdentity t
    JOIN inserted i ON i.DocumentId = t.DocumentId;

    -- Insert missing rows
    INSERT INTO edfi.EducationOrganizationIdentity (DocumentId, EducationOrganizationId, Discriminator)
    SELECT i.DocumentId, i.SchoolId, N'School'
    FROM inserted i
    WHERE NOT EXISTS (
        SELECT 1 FROM edfi.EducationOrganizationIdentity t WHERE t.DocumentId = i.DocumentId
    );
END;
GO
```

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

Change Query journaling can still use `dms.DocumentChangeEvent` as in the baseline redesign, emitted by triggers on `dms.Document`. Because touch cascades materialize indirect impacts as local `ContentVersion` updates on the touched parents, those touched rows naturally appear in the change journal.

#### Example: `dms.DocumentChangeEvent` journal triggers (on `dms.Document`)

This example shows journal emission that records one `dms.DocumentChangeEvent` row whenever a document’s served representation stamp changes (local write, identity change, or touch).

**PostgreSQL (sketch)**

```sql
CREATE OR REPLACE FUNCTION dms.trg_document_change_event_ins()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
  SELECT ContentVersion, DocumentId, ResourceKeyId
  FROM inserted;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION dms.trg_document_change_event_upd()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
  SELECT i.ContentVersion, i.DocumentId, i.ResourceKeyId
  FROM inserted i
  JOIN deleted d ON d.DocumentId = i.DocumentId
  WHERE i.ContentVersion <> d.ContentVersion;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS TR_DocumentChangeEvent_Insert ON dms.Document;
CREATE TRIGGER TR_DocumentChangeEvent_Insert
AFTER INSERT ON dms.Document
REFERENCING NEW TABLE AS inserted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_document_change_event_ins();

DROP TRIGGER IF EXISTS TR_DocumentChangeEvent_Update ON dms.Document;
CREATE TRIGGER TR_DocumentChangeEvent_Update
AFTER UPDATE ON dms.Document
REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_document_change_event_upd();
```

**SQL Server (sketch)**

```sql
CREATE OR ALTER TRIGGER dms.TR_DocumentChangeEvent
ON dms.Document
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
    SELECT i.ContentVersion, i.DocumentId, i.ResourceKeyId
    FROM inserted i
    LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
    WHERE d.DocumentId IS NULL
       OR i.ContentVersion <> d.ContentVersion;
END;
```

### E) `dms.ReferenceEdge` (persisted reverse index; touch targeting + diagnostics)

Keep a single adjacency list table:

- one row per `(ParentDocumentId, ChildDocumentId)`
- `IsIdentityComponent` is the OR across all reference sites from the parent to the child

This table is used for:
- touch cascade targeting (`ChildDocumentId → ParentDocumentId`) for **non-identity** referrers
- diagnostics (“who references me?”)
- operational audit/rebuild targeting

### Baseline tables that can be removed

This alternative changes the baseline cascade and update-tracking mechanisms such that some baseline design core tables are no longer required:

- `dms.IdentityLock` (baseline: `reference/design/backend-redesign/transactions-and-concurrency.md`)
  - Baseline purpose: phantom-safe identity-closure locking for application-managed `dms.ReferentialIdentity` recompute and dependency-stable read-time algorithms.
  - Alternative: identity correctness is maintained by DB cascade propagation + row-local triggers, and optimistic concurrency uses stored representation metadata (no dependency-token reads/locks). There is no application-managed identity-closure traversal to lock, so `dms.IdentityLock` is redundant.

- `dms.IdentityChangeEvent` (baseline: `reference/design/backend-redesign/update-tracking.md` / `reference/design/backend-redesign/data-model.md`)
  - Baseline purpose: expand indirectly impacted parents during Change Query selection (child identity changes + `dms.ReferenceEdge` reverse lookups).
  - Alternative: indirect impacts are materialized immediately by the touch cascade as local `dms.Document.ContentVersion` updates on the parents, so Change Query selection does not need a separate “identity changed” journal.

`dms.DocumentChangeEvent` is not strictly required, but is still recommended:
- If you keep it, Change Query window scans remain narrow and index-friendly (same motivation as baseline).
- If you drop it, Change Query selection must instead query/index `dms.Document` directly on `(ResourceKeyId, ContentVersion)` (or the chosen representation stamp), which can add hot, high-churn indexing pressure to `dms.Document`.

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
2. Apply a diff to `dms.ReferenceEdge` scoped to Parent:
   - insert missing
   - update `IsIdentityComponent` changes
   - delete stale

Avoid `MERGE` on SQL Server; use explicit `INSERT ... WHERE NOT EXISTS`, `UPDATE ... FROM`, `DELETE ... WHERE NOT EXISTS`.

### Example: per-resource edge projection query (StudentSchoolAssociation)

This is an illustrative “edge projection query” for a single resource type, generated from the derived relational model and ApiSchema reference bindings.

**PostgreSQL (sketch)**

```sql
-- Input: parent_document_id bigint
SELECT ChildDocumentId, bool_or(IsIdentityComponent) AS IsIdentityComponent
FROM (
    -- Root table FK sites
    SELECT r.Student_DocumentId AS ChildDocumentId, true  AS IsIdentityComponent
    FROM edfi.StudentSchoolAssociation r
    WHERE r.DocumentId = parent_document_id AND r.Student_DocumentId IS NOT NULL

    UNION ALL
    SELECT r.School_DocumentId, true
    FROM edfi.StudentSchoolAssociation r
    WHERE r.DocumentId = parent_document_id AND r.School_DocumentId IS NOT NULL

    -- Child table FK sites
    UNION ALL
    SELECT c.Program_DocumentId, false
    FROM edfi.StudentSchoolAssociationProgramParticipation c
    WHERE c.StudentSchoolAssociation_DocumentId = parent_document_id AND c.Program_DocumentId IS NOT NULL
) x
GROUP BY ChildDocumentId;
```

**SQL Server (sketch)**

```sql
-- Input: @ParentDocumentId bigint
SELECT x.ChildDocumentId,
       CAST(MAX(CAST(x.IsIdentityComponent AS int)) AS bit) AS IsIdentityComponent
FROM (
    SELECT r.Student_DocumentId AS ChildDocumentId, CAST(1 AS bit) AS IsIdentityComponent
    FROM edfi.StudentSchoolAssociation r
    WHERE r.DocumentId = @ParentDocumentId AND r.Student_DocumentId IS NOT NULL

    UNION ALL
    SELECT r.School_DocumentId, CAST(1 AS bit)
    FROM edfi.StudentSchoolAssociation r
    WHERE r.DocumentId = @ParentDocumentId AND r.School_DocumentId IS NOT NULL

    UNION ALL
    SELECT c.Program_DocumentId, CAST(0 AS bit)
    FROM edfi.StudentSchoolAssociationProgramParticipation c
    WHERE c.StudentSchoolAssociation_DocumentId = @ParentDocumentId AND c.Program_DocumentId IS NOT NULL
) x
GROUP BY x.ChildDocumentId;
```

### Example: `dms.RecomputeReferenceEdges` (generated routine)

In practice, the DDL generator will emit a resource-specific body (or a dispatcher that selects the resource-specific projection) so the routine can project the FK graph for the resource being written.

**PostgreSQL (sketch)**

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

  -- Example projection for StudentSchoolAssociation (see above), collapsed by OR:
  INSERT INTO reference_edge_expected (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
  SELECT parent_document_id, ChildDocumentId, bool_or(IsIdentityComponent)
  FROM (
      SELECT r.Student_DocumentId AS ChildDocumentId, true AS IsIdentityComponent
      FROM edfi.StudentSchoolAssociation r
      WHERE r.DocumentId = parent_document_id AND r.Student_DocumentId IS NOT NULL

      UNION ALL
      SELECT r.School_DocumentId, true
      FROM edfi.StudentSchoolAssociation r
      WHERE r.DocumentId = parent_document_id AND r.School_DocumentId IS NOT NULL

      UNION ALL
      SELECT c.Program_DocumentId, false
      FROM edfi.StudentSchoolAssociationProgramParticipation c
      WHERE c.StudentSchoolAssociation_DocumentId = parent_document_id AND c.Program_DocumentId IS NOT NULL
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

**SQL Server (sketch)**

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

    -- Example projection for StudentSchoolAssociation (see above), collapsed by OR:
    INSERT INTO #expected (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
    SELECT @ParentDocumentId,
           x.ChildDocumentId,
           CAST(MAX(CAST(x.IsIdentityComponent AS int)) AS bit) AS IsIdentityComponent
    FROM (
        SELECT r.Student_DocumentId AS ChildDocumentId, CAST(1 AS bit) AS IsIdentityComponent
        FROM edfi.StudentSchoolAssociation r
        WHERE r.DocumentId = @ParentDocumentId AND r.Student_DocumentId IS NOT NULL

        UNION ALL
        SELECT r.School_DocumentId, CAST(1 AS bit)
        FROM edfi.StudentSchoolAssociation r
        WHERE r.DocumentId = @ParentDocumentId AND r.School_DocumentId IS NOT NULL

        UNION ALL
        SELECT c.Program_DocumentId, CAST(0 AS bit)
        FROM edfi.StudentSchoolAssociationProgramParticipation c
        WHERE c.StudentSchoolAssociation_DocumentId = @ParentDocumentId AND c.Program_DocumentId IS NOT NULL
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

#### Example: per-resource referential-identity + version-stamping trigger (StudentSchoolAssociation)

This example shows a per-resource trigger recomputing and upserting `dms.ReferentialIdentity` from locally present identity columns (including propagated identity-component values) and stamping `dms.Document.IdentityVersion`/`ContentVersion` when the identity projection changes.

Notes:
- The DDL generator inlines the correct `ResourceKeyId` for the table and the ordered identity concatenation for that resource’s `identityJsonPaths`.
- The UUIDv5 helper is assumed to exist (example name: `dms.uuid_v5(namespace_uuid, name_text)` / `dms.fn_uuid_v5(...)`).

**PostgreSQL (sketch)**

```sql
CREATE OR REPLACE FUNCTION edfi.trg_ssa_referential_identity_upsert()
RETURNS trigger AS
$$
DECLARE
  resource_key_id smallint := 123; -- generated constant for edfi.StudentSchoolAssociation
  namespace_uuid uuid := '00000000-0000-0000-0000-000000000000'; -- generated constant (UUIDv5 namespace)
  resource_info text := 'EdFiStudentSchoolAssociation'; -- generated canonical resource info string
  identity_text text;
  referential_id uuid;
  stamp bigint;
BEGIN
  -- Avoid stamping on no-op updates.
  IF TG_OP = 'UPDATE'
     AND NEW.Student_StudentUniqueId IS NOT DISTINCT FROM OLD.Student_StudentUniqueId
     AND NEW.School_SchoolId        IS NOT DISTINCT FROM OLD.School_SchoolId
     AND NEW.EntryDate              IS NOT DISTINCT FROM OLD.EntryDate
  THEN
    RETURN NEW;
  END IF;

  -- Ordered identity concatenation (generated for identityJsonPaths)
  identity_text :=
      '$.studentReference.studentUniqueId=' || NEW.Student_StudentUniqueId
   || '#$.schoolReference.schoolId='        || NEW.School_SchoolId
   || '#$.entryDate='                      || to_char(NEW.EntryDate, 'YYYY-MM-DD');

  referential_id := dms.uuid_v5(namespace_uuid, resource_info || identity_text);

  -- Upsert by (DocumentId, ResourceKeyId); update sets the new ReferentialId (PK update).
  INSERT INTO dms.ReferentialIdentity (ReferentialId, DocumentId, ResourceKeyId)
  VALUES (referential_id, NEW.DocumentId, resource_key_id)
  ON CONFLICT (DocumentId, ResourceKeyId) DO UPDATE
    SET ReferentialId = EXCLUDED.ReferentialId;

  -- Stamp identity + served representation metadata.
  -- Use one stamp value for both IdentityVersion and ContentVersion.
  stamp := nextval('dms.ChangeVersionSequence');
  UPDATE dms.Document d
  SET
      IdentityVersion = stamp,
      IdentityLastModifiedAt = now(),
      ContentVersion = stamp,
      ContentLastModifiedAt = now()
  WHERE d.DocumentId = NEW.DocumentId;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER TR_SSA_ReferentialIdentity_Upsert
AFTER INSERT OR UPDATE OF Student_StudentUniqueId, School_SchoolId, EntryDate
ON edfi.StudentSchoolAssociation
FOR EACH ROW
EXECUTE FUNCTION edfi.trg_ssa_referential_identity_upsert();
```

**SQL Server (sketch)**

```sql
CREATE OR ALTER TRIGGER edfi.TR_SSA_ReferentialIdentity_Upsert
ON edfi.StudentSchoolAssociation
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ResourceKeyId smallint = 123; -- generated constant for edfi.StudentSchoolAssociation
    DECLARE @Namespace uniqueidentifier = '00000000-0000-0000-0000-000000000000'; -- generated UUIDv5 namespace
    DECLARE @ResourceInfo nvarchar(256) = N'EdFiStudentSchoolAssociation'; -- generated canonical resource info string

    ;WITH changed AS (
        SELECT i.DocumentId,
               i.Student_StudentUniqueId,
               i.School_SchoolId,
               i.EntryDate
        FROM inserted i
        LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
        WHERE d.DocumentId IS NULL
           OR i.Student_StudentUniqueId <> d.Student_StudentUniqueId
           OR i.School_SchoolId        <> d.School_SchoolId
           OR i.EntryDate              <> d.EntryDate
    ),
    computed AS (
        SELECT
            c.DocumentId,
            dms.fn_uuid_v5(
                @Namespace,
                @ResourceInfo
                + N'$.studentReference.studentUniqueId=' + c.Student_StudentUniqueId
                + N'#$.schoolReference.schoolId='        + CAST(c.School_SchoolId AS nvarchar(32))
                + N'#$.entryDate='                      + CONVERT(nvarchar(10), c.EntryDate, 23)
            ) AS ReferentialId
        FROM changed c
    )
    -- Update existing
    UPDATE ri
    SET ri.ReferentialId = c.ReferentialId
    FROM dms.ReferentialIdentity ri
    JOIN computed c
      ON c.DocumentId = ri.DocumentId
     AND ri.ResourceKeyId = @ResourceKeyId;

    -- Insert missing
    INSERT INTO dms.ReferentialIdentity (ReferentialId, DocumentId, ResourceKeyId)
    SELECT c.ReferentialId, c.DocumentId, @ResourceKeyId
    FROM computed c
    WHERE NOT EXISTS (
        SELECT 1
        FROM dms.ReferentialIdentity ri
        WHERE ri.DocumentId = c.DocumentId
          AND ri.ResourceKeyId = @ResourceKeyId
    );

    -- Stamp identity + served representation metadata.
    UPDATE d
    SET
        d.IdentityVersion = s.Stamp,
        d.IdentityLastModifiedAt = sysutcdatetime(),
        d.ContentVersion = s.Stamp,
        d.ContentLastModifiedAt = sysutcdatetime()
    FROM dms.Document d
    JOIN computed c ON c.DocumentId = d.DocumentId
    CROSS APPLY (SELECT NEXT VALUE FOR dms.ChangeVersionSequence AS Stamp) s;
END;
GO
```

### IdentityVersion stamping

When a document’s identity projection changes (direct or cascaded), triggers must:
- update `dms.Document.IdentityVersion` (monotonic stamp from `dms.ChangeVersionSequence`)
- update `dms.Document.IdentityLastModifiedAt`
- also update `dms.Document.ContentVersion/ContentLastModifiedAt` because the representation changed

Touch cascades are triggered by the `IdentityVersion` change.

## Concurrency and operational hardening

This alternative makes cascades synchronous and write-amplifying, so it must have an explicit “bounded failure” story.

### Locking model (why no special lock table)

This alternative does **not** use a baseline-style “special” lock protocol (e.g., `dms.IdentityLock`) because it removes the baseline’s main reason for it: **application-managed impacted-set discovery** (identity-closure traversal / dependency scans) that must be made phantom-safe.

Instead:

- **Identity correctness is enforced by the database** via composite foreign keys and `ON UPDATE CASCADE` for identity-component sites. The database must take whatever locks are required on the referenced key and dependent rows to maintain referential integrity and apply the cascade. This prevents “missed dependents” without needing application-managed closure locking.
- **Derived artifacts are updated in the same transaction** via normal `UPDATE/INSERT/DELETE` statements in triggers/procedures:
  - `dms.ReferentialIdentity` upserts take locks on the affected index entries/rows.
  - `dms.Document` version stamps (`IdentityVersion`/`ContentVersion`) take row locks on the stamped document row(s).
  - `dms.ReferenceEdge` recompute/diff takes row/key locks on the affected edge rows for the parent document.

So locking still exists, but it is the database’s **standard row/key locking** implied by:
- the cascaded FK updates,
- the trigger/procedure statements, and
- normal unique/foreign-key enforcement.

This does **not** eliminate deadlocks or contention risk. It means this design relies on the database to provide correctness under concurrency, while the system still needs operational hardening (ordering, timeouts, retries, and guardrails) to handle worst-case fan-out.

### Database isolation defaults

- SQL Server: strongly recommend MVCC reads (`READ_COMMITTED_SNAPSHOT ON`, optionally `ALLOW_SNAPSHOT_ISOLATION ON`) to reduce deadlocks and blocking caused by readers during concurrent writes.
  - Why: without MVCC, `READ COMMITTED` reads take `S` locks. This design includes trigger/procedure paths that *read* `dms.ReferenceEdge` (touch targeting) and resource tables (edge projection), while other transactions *write* those same structures. `S` locks on reads materially increase the deadlock surface area (“Tx A reads edges then updates documents; Tx B updates documents then writes edges”).
  - With `READ_COMMITTED_SNAPSHOT`, those reads use row versioning and generally do not take `S` locks, reducing deadlocks and improving concurrency for touch cascades and edge recompute.
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
