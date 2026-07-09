---
jira: DMS-991
jira_url: https://edfi.atlassian.net/browse/DMS-991
---

# Story: Reconstitute Reference Identity Values from Local Propagated Columns

## Description

Implement reconstitution of reference identity values into returned JSON using the referencing row’s locally stored propagated identity columns.

This baseline redesign persists referenced identity natural-key fields alongside every `..._DocumentId` reference column (kept synchronized by FK cascades). Reads should use those local columns directly:
- no referenced-table joins for reference identity fields,
- no union-view projection required for abstract references.

Internal identity-lineage anchor columns are propagation-only metadata. They are not JSON-bound and are never emitted in
the reconstructed reference object.

Align with:
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (reference reconstitution),
- `reference/design/backend-redesign/design-docs/data-model.md` (propagated identity columns and abstract identity tables).

## Acceptance Criteria

- Reference objects in responses contain identity fields populated from local propagated columns on the referencing table row.
- No referenced-table joins are required to emit reference identity fields (concrete or abstract).
- Integration tests cover at least one identity-component reference and one non-identity reference scenario.

## Tasks

1. Extend the compiled read plan to map reference-object identity fields to local propagated column names.
2. Populate reference objects from those columns during JSON reconstitution.
3. Add tests for:
   - concrete references (identity fields emitted correctly),
   - abstract references (abstract identity fields emitted correctly),
   - null/absent references (no partial reference objects).
