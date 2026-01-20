# Story: Execute Root-Table Queries with Deterministic Paging

## Description

Implement query execution consistent with the redesign constraints:

- Query compilation is limited to root-table paths (`queryFieldMapping` does not cross arrays).
- Page selection is performed over the resource root table ordered by `DocumentId` ascending.
- Reconstitution is page-based (hydrate/reconstitute in bulk for the selected page).

Align with `reference/design/backend-redesign/summary.md` and `reference/design/backend-redesign/flattening-reconstitution.md` query sections.

Note: applies to non-descriptor resources; descriptor endpoint query behavior is covered separately.

## Acceptance Criteria

- Query filtering uses only fields mapped to root-table columns.
- Paging is deterministic and stable (order by `DocumentId` ascending).
- Returned items are reconstituted using the page hydration path (not N get-by-id calls).
- Integration tests cover:
  - filtering on a scalar field,
  - paging behavior across multiple pages.

## Tasks

1. Implement query compilation from `queryFieldMapping` to physical columns (root only).
2. Implement SQL generation for filters + paging and execute it safely (parameterized).
3. Integrate page keyset selection with hydration + reconstitution.
4. Add integration tests for query correctness and stable paging.
