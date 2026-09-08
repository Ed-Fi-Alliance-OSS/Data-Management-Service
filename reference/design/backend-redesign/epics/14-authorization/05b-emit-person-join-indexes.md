---
jira: DMS-1094
jira_url: https://edfi.atlassian.net/browse/DMS-1094
---

# Story: Emit people indexes needed for joins

## Description

The DDL generator should emit the indexes required for traversing references to reach a Person (Student/Contact/Staff) for authorization. This is the companion to `DMS-1054`, which handles the remaining authorization index categories.

See:

- `reference/design/backend-redesign/design-docs/auth.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`

## Acceptance Criteria

### Index inventory in the Derived Relational Model

- Authorization indexes on resource tables are represented as `DbIndexInfo` entries with `Kind = DbIndexKind.Authorization` in `DerivedRelationalModelSet.IndexesInCreateOrder` (see `compiled-mapping-set.md` §2.2).
- These indexes are schema-derived from `securableElements` in ApiSchema.json and are part of the unified model inventory.
- Index ordering in `IndexesInCreateOrder` is canonical and deterministic: `(schema, table, name)`.

### Person join indexes

- For every resource that requires traversing references to reach a Person (Student/Contact/Staff) for authorization, the DDL generator emits an index on the foreign-key DocumentId column used in the join, with an INCLUDE on the table's own DocumentId. For example:
  - edfi.CourseTranscript on (StudentAcademicRecord_DocumentId) INCLUDE (DocumentId)
  - edfi.StudentAcademicRecord on (Student_DocumentId) INCLUDE (DocumentId)
  - This applies to every intermediate table in the path from the authorized resource to the Person resource.

### General requirements

- All indexes use the canonical column name (not the alias column), since alias columns cannot be indexed. This aligns with the key-unification design.
- All indexes are emitted for both PostgreSQL and SQL Server dialects.
- The generated DDL is deterministic — running the generator twice with the same input produces identical output.

NOTE: The `ResolveSecurableElementColumnPath` function might be of help.