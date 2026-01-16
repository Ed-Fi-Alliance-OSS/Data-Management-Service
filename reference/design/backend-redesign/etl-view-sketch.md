# ETL View Sketch: Natural-Key Views with `LastModifiedDate` + `ChangeVersion`

## Status

Sketch. This is **not** a full design. It only answers “how might ETL-friendly SQL views work?” for consumers that want natural-key-shaped rows (not surrogate `DocumentId` FKs) and want update-tracking metadata per row.

## Goal

Provide optional read-only database views that:

- look like composite natural key tables (instead of `..._DocumentId` / `..._DescriptorId` surrogate keys), and
- include `LastModifiedDate` and `ChangeVersion` per row, aligned to the representation-sensitive semantics in [update-tracking.md](update-tracking.md).

This is intended for direct database ETL and analytics-style consumers. It does **not** change the write model (DMS still stores stable `DocumentId` FKs and resolves identities via `dms.ReferentialIdentity`).

## Non-goals / open questions

- Exact naming conventions and which fields to expose per resource/reference are out of scope
- Other tables that ETL uses beyond the relational resource tables

## View shape (sketch)

For each project schema `{schema}` (e.g., `edfi`) and each resource root table `{schema}.{R}`:

- Create a view `{schema}.{R}_Etl` that projects:
  - a stable technical id (`DocumentUuid` and optionally `DocumentId`),
  - the natural key fields for `R`:
    - scalar identity columns from `{schema}.{R}`, plus
    - referenced identity fields for any identity elements that are sourced from `{resource}Reference` objects (by joining through the stored `..._DocumentId` FK),
  - scalar fields from `{schema}.{R}`,
  - derived `LastModifiedDate` and `ChangeVersion`.

Define similar views for collection tables `{schema}.{R}_{CollectionPath}` that replace the parent `..._DocumentId` with the parent natural key columns (plus ordinals).

## Derived metadata (`LastModifiedDate`, `ChangeVersion`)

This mirrors the derived-token rules in [update-tracking.md](update-tracking.md):

- Representation dependencies are the outbound **non-descriptor** referenced documents for a parent `P`, indexed by `dms.ReferenceEdge(ParentDocumentId → ChildDocumentId)`.
- Descriptor references are excluded from `dms.ReferenceEdge` by design because descriptors are treated as immutable in this redesign (see `dms.Descriptor` in [data-model.md](data-model.md)).

For a parent document `P`:

- `LastModifiedDate(P) = max(P.ContentLastModifiedAt, P.IdentityLastModifiedAt, max(dep.IdentityLastModifiedAt))`
- `ChangeVersion(P) = max(P.ContentVersion, P.IdentityVersion, max(dep.IdentityVersion))`

In SQL, a common pattern is:

1. Precompute one row per parent in a `dep_max` CTE:
   - `MAX(child.IdentityVersion) AS MaxDepIdentityVersion`
   - `MAX(child.IdentityLastModifiedAt) AS MaxDepIdentityLastModifiedAt`
2. Join `dep_max` back to the parent rows and apply `GREATEST(...)` (PostgreSQL) or an equivalent `CASE` expression (SQL Server).

## Example (PostgreSQL): `edfi.StudentSchoolAssociation`

This uses the illustrative table shapes in [data-model.md](data-model.md) and projects a natural-key flavored row:

- `student.StudentUniqueId` instead of `ssa.Student_DocumentId`
- `school.SchoolId` instead of `ssa.School_DocumentId`
- derived `LastModifiedDate` and `ChangeVersion` for the SSA document

```sql
CREATE VIEW edfi.StudentSchoolAssociation_Etl AS
WITH dep_max AS (
    SELECT
        re.ParentDocumentId AS DocumentId,
        MAX(child.IdentityVersion)        AS MaxDepIdentityVersion,
        MAX(child.IdentityLastModifiedAt) AS MaxDepIdentityLastModifiedAt
    FROM dms.ReferenceEdge re
    JOIN edfi.StudentSchoolAssociation ssa
      ON ssa.DocumentId = re.ParentDocumentId
    JOIN dms.Document child
      ON child.DocumentId = re.ChildDocumentId
    GROUP BY re.ParentDocumentId
)
SELECT
    d.DocumentUuid AS DocumentUuid,
    ssa.DocumentId AS DocumentId,

    -- Natural key projection (instead of ..._DocumentId surrogate keys)
    student.StudentUniqueId,
    school.SchoolId,
    ssa.EntryDate,

    -- Example scalar fields
    ssa.ExitWithdrawDate,

    -- Derived metadata (representation-sensitive)
    GREATEST(
      d.ContentLastModifiedAt,
      d.IdentityLastModifiedAt,
      COALESCE(dm.MaxDepIdentityLastModifiedAt, d.IdentityLastModifiedAt)
    ) AS LastModifiedDate,

    GREATEST(
      d.ContentVersion,
      d.IdentityVersion,
      COALESCE(dm.MaxDepIdentityVersion, 0)
    ) AS ChangeVersion
FROM edfi.StudentSchoolAssociation ssa
JOIN dms.Document d
  ON d.DocumentId = ssa.DocumentId
LEFT JOIN dep_max dm
  ON dm.DocumentId = ssa.DocumentId
JOIN edfi.Student student
  ON student.DocumentId = ssa.Student_DocumentId
JOIN edfi.School school
  ON school.DocumentId = ssa.School_DocumentId;
```

### References, descriptors, and polymorphism (sketch)

Other resources follow the same patterns:

- **Non-descriptor references** (`..._DocumentId`):
  - join the referenced resource table and project identity columns (the same fields DMS would emit into `{resource}Reference` objects).
  - for abstract/polymorphic references, join `{schema}.{AbstractResource}_View` instead of a concrete table (see “Abstract identity views” in [data-model.md](data-model.md)).
- **Descriptor references** (`..._DescriptorId`):
  - join `dms.Descriptor` and project `Uri`

## Collections (sketch)

For collection-shaped outputs, define one view per collection table:

- Replace the parent FK column (e.g., `School_DocumentId`) with the parent natural key columns.
- Keep ordinals when order is significant for consumers.
- Reuse the parent document’s derived metadata (`LastModifiedDate`, `ChangeVersion`) by joining back to the parent root table (or the parent ETL view).

This sketch does not prescribe whether a collection ETL view should include ordinals, unique-constraint columns only, or both.

## Incremental ETL and performance notes

These views are convenient but can be expensive to scan in full:

- Computing `max(dep.IdentityVersion)` / `max(dep.IdentityLastModifiedAt)` per row is the same `dep_max` aggregation called out as potentially dominant in [update-tracking.md](update-tracking.md).
- The intended incremental pattern is:
  1. Select candidate `DocumentId`s in a `[min,max]` window using the journal-driven Change Query candidate selection (`dms.DocumentChangeEvent` for direct changes + `dms.IdentityChangeEvent` + `dms.ReferenceEdge` expansion for indirect changes).
  2. Join those candidate ids to the ETL view(s) to fetch natural-key-shaped rows.

If view performance is insufficient at scale, the same tradeoffs described in [update-tracking.md](update-tracking.md) apply (e.g., adding a monotonic `MaxDepIdentityVersion` column to avoid the `dep_max` group-by, or pushing derived metadata into an eventually-consistent projection table).

