---
jira: DMS-985
jira_url: https://edfi.atlassian.net/browse/DMS-985
---

# Story: Populate Propagation-Complete Reference Identity Tuples (No Edge Table)

## Description

Populate propagated identity natural-key columns and required internal identity-lineage `DocumentId` anchors alongside
every persisted `..._DocumentId` reference FK column.

This is required by the baseline redesign to:
- enable `ON UPDATE CASCADE` propagation of referenced identity values into referrers (no touch cascades, no reverse-edge table),
- atomically propagate reference-backed identity repoints by carrying both the public value and the stable source-row
  `DocumentId`,
- simplify query compilation for reference-identity query parameters (local predicates), and
- ensure indirect representation changes become real row updates that trigger normal update-tracking stamping.

Align with:
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (reference bindings and flattening),
- `reference/design/backend-redesign/design-docs/data-model.md` (reference column conventions and composite FKs), and
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (write-time propagation semantics).

## Acceptance Criteria

- For every document reference site, the persisted row includes:
  - `..._DocumentId`, and
  - `{RefBaseName}_{IdentityPart}` columns populated from the request body, and
  - the minimal required identity-lineage anchors from `ResolvedReferenceSet`, reusing an equivalent explicit
    `..._DocumentId` where the compiled model proves that reuse is valid.
- Writes satisfy per-reference “all-or-none” constraints (no null-bypassing of composite FKs).
- A referenced scalar identity update and a reference-backed identity repoint both cascade the complete stored tuple
  (values plus anchors) into referrers.

## Tasks

1. Extend flattening plan compilation to emit bindings for propagated reference identity columns and lineage anchors for
   every document reference site.
2. Populate public identity columns from extracted reference identities and anchor columns from `ResolvedReferenceSet`.
3. Add unit/integration tests verifying:
   1. reference identity columns are present and correctly populated for both identity-component and non-identity references,
   2. “all-or-none” constraints are satisfied (and violations map to deterministic errors),
   3. an identity update on a referenced document cascades into referrers’ complete propagation tuples,
   4. repointing DS 5.2 Session from School A to School B cascades both `SchoolId` and `School_DocumentId` into
      CourseOffering while its full Session and School FKs remain valid, and
   5. the same anchors propagate transitively through CourseOffering into Section.
