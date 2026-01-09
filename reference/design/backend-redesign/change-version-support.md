# ChangeVersion Support (Option 1: Change Journals + Read-Time Derivation)

## Status

Draft. This document describes “Option 1” for supporting Ed-Fi-style Change Queries in DMS when `ChangeVersion` is derived (not stored) as described in:

- `reference/design/backend-redesign/DERIVED-TOKEN.md`

The goal is to avoid ODS-style write-time fan-out (and the SERIALIZABLE edge-scan behavior it drives) while still returning correct `ChangeVersion` values and supporting efficient “what changed since X?” queries.

## Goals and non-goals

### Goals

1. **ODS-compatible semantics (externally visible)**:
   - `ChangeVersion` changes when the resource representation changes (including indirect identity/descriptor URI changes).
2. **No write-time fan-out for indirect changes**:
   - avoid “update all referencing aggregate roots” on identity/descriptor changes.
3. **No SERIALIZABLE dependency scans**:
   - keep the derived-token locking posture (strict identity correctness remains, but representation metadata should not require SERIALIZABLE edge scans).
4. **Cross-engine**:
   - PostgreSQL and SQL Server.
5. **Efficient change queries**:
   - queries should scan by `ChangeVersion` ranges and be pageable deterministically.

### Non-goals

- **As-of snapshots**: Change Queries are not “read the database as it was at maxChangeVersion”. They return current representations whose derived `ChangeVersion` falls in the requested window (matching ODS behavior).
- **Lossless event sourcing**: This is not intended to return every intermediate change when a document changes multiple times in a window.

## Recap: derived `ChangeVersion`

Derived-token mapping used by this design:

```text
LocalChangeVersion(P) = max(P.ContentVersion, P.IdentityVersion)

ChangeVersion(P) = max(
  LocalChangeVersion(P),
  max(for each dependency D: D.IdentityVersion)
)
```

Key implication:

> If `ChangeVersion(P)` is in `[min,max]`, at least one contributor stamp is in `[min,max]` (because the derived value is a `max()` of contributor stamps).

This lets change queries build a *candidate set* from “contributors in the window”, then compute + filter the derived `ChangeVersion` only for those candidates.

## Option 1: append-only change journals

Option 1 adds small, append-only “journals” that let the read-side find contributors in a `[min,max]` window without scanning large base tables or relying on complex predicates.

This does **not** store derived `ChangeVersion(P)` for all documents (which would reintroduce write-time fan-out). It stores only the “contributor facts” that already exist at write time:

- “Document `P` had a local representation change at change version `v`”
- “Document `D` had an identity/URI change at change version `v`”

The read-side then uses `dms.ReferenceEdge` to expand identity changes into impacted parents at query time.

### Data model

The DDL here is illustrative. Column sizes should match the existing `dms.Document` schema.

#### 1) Global change version sequence

**PostgreSQL**

```sql
CREATE SEQUENCE dms.ChangeVersionSequence AS bigint START WITH 1 INCREMENT BY 1;
```

**SQL Server**

```sql
CREATE SEQUENCE dms.ChangeVersionSequence
    AS bigint
    START WITH 1
    INCREMENT BY 1;
```

#### 2) Local token stamps (on `dms.Document`)

Assumes the token columns from `reference/design/backend-redesign/DERIVED-TOKEN.md` exist:

- `ContentVersion bigint` (global stamp)
- `IdentityVersion bigint` (global stamp)
- `ContentLastModifiedAt`, `IdentityLastModifiedAt`

#### 3) `dms.DocumentChangeEvent` (local changes)

This journal records local changes that can affect the document’s own representation (content and/or identity projection).

**PostgreSQL**

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentChangeEvent PRIMARY KEY (ChangeVersion, DocumentId)
);

-- Efficient resource-window scans
CREATE INDEX IX_DocumentChangeEvent_Resource_ChangeVersion
    ON dms.DocumentChangeEvent (ProjectName, ResourceName, ResourceVersion, ChangeVersion, DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL,
    ProjectName nvarchar(256) NOT NULL,
    ResourceName nvarchar(256) NOT NULL,
    ResourceVersion nvarchar(64) NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_DocumentChangeEvent_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_DocumentChangeEvent PRIMARY KEY CLUSTERED (ChangeVersion, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);

CREATE INDEX IX_DocumentChangeEvent_Resource_ChangeVersion
    ON dms.DocumentChangeEvent (ProjectName, ResourceName, ResourceVersion, ChangeVersion, DocumentId);
```

#### 4) `dms.IdentityChangeEvent` (identity / descriptor URI changes)

This journal records identity-projection changes (including descriptor URI changes). It is used to find *indirectly impacted* documents at read time via `dms.ReferenceEdge`.

**PostgreSQL**

```sql
CREATE TABLE dms.IdentityChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_IdentityChangeEvent PRIMARY KEY (ChangeVersion, DocumentId)
);

CREATE INDEX IX_IdentityChangeEvent_ChangeVersion
    ON dms.IdentityChangeEvent (ChangeVersion, DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.IdentityChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_IdentityChangeEvent_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_IdentityChangeEvent PRIMARY KEY CLUSTERED (ChangeVersion, DocumentId),
    CONSTRAINT FK_IdentityChangeEvent_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);

CREATE INDEX IX_IdentityChangeEvent_ChangeVersion
    ON dms.IdentityChangeEvent (ChangeVersion, DocumentId);
```

## Write-side behavior

### Stamping rule (recommended)

Allocate **one** stamp per document update operation and apply it to the relevant token columns:

- If local content changed: set `ContentVersion = @stamp`, `ContentLastModifiedAt = now()`.
- If identity projection changed: set `IdentityVersion = @stamp`, `IdentityLastModifiedAt = now()`.
- If both changed, reuse the same `@stamp` for both.

This avoids “double bumping” on an identity-changing update while preserving semantics.

Best-effort minimization: do not allocate a new stamp (and do not insert journal rows) for no-op writes where neither persisted content nor identity projection changes.

### Journal insertion rules

Within the same transaction that updates `dms.Document`:

- If `ContentVersion` or `IdentityVersion` changed:
  - insert one row into `dms.DocumentChangeEvent(ChangeVersion=@stamp, DocumentId=..., resource key...)`.
- If `IdentityVersion` changed:
  - insert one row into `dms.IdentityChangeEvent(ChangeVersion=@stamp, DocumentId=...)`.

Identity-closure recompute (strict correctness) already iterates impacted documents. For each document whose identity projection actually changes, apply the same `IdentityVersion` bump rule and insert an `IdentityChangeEvent` row.

### PostgreSQL: optional trigger to enforce journal writes

If DMS prefers to enforce journaling in the database (rather than in application code), a single trigger on `dms.Document` can do it.

```sql
CREATE OR REPLACE FUNCTION dms.trg_document_change_events()
RETURNS trigger AS
$$
DECLARE
  localChangeVersion bigint;
BEGIN
  -- INSERT
  IF (TG_OP = 'INSERT') THEN
    localChangeVersion := GREATEST(NEW.ContentVersion, NEW.IdentityVersion);

    INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ProjectName, ResourceName, ResourceVersion)
    VALUES (localChangeVersion, NEW.DocumentId, NEW.ProjectName, NEW.ResourceName, NEW.ResourceVersion);

    INSERT INTO dms.IdentityChangeEvent(ChangeVersion, DocumentId)
    VALUES (NEW.IdentityVersion, NEW.DocumentId);

    RETURN NEW;
  END IF;

  -- UPDATE
  IF (TG_OP = 'UPDATE') THEN
    IF (NEW.ContentVersion <> OLD.ContentVersion OR NEW.IdentityVersion <> OLD.IdentityVersion) THEN
      localChangeVersion := GREATEST(NEW.ContentVersion, NEW.IdentityVersion);

      INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ProjectName, ResourceName, ResourceVersion)
      VALUES (localChangeVersion, NEW.DocumentId, NEW.ProjectName, NEW.ResourceName, NEW.ResourceVersion);
    END IF;

    IF (NEW.IdentityVersion <> OLD.IdentityVersion) THEN
      INSERT INTO dms.IdentityChangeEvent(ChangeVersion, DocumentId)
      VALUES (NEW.IdentityVersion, NEW.DocumentId);
    END IF;
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER TR_Document_ChangeEvents
AFTER INSERT OR UPDATE ON dms.Document
FOR EACH ROW
EXECUTE FUNCTION dms.trg_document_change_events();
```

### SQL Server: optional trigger to enforce journal writes

```sql
CREATE OR ALTER TRIGGER dms.TR_Document_ChangeEvents
ON dms.Document
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Local changes (content and/or identity): insert one row per affected document
    INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ProjectName, ResourceName, ResourceVersion)
    SELECT
        CASE WHEN i.ContentVersion > i.IdentityVersion THEN i.ContentVersion ELSE i.IdentityVersion END AS ChangeVersion,
        i.DocumentId,
        i.ProjectName,
        i.ResourceName,
        i.ResourceVersion
    FROM inserted i
    LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
    WHERE d.DocumentId IS NULL
       OR i.ContentVersion <> d.ContentVersion
       OR i.IdentityVersion <> d.IdentityVersion;

    -- Identity changes only
    INSERT INTO dms.IdentityChangeEvent(ChangeVersion, DocumentId)
    SELECT
        i.IdentityVersion AS ChangeVersion,
        i.DocumentId
    FROM inserted i
    LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
    WHERE d.DocumentId IS NULL
       OR i.IdentityVersion <> d.IdentityVersion;
END;
```

## Read-side behavior (change queries)

This section focuses on the change-query selection problem: “Which documents should be returned for resource `R` and window `[min,max]`?”

### Why `ChangeVersion` adds complexity vs `_etag/_lastModifiedDate`

Under derived tokens:

- `_etag/_lastModifiedDate` are computed **only for returned documents** (e.g., “GET by id”, or a query page).
- `ChangeVersion` must support “what changed since X?”, which requires **set selection and paging** by `ChangeVersion`.

So the extra complexity is not that `ChangeVersion` is harder to compute per document (it isn’t), but that **filtering and paging** require a query that can efficiently:

1. find candidates in the requested window, and
2. compute the derived value to verify membership in the window.

The journals make step (1) efficient.

### `availableChangeVersions`

At minimum (like ODS), DMS can return the “newest” change version from the global sequence:

**PostgreSQL**

```sql
SELECT last_value AS NewestChangeVersion
FROM dms.ChangeVersionSequence;
```

**SQL Server**

```sql
SELECT CAST(current_value AS bigint) AS NewestChangeVersion
FROM sys.sequences
WHERE name = 'ChangeVersionSequence' AND SCHEMA_NAME(schema_id) = 'dms';
```

Optionally, DMS can compute `OldestChangeVersion` as the minimum retained journal entry across:

- `dms.DocumentChangeEvent`
- `dms.IdentityChangeEvent`
- tracked deletes / key changes tables (future)

### Resource change query algorithm (high level)

Given resource key `R = (ProjectName, ResourceName, ResourceVersion)` and window `[min,max]`:

1. **Direct contributor scan**:
   - from `dms.DocumentChangeEvent` for `R` in `[min,max]`, collect `DocumentId`s.
2. **Indirect contributor scan**:
   - from `dms.IdentityChangeEvent` in `[min,max]`, collect changed dependency `DocumentId`s.
   - expand to parents via `dms.ReferenceEdge(ChildDocumentId → ParentDocumentId)` using **all edges** (representation dependencies are not limited to `IsIdentityComponent=true`).
   - filter parents to resource `R`.
3. **Candidate union**:
   - union distinct of (direct + indirect parents).
4. **Derived `ChangeVersion` compute for candidates**:
   - `LocalChangeVersion = max(ContentVersion, IdentityVersion)`
   - `MaxDepIdentityVersion = max(child.IdentityVersion)` over all outbound dependencies (via `dms.ReferenceEdge`)
   - `ChangeVersion = max(LocalChangeVersion, MaxDepIdentityVersion)`
5. **Filter + page**:
   - filter computed `ChangeVersion` to `[min,max]`
   - order by `(ChangeVersion, DocumentId)` for deterministic paging

### PostgreSQL: resource change query (journal-driven)

Parameters:

- `@ProjectName`, `@ResourceName`, `@ResourceVersion`
- `@MinChangeVersion`, `@MaxChangeVersion`
- `@AfterChangeVersion`, `@AfterDocumentId` (for keyset paging; use `0,0` for first page)
- `@Limit`

```sql
WITH direct AS (
    SELECT e.DocumentId
    FROM dms.DocumentChangeEvent e
    WHERE e.ProjectName = @ProjectName
      AND e.ResourceName = @ResourceName
      AND e.ResourceVersion = @ResourceVersion
      AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
deps_in_window AS (
    SELECT e.DocumentId
    FROM dms.IdentityChangeEvent e
    WHERE e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
indirect AS (
    SELECT DISTINCT re.ParentDocumentId AS DocumentId
    FROM dms.ReferenceEdge re
    JOIN deps_in_window w
      ON w.DocumentId = re.ChildDocumentId
),
candidates AS (
    SELECT DocumentId FROM direct
    UNION
    SELECT d.DocumentId
    FROM indirect i
    JOIN dms.Document d
      ON d.DocumentId = i.DocumentId
    WHERE d.ProjectName = @ProjectName
      AND d.ResourceName = @ResourceName
      AND d.ResourceVersion = @ResourceVersion
),
dep_max AS (
    SELECT re.ParentDocumentId AS DocumentId,
           MAX(child.IdentityVersion) AS MaxDepIdentityVersion
    FROM dms.ReferenceEdge re
    JOIN dms.Document child
      ON child.DocumentId = re.ChildDocumentId
    JOIN candidates c
      ON c.DocumentId = re.ParentDocumentId
    GROUP BY re.ParentDocumentId
),
computed AS (
    SELECT d.DocumentId,
           GREATEST(
             d.ContentVersion,
             d.IdentityVersion,
             COALESCE(dm.MaxDepIdentityVersion, 0)
           ) AS ChangeVersion
    FROM dms.Document d
    JOIN candidates c
      ON c.DocumentId = d.DocumentId
    LEFT JOIN dep_max dm
      ON dm.DocumentId = d.DocumentId
    WHERE d.ProjectName = @ProjectName
      AND d.ResourceName = @ResourceName
      AND d.ResourceVersion = @ResourceVersion
)
SELECT DocumentId, ChangeVersion
FROM computed
WHERE ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
  AND (
    ChangeVersion > @AfterChangeVersion
    OR (ChangeVersion = @AfterChangeVersion AND DocumentId > @AfterDocumentId)
  )
ORDER BY ChangeVersion, DocumentId
LIMIT @Limit;
```

### SQL Server: resource change query (journal-driven)

Parameters are the same as above.

```sql
WITH direct AS (
    SELECT e.DocumentId
    FROM dms.DocumentChangeEvent e
    WHERE e.ProjectName = @ProjectName
      AND e.ResourceName = @ResourceName
      AND e.ResourceVersion = @ResourceVersion
      AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
deps_in_window AS (
    SELECT e.DocumentId
    FROM dms.IdentityChangeEvent e
    WHERE e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
indirect AS (
    SELECT DISTINCT re.ParentDocumentId AS DocumentId
    FROM dms.ReferenceEdge re
    JOIN deps_in_window w
      ON w.DocumentId = re.ChildDocumentId
),
candidates AS (
    SELECT DocumentId FROM direct
    UNION
    SELECT d.DocumentId
    FROM indirect i
    JOIN dms.Document d
      ON d.DocumentId = i.DocumentId
    WHERE d.ProjectName = @ProjectName
      AND d.ResourceName = @ResourceName
      AND d.ResourceVersion = @ResourceVersion
),
dep_max AS (
    SELECT re.ParentDocumentId AS DocumentId,
           MAX(child.IdentityVersion) AS MaxDepIdentityVersion
    FROM dms.ReferenceEdge re
    JOIN dms.Document child
      ON child.DocumentId = re.ChildDocumentId
    JOIN candidates c
      ON c.DocumentId = re.ParentDocumentId
    GROUP BY re.ParentDocumentId
),
computed AS (
    SELECT d.DocumentId,
           CASE
             WHEN dm.MaxDepIdentityVersion IS NULL
               THEN (CASE WHEN d.ContentVersion > d.IdentityVersion THEN d.ContentVersion ELSE d.IdentityVersion END)
             ELSE
               (CASE
                  WHEN dm.MaxDepIdentityVersion >= d.ContentVersion AND dm.MaxDepIdentityVersion >= d.IdentityVersion THEN dm.MaxDepIdentityVersion
                  WHEN d.ContentVersion >= d.IdentityVersion THEN d.ContentVersion
                  ELSE d.IdentityVersion
                END)
           END AS ChangeVersion
    FROM dms.Document d
    JOIN candidates c
      ON c.DocumentId = d.DocumentId
    LEFT JOIN dep_max dm
      ON dm.DocumentId = d.DocumentId
    WHERE d.ProjectName = @ProjectName
      AND d.ResourceName = @ResourceName
      AND d.ResourceVersion = @ResourceVersion
)
SELECT TOP (@Limit) DocumentId, ChangeVersion
FROM computed
WHERE ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
  AND (
    ChangeVersion > @AfterChangeVersion
    OR (ChangeVersion = @AfterChangeVersion AND DocumentId > @AfterDocumentId)
  )
ORDER BY ChangeVersion, DocumentId;
```

### Worked example (indirect identity change)

Scenario:

- Student `S` identity changes at `IdentityVersion = 100`.
- GraduationPlan `G` references `S` (non-identity component reference).
- `G` has no local changes.

Write side:

- `S.IdentityVersion` is set to `100`.
- Insert `dms.IdentityChangeEvent(ChangeVersion=100, DocumentId=S)`.

Read side (GraduationPlan change query window `[99, 100]`):

- `deps_in_window` contains `S`.
- `indirect` contains `G` via `dms.ReferenceEdge(G → S)`.
- `dep_max(G) = 100` (from `S.IdentityVersion`).
- `ChangeVersion(G) = max(G.LocalChangeVersion, 100) = 100`.
- `G` is returned, with `changeVersion = 100`.

## Retention considerations

The journals will grow with write volume. Two pragmatic approaches:

1. **No retention (initially)**:
   - simplest, but tables grow without bound.
2. **Retention window** (recommended once feature is used):
   - periodically delete rows older than a retention horizon (by `ChangeVersion` range, time, or both),
   - expose `oldestChangeVersion` accordingly, so clients never request data older than retention.

Partitioning by `ChangeVersion` range (or time) is a natural fit for both PostgreSQL and SQL Server when this becomes operationally necessary.

## Optional enhancements (write-time work to reduce read-time cost)

The journal-driven algorithm still needs to compute `max(child.IdentityVersion)` per candidate via `dms.ReferenceEdge`. For large candidate sets, this group-by can be the dominant read cost.

If this becomes too expensive, the following options can simplify the read-time computation without requiring SERIALIZABLE:

### A) Monotonic “max dependency identity” column (bounded fan-out, no SERIALIZABLE)

Add a column on `dms.Document`:

- `MaxDepIdentityVersion bigint NOT NULL DEFAULT 0`

Maintain it as a monotonic max:

- when `ReferenceEdge(Parent → Child)` is inserted, set `Parent.MaxDepIdentityVersion = max(Parent.MaxDepIdentityVersion, Child.IdentityVersion)`
- when `Child.IdentityVersion` increases, set `Parent.MaxDepIdentityVersion = max(Parent.MaxDepIdentityVersion, Child.IdentityVersion)` for all parents

This is a write-time fan-out across parents, but it is:

- **one-column updates** (no reconstitution/hashing),
- **monotonic** (no need to handle decreases), and
- does not require SERIALIZABLE edge scans if edge writers also “self-heal” by applying the max at edge creation time.

Read-time simplifies to:

```text
ChangeVersion(P) = max(P.ContentVersion, P.IdentityVersion, P.MaxDepIdentityVersion)
```

### B) Denormalize parent resource key onto `dms.ReferenceEdge`

If indirect expansion frequently joins `ReferenceEdge → Document` only to filter parents by `(ProjectName, ResourceName, ResourceVersion)`, consider storing those parent fields on `ReferenceEdge` (maintained on parent writes). This reduces a join in the candidate-building step.

## Summary

Option 1 keeps the derived-token semantics (no write-time fan-out for indirect representation changes) and adds small append-only journals so change queries can:

- scan contributor events by `ChangeVersion` range,
- expand indirect impacts via `dms.ReferenceEdge`,
- compute derived `ChangeVersion` only for candidates,
- page deterministically by `(ChangeVersion, DocumentId)`.
