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

- For every resource that has an EducationOrganization securableElement, the DDL generator emits an index on the corresponding DB column (mapped via the Derived Relational Model from the securable element path). The securable element is expressed as a JSON path under a reference object (e.g. `$.studentAcademicRecordReference.educationOrganizationId`); the DB column is resolved from whichever table the reference lives on — the root resource table for non-nested paths, or the child collection table for array-nested paths (e.g. `$.requiredAssessments[*].assessmentReference.educationOrganizationId` resolves to a child table column). See `auth.md` § "ResolveSecurableElementColumnPath" for the canonical statement.
- Skip the index if it is already covered by a PrimaryAssociation index above.
- Also skip the index when the resolved `(table, column)` is already the leading column of an existing PrimaryKey or UniqueConstraint index — that index already supports the auth equality lookup with no extra storage or write cost. In this case, no `DbIndexKind.Authorization` inventory entry is emitted; consumers verifying auth-index coverage must therefore treat PK/UK leading-column membership as equivalent coverage.

### Namespace securableElement indexes

- For every resource that has a Namespace securableElement, the DDL generator emits an index on the corresponding DB column (mapped via the Derived Relational Model from the securable element path, on whichever table — root or child collection — the reference lives on; see `auth.md` for the canonical wording).
- Skip the index if it is already covered by a PrimaryAssociation index above. This dedup is a no-op for DS 5.2 since Namespace columns do not currently overlap PA key columns, but applying it symmetrically with EdOrg keeps coverage robust to extension schemas that may put a Namespace path on a PA key column.
- Also skip the index when the resolved `(table, column)` is already the leading column of an existing PrimaryKey or UniqueConstraint index — same rationale and manifest-contract caveat as the EdOrg case above.

### General requirements

- All indexes use the canonical column name (not the alias column), since alias columns cannot be indexed. This aligns with the key-unification design.
- All indexes are emitted for both PostgreSQL and SQL Server dialects.
- The generated DDL is deterministic — running the generator twice with the same input produces identical output.
