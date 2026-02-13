# ETL View Sketch: Natural-Key Views with `LastModifiedDate` + `ChangeVersion`

## Status

Sketch. This is **not** a full design. It answers “how might ETL-friendly SQL views work?” for consumers that want natural-key-shaped rows (not surrogate `DocumentId` FKs) and want update-tracking metadata per row.

## Goal

Provide optional read-only database views that:

- look like composite natural key tables (instead of `..._DocumentId` / `..._DescriptorId` surrogate keys), and
- include `LastModifiedDate` and `ChangeVersion` per row, aligned to the stored-stamp semantics in [update-tracking.md](update-tracking.md).

This is intended for direct database ETL and analytics-style consumers. It does **not** change the write model (DMS still stores stable `DocumentId` FKs and resolves identities via `dms.ReferentialIdentity`).

## View shape (sketch)

For each project schema `{schema}` (e.g., `edfi`) and each resource root table `{schema}.{R}`:

- Create a view `{schema}.{R}_Etl` that projects:
  - a stable technical id (`DocumentUuid` and optionally `DocumentId`),
  - the natural key fields for `R`:
    - scalar identity columns from `{schema}.{R}`, plus
    - referenced identity fields for any identity elements sourced from `{resource}Reference` objects (from local per-site identity-part binding columns; aliases when unified; see [key-unification.md](key-unification.md)),
  - scalar fields from `{schema}.{R}`,
  - `LastModifiedDate` and `ChangeVersion` from `dms.Document` stamps.

Define similar views for collection tables `{schema}.{R}_{CollectionPath}` that replace the parent `..._DocumentId` with the parent natural key columns (plus ordinals).

## Update-tracking metadata (`LastModifiedDate`, `ChangeVersion`)

With stored stamps:

- `LastModifiedDate(P) = dms.Document.ContentLastModifiedAt`
- `ChangeVersion(P) = dms.Document.ContentVersion`

No dependency aggregation is required for these views in the baseline redesign because indirect impacts are materialized as real updates on referrers (FK cascades update canonical reference identity storage columns, triggering normal stamping). Under key unification, per-site identity columns may be generated aliases; see [key-unification.md](key-unification.md).

## Example (PostgreSQL): `edfi.StudentSchoolAssociation`

This uses the illustrative table shapes in [data-model.md](data-model.md) and projects a natural-key flavored row:

- `StudentUniqueId` instead of `ssa.Student_DocumentId` (from `ssa.Student_StudentUniqueId`)
- `SchoolId` instead of `ssa.School_DocumentId` (from `ssa.School_SchoolId`)
- stored `LastModifiedDate` and `ChangeVersion` for the SSA document

```sql
CREATE VIEW edfi.StudentSchoolAssociation_Etl AS
SELECT
    d.DocumentUuid AS DocumentUuid,
    ssa.DocumentId AS DocumentId,

    -- Natural key projection (instead of ..._DocumentId surrogate keys)
    ssa.Student_StudentUniqueId,
    ssa.School_SchoolId,
    ssa.EntryDate,

    -- Example scalar fields
    ssa.ExitWithdrawDate,

    -- Stored metadata (representation-sensitive)
    d.ContentLastModifiedAt AS LastModifiedDate,
    d.ContentVersion        AS ChangeVersion
FROM edfi.StudentSchoolAssociation ssa
JOIN dms.Document d
  ON d.DocumentId = ssa.DocumentId;
```

### References, descriptors, and polymorphism (sketch)

- **Non-descriptor references** (`..._DocumentId`):
  - project identity fields from the referencing table’s per-site identity-part binding columns (aliases when unified; see [key-unification.md](key-unification.md)).
  - this applies to both concrete and abstract references.
- **Descriptor references** (`..._DescriptorId`):
  - join `dms.Descriptor` and project `Uri` (unless a separate descriptor-URI denormalization is introduced).

## Collections (sketch)

For collection-shaped outputs, define one view per collection table:

- Replace the parent FK column (e.g., `School_DocumentId`) with the parent natural key columns.
- Keep ordinals when order is significant for consumers.
- Reuse the parent document’s stored metadata (`LastModifiedDate`, `ChangeVersion`) by joining back to the parent root table (or the parent ETL view).

## Incremental ETL and performance notes

The intended incremental pattern is:
1. Select candidate `DocumentId`s in a `[min,max]` window using journal-driven Change Query candidate selection (`dms.DocumentChangeEvent` + verify against `dms.Document.ContentVersion`; see [update-tracking.md](update-tracking.md)).
2. Join those candidate ids to the ETL view(s) to fetch natural-key-shaped rows.

If view performance is insufficient at scale, consider:
- pre-materialized projections (e.g., `dms.DocumentCache`), or
- narrow denormalized “ETL surfaces” for specific consumers.
