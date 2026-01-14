# Backend Redesign Alternative: ODS-Style Natural-Key Cascades (Maintain `ReferentialIdentity` via Natural Keys)

## Status

Draft (alternative design exploration).

## Context

The baseline redesign keeps stable surrogate keys (`DocumentId`) for all references and treats `dms.ReferentialIdentity` as a strict derived index maintained transactionally (including identity closure recompute) via application code + `dms.ReferenceEdge` + `dms.IdentityLock`.

This alternative explores a different trade:

- Keep the same **`DocumentId` surrogate-key model** (tables per resource; FK references use `..._DocumentId`).
- Also **materialize natural identity values** in relational columns for identity-bearing relationships, and let the database keep them consistent via **`ON UPDATE CASCADE`**.
- Maintain `dms.ReferentialIdentity` via **database triggers** driven by those materialized columns, instead of application-managed discovery/closure.

This pattern resembles Ed-Fi ODS conventions, where natural keys are stored and are the “thing that cascades”.

## Goal

Make `dms.ReferentialIdentity` (and any other identity-derived artifacts) stay in sync *because the database rewrites identity-bearing values itself*, not because the application runs an explicit identity-closure recompute.

## High-Level Idea

For any resource whose identity includes reference identity values (a “reference-bearing identity”):

1. Store the usual stable FK column:
   - `Student_DocumentId bigint NOT NULL` (for referential integrity and join performance)
2. Also store the referenced identity value(s) as columns on the referencing row:
   - `StudentUniqueId nvarchar(32) NOT NULL` (or whatever type is appropriate)
3. Enforce consistency between the two via a composite FK that includes `DocumentId` and the identity value:
   - `FOREIGN KEY (Student_DocumentId, StudentUniqueId) REFERENCES edfi.Student(DocumentId, StudentUniqueId) ON UPDATE CASCADE`

If `edfi.Student.StudentUniqueId` changes, the database rewrites the dependent rows’ `StudentUniqueId` values automatically (cascading), and triggers on those dependent tables can then update derived artifacts (e.g., `dms.ReferentialIdentity`) using only local row data.

## Data Model Changes vs. Baseline Redesign

### 1) Add “identity value” columns for identity-component references

For each identity-component document reference edge source (derived from ApiSchema `identityJsonPaths` + `documentPathsMapping`):

- Add columns for the identity fields that appear in the reference object.
  - Example: `$.studentReference.studentUniqueId` becomes `StudentUniqueId`.
  - Example: `$.schoolReference.schoolId` becomes `SchoolId`.

These columns are **only required for references that participate in the parent’s identity**, not for arbitrary non-identity references (unless you intentionally choose to denormalize more).

### 2) Add unique constraints on referenced resources to support composite FKs

To allow composite FKs of the form `(DocumentId, NaturalKeyPart...)`, the referenced resource root tables need an appropriate unique constraint:

- Example on `edfi.Student`:
  - `UNIQUE (DocumentId, StudentUniqueId)`
- Example on `edfi.School`:
  - `UNIQUE (DocumentId, SchoolId)`

This is in addition to (or instead of) the baseline redesign’s natural-key unique constraint choices.

### 3) Add composite FKs with `ON UPDATE CASCADE`

On each referencing table that stores identity value columns:

- Add composite FK(s) that include the referenced `..._DocumentId` and the referenced identity value columns.
- Use `ON UPDATE CASCADE` so identity changes in the referenced row rewrite dependent identity columns.

## Concrete Example: `StudentSchoolAssociation`

### Baseline redesign shape (conceptual)

`edfi.StudentSchoolAssociation` stores:

- `DocumentId` (PK/FK → `dms.Document`)
- `Student_DocumentId` (FK → `edfi.Student(DocumentId)`)
- `School_DocumentId` (FK → `edfi.School(DocumentId)`)
- Unique constraint for identity uses the FK columns (no natural-key duplication).

### Alternative shape (ODS-style identity columns + cascades)

In addition to `..._DocumentId`, also store the identity values that participate in SSA’s identity:

- `StudentUniqueId`
- `SchoolId`

And enforce they match the referenced row:

#### PostgreSQL sketch

```sql
-- Referenced resources expose (DocumentId, NaturalKey...) as a unique key
ALTER TABLE edfi.Student ADD CONSTRAINT UX_Student_DocumentId_StudentUniqueId
  UNIQUE (DocumentId, StudentUniqueId);

ALTER TABLE edfi.School ADD CONSTRAINT UX_School_DocumentId_SchoolId
  UNIQUE (DocumentId, SchoolId);

-- Referencing resource stores both DocumentId and identity values, kept in sync by cascade
ALTER TABLE edfi.StudentSchoolAssociation
  ADD COLUMN StudentUniqueId varchar(32) NOT NULL,
  ADD COLUMN SchoolId integer NOT NULL;

ALTER TABLE edfi.StudentSchoolAssociation
  ADD CONSTRAINT FK_SSA_Student_DocId_StudentUniqueId
    FOREIGN KEY (Student_DocumentId, StudentUniqueId)
    REFERENCES edfi.Student (DocumentId, StudentUniqueId)
    ON UPDATE CASCADE;

ALTER TABLE edfi.StudentSchoolAssociation
  ADD CONSTRAINT FK_SSA_School_DocId_SchoolId
    FOREIGN KEY (School_DocumentId, SchoolId)
    REFERENCES edfi.School (DocumentId, SchoolId)
    ON UPDATE CASCADE;

-- Identity uniqueness for SSA can now be enforced directly on the natural keys
ALTER TABLE edfi.StudentSchoolAssociation
  ADD CONSTRAINT UX_SSA_StudentUniqueId_SchoolId_EntryDate
    UNIQUE (StudentUniqueId, SchoolId, EntryDate);
```

#### SQL Server sketch

```sql
ALTER TABLE edfi.Student
  ADD CONSTRAINT UX_Student_DocumentId_StudentUniqueId
    UNIQUE (DocumentId, StudentUniqueId);

ALTER TABLE edfi.School
  ADD CONSTRAINT UX_School_DocumentId_SchoolId
    UNIQUE (DocumentId, SchoolId);

ALTER TABLE edfi.StudentSchoolAssociation
  ADD StudentUniqueId nvarchar(32) NOT NULL,
      SchoolId int NOT NULL;

ALTER TABLE edfi.StudentSchoolAssociation
  ADD CONSTRAINT FK_SSA_Student_DocId_StudentUniqueId
    FOREIGN KEY (Student_DocumentId, StudentUniqueId)
    REFERENCES edfi.Student (DocumentId, StudentUniqueId)
    ON UPDATE CASCADE;

ALTER TABLE edfi.StudentSchoolAssociation
  ADD CONSTRAINT FK_SSA_School_DocId_SchoolId
    FOREIGN KEY (School_DocumentId, SchoolId)
    REFERENCES edfi.School (DocumentId, SchoolId)
    ON UPDATE CASCADE;
```

## Maintaining `dms.ReferentialIdentity` in This Alternative

### What changes

In the baseline redesign, recomputing `dms.ReferentialIdentity` for reference-bearing identities requires:

- knowing which documents are impacted (identity-closure expansion),
- locking them (`dms.IdentityLock` ordering),
- projecting identity values across FK joins (including references),
- computing UUIDv5 referential ids, then replacing rows.

In this alternative, the key change is:

- The **identity-bearing value columns are always locally present** on the resource row.
- They are kept consistent with upstream identity changes by the database (`ON UPDATE CASCADE`).

So `dms.ReferentialIdentity` maintenance becomes “row-local”: insert/update/delete of a resource row can update the corresponding `dms.ReferentialIdentity` row(s) without doing identity projection joins.

### Trigger approach (per resource table)

For each resource root table `{schema}.{R}`:

- `AFTER INSERT`: compute and insert the primary referential id row.
- `AFTER UPDATE` (only when identity columns changed): recompute referential id and update/replace the row.
- `AFTER DELETE`: delete the row(s) (or rely on `ON DELETE CASCADE` via `DocumentId` if you model it that way).

The trigger must compute the UUIDv5 inputs exactly as Core does:

- `ResourceInfoString = ProjectName + ResourceName`
- `DocumentIdentityString = join("#", "$" + IdentityJsonPath + "=" + IdentityValue)`
- `ReferentialId = UUIDv5(namespace, ResourceInfoString + DocumentIdentityString)`

Because each resource has a fixed ordered list of `identityJsonPaths`, the DDL generator can emit a per-resource trigger expression that concatenates the correct path literals with the relevant column values.

### Subclass alias rows

If a resource is a subclass and participates in polymorphic identity behavior, the trigger must maintain up to two rows:

- primary: `(ReferentialId, DocumentId, ResourceKeyId for concrete resource)`
- alias: `(ReferentialId, DocumentId, ResourceKeyId for superclass resource)`

The alias `DocumentIdentityString` may differ (identity rename case) because the identity JsonPath literal changes (e.g., `$.schoolId` vs `$.educationOrganizationId`). The generator can emit both strings deterministically.

## Why Not Move `ReferentialId` to Per-Resource Tables

It may be tempting to eliminate `dms.ReferentialIdentity` and instead add a `ReferentialId` column + unique index to each resource root table (and then resolve `ReferentialId → DocumentId` by querying the target resource table).

Reasons to avoid that approach (even in this ODS-style alternative):

- **Loses the “single identity index” contract**: resolution becomes “group by target type, then query N tables”, which increases query count and branching complexity on the hot write path (and complicates cache keying: you must include resource type / `ResourceKeyId`, not just the UUID).
- **Makes polymorphism harder, not easier**: abstract targets still need a separate structure (an abstract identity table per abstract resource), so “per-resource referential ids” still does not fully eliminate special-case identity storage.
- **Spreads random-UUID index overhead across every resource table**: each table gains a write-hot UUID unique index with its own bloat/fragmentation and maintenance characteristics (especially on SQL Server). This can increase total index footprint and operational tuning surface area.
- **Complicates uniform diagnostics and repair**: a single “rebuild/verify identity index” operation becomes “scan and validate every resource table + every abstract identity table” to find missing/incorrect referential ids.
- **Makes descriptor behavior less uniform**: descriptor referential ids are still special (computed from normalized URI + descriptor type). Without a central identity table, you either introduce another “descriptor identity” table or add referential-id columns/indexes to descriptor storage specifically, which reintroduces a central special case.

In short: per-resource storage can trade one hot index for many smaller hot indexes, but it materially increases query fan-out and correctness surface area while still requiring special handling for polymorphic/abstract identity resolution.

## Polymorphic / Abstract Reference Targets (Big Complication)

ODS-style cascades rely on having a concrete referenced table to cascade from.

For abstract targets (e.g., `EducationOrganizationReference`) the baseline redesign intentionally uses:

- FK → `dms.Document(DocumentId)` for existence, plus
- application-level membership validation against `{Abstract}_View`.

That structure cannot support `ON UPDATE CASCADE` of “abstract identity values” because there is no single base table with the abstract identity columns.

To support ODS-style cascades for abstract identities, introduce an **“abstract identity” table per abstract resource** that materializes the abstract identity fields for every concrete member document, and reference that table with `ON UPDATE CASCADE`.

### Example: `edfi.EducationOrganizationIdentity`

This table replaces the union view as the DB-level membership/identity anchor for `EducationOrganization` references.

#### PostgreSQL (sketch)

```sql
CREATE TABLE edfi.EducationOrganizationIdentity (
    DocumentId bigint NOT NULL,
    EducationOrganizationId integer NOT NULL,
    Discriminator varchar(256) NOT NULL,
    CONSTRAINT PK_EducationOrganizationIdentity PRIMARY KEY (DocumentId),
    CONSTRAINT FK_EducationOrganizationIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_EducationOrganizationIdentity_DocumentId_EducationOrganizationId UNIQUE (DocumentId, EducationOrganizationId),
    CONSTRAINT UX_EducationOrganizationIdentity_EducationOrganizationId UNIQUE (EducationOrganizationId)
);
```

Populate it from each concrete member table (trigger-maintained):

```sql
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

#### SQL Server (sketch)

```sql
CREATE TABLE edfi.EducationOrganizationIdentity (
    DocumentId bigint NOT NULL,
    EducationOrganizationId int NOT NULL,
    Discriminator nvarchar(256) NOT NULL,
    CONSTRAINT PK_EducationOrganizationIdentity PRIMARY KEY CLUSTERED (DocumentId),
    CONSTRAINT FK_EducationOrganizationIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_EducationOrganizationIdentity_DocumentId_EducationOrganizationId UNIQUE (DocumentId, EducationOrganizationId),
    CONSTRAINT UX_EducationOrganizationIdentity_EducationOrganizationId UNIQUE (EducationOrganizationId)
);
GO

CREATE OR ALTER TRIGGER edfi.TR_School_EdOrgIdentity_Upsert
ON edfi.School
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE t
    SET
        t.EducationOrganizationId = i.SchoolId,
        t.Discriminator = N'School'
    FROM edfi.EducationOrganizationIdentity t
    JOIN inserted i ON i.DocumentId = t.DocumentId;

    INSERT INTO edfi.EducationOrganizationIdentity (DocumentId, EducationOrganizationId, Discriminator)
    SELECT i.DocumentId, i.SchoolId, N'School'
    FROM inserted i
    WHERE NOT EXISTS (
        SELECT 1 FROM edfi.EducationOrganizationIdentity t WHERE t.DocumentId = i.DocumentId
    );
END;
GO
```

### Referencing an abstract identity with cascades

A table that stores an `EducationOrganizationReference` can now store `EducationOrganizationId` alongside the stable FK and enforce both membership and identity cascades:

```sql
ALTER TABLE edfi.SomeResource
  ADD EducationOrganizationId integer NOT NULL;

ALTER TABLE edfi.SomeResource
  ADD CONSTRAINT FK_SomeResource_EdOrgIdentity
    FOREIGN KEY (EducationOrganization_DocumentId, EducationOrganizationId)
    REFERENCES edfi.EducationOrganizationIdentity (DocumentId, EducationOrganizationId)
    ON UPDATE CASCADE;
```

This can remove the need for `{Abstract}_View` membership validation on the write path (the FK implies membership) and can simplify read-time abstract identity projection (join to the identity table for the abstract identity fields).

## Benefits

- `dms.ReferentialIdentity` maintenance can be DB-owned and row-local (no identity projection joins, no identity-closure expansion in application code).
- Identity uniqueness for reference-bearing identities can be expressed directly as natural-key uniqueness constraints.
- Reads that need identity values may avoid extra joins if the natural-key columns are already present.

## Costs / Risks (Why the Baseline Design Avoids This)

- **Reintroduces rewrite cascades**: changing a frequently-referenced identity value can rewrite a large portion of the database (now enforced by FK cascade). This is exactly the fan-out the baseline redesign is trying to avoid.
- **Harder to keep cross-engine parity**: cascade behavior, trigger semantics, and multi-row update patterns differ between PostgreSQL and SQL Server and require careful testing.
- **Operational blast radius**: a single identity update can touch many tables, fire many triggers, and create large write amplification (locks, WAL/log growth, index churn).
- **Polymorphism is awkward**: abstract targets don’t naturally fit `ON UPDATE CASCADE` without adding additional derived tables.
- **Still needs strict correctness testing**: the DB will enforce consistency, but a wrong trigger (or a mismatch vs Core UUIDv5 formatting) can still produce incorrect mappings.

## When This Alternative Might Be Acceptable

- Identity updates are enabled but operationally rare.
- You strongly prefer “database enforces derived artifacts” over application-managed closure recompute.
- You are willing to accept:
  - increased write amplification during identity updates,
  - more complex DDL generation (extra columns/constraints/triggers),
  - and potentially additional derived tables for abstract identity support.
