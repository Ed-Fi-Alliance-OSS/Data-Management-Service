---
jira: DMS-1054
jira_url: https://edfi.atlassian.net/browse/DMS-1054
---

# Story: Emit Indexes for the Relationship-based and Namespace-based Strategies

## Description

The DDL generator should emit the indexes required by the Relationship-based and Namespace-based authorization strategies (excluding Person join indexes, which are handled in `DMS-1094`), as specified in:

- `reference/design/backend-redesign/design-docs/auth.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`

## Acceptance Criteria

### Index inventory in the Derived Relational Model

- Authorization indexes on resource tables are represented as `DbIndexInfo` entries with `Kind = DbIndexKind.Authorization` in `DerivedRelationalModelSet.IndexesInCreateOrder` (see `compiled-mapping-set.md` §2.2).
- These indexes are schema-derived from `securableElements` in ApiSchema.json and are part of the unified model inventory.
- Index ordering in `IndexesInCreateOrder` is canonical and deterministic: `(schema, table, name)`.

### PrimaryAssociation indexes

- The DDL generator emits an index for each of the following (hardcoded) PrimaryAssociations.
  Column names use the post-key-unification canonical storage column name on the root table —
  the form that survives `KeyUnificationPass` — not the pre-unification logical column name:
  - edfi.StudentSchoolAssociation on (SchoolId_Unified) INCLUDE (Student_DocumentId)
  - edfi.StudentContactAssociation on (Student_DocumentId) INCLUDE (Contact_DocumentId)
  - edfi.StaffEducationOrganizationAssignmentAssociation on (EducationOrganization_EducationOrganizationId) INCLUDE (Staff_DocumentId)
  - edfi.StaffEducationOrganizationEmploymentAssociation on (EducationOrganization_EducationOrganizationId) INCLUDE (Staff_DocumentId)
  - edfi.StudentEducationOrganizationResponsibilityAssociation on (EducationOrganization_EducationOrganizationId) INCLUDE (Student_DocumentId)

### EducationOrganization securableElement indexes

- For every resource that has an EducationOrganization securableElement, the DDL generator emits an index on the corresponding DB column (mapped via the Derived Relational Model from the securable element path). The securable element is expressed as a JSON path under a reference object (e.g. `$.studentAcademicRecordReference.educationOrganizationId`), but the DB column is resolved from the root resource table using `documentPathsMapping`. Skip the index if it is already covered by a PrimaryAssociation index above.

### Namespace securableElement indexes

- For every resource that has a Namespace securableElement, the DDL generator emits an index on the corresponding DB column (mapped via the Derived Relational Model from the securable element path). Skip the index if it is already covered by a PrimaryAssociation index above. This dedup is a no-op for DS 5.2 since Namespace columns do not currently overlap PA key columns, but applying it symmetrically with EdOrg keeps coverage robust to extension schemas that may put a Namespace path on a PA key column.

### General requirements

- All indexes use the canonical column name (not the alias column), since alias columns cannot be indexed. This aligns with the key-unification design.
- All indexes are emitted for both PostgreSQL and SQL Server dialects.
- The generated DDL is deterministic — running the generator twice with the same input produces identical output.
