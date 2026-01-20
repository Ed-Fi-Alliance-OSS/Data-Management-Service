# Story: Populate Propagated Reference Identity Columns (No Edge Table)

## Description

Populate propagated identity natural-key columns alongside every persisted `..._DocumentId` reference FK column.

This is required by the baseline redesign to:
- enable `ON UPDATE CASCADE` propagation of referenced identity values into referrers (no touch cascades, no reverse-edge table),
- simplify query compilation for reference-identity query parameters (local predicates), and
- ensure indirect representation changes become real row updates that trigger normal update-tracking stamping.

Align with:
- `reference/design/backend-redesign/flattening-reconstitution.md` (reference bindings and flattening),
- `reference/design/backend-redesign/data-model.md` (reference column conventions and composite FKs), and
- `reference/design/backend-redesign/transactions-and-concurrency.md` (write-time propagation semantics).

## Acceptance Criteria

- For every document reference site, the persisted row includes:
  - `..._DocumentId`, and
  - `{RefBaseName}_{IdentityPart}` columns populated from the request body.
- Writes satisfy per-reference “all-or-none” constraints (no null-bypassing of composite FKs).
- A referenced identity update cascades updates into the stored reference identity columns (DB-level), proving that the write path populated the required columns for propagation.

## Tasks

1. Extend flattening plan compilation to emit bindings for propagated reference identity columns for every document reference site.
2. Populate propagated identity columns in row buffers from extracted reference identities during flattening.
3. Add unit/integration tests verifying:
   1. reference identity columns are present and correctly populated for both identity-component and non-identity references,
   2. “all-or-none” constraints are satisfied (and violations map to deterministic errors),
   3. an identity update on a referenced document cascades into referrers’ propagated columns.
