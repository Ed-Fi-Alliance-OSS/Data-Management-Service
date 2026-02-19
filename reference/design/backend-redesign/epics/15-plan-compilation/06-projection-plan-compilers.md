---
jira: TBD
jira_url: TBD
---

# Story: Compile Projection Plans (Reference Identity + Descriptor URI)

## Description

Compile the additional plan metadata (and SQL where needed) required for projection steps that sit alongside hydration:

- **Reference identity projection (no joins)**: metadata that maps reference-object identity fields to local propagated binding/path columns on the referencing table row (driven by `DocumentReferenceBindings`).
- **Descriptor URI projection (batched)**: deterministic page-batched lookup `(DescriptorId, Uri)` for all descriptor ids referenced by a page.

This story is about producing deterministic, executor-ready projection inputs. The runtime projection execution and JSON reconstitution are covered by the relational read-path epic.

Design references:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (projection responsibilities: reference identity + descriptor URI)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (reference reconstitution from local columns)
- `reference/design/backend-redesign/design-docs/key-unification.md` (descriptor FK de-duplication notes under key unification)

## Scope (What This Story Is Talking About)

- Owns compilation/validation of projection-specific plan inputs that are stable for a selection key and do not require SQL parsing at runtime.
- Does not implement projection execution loops or JSON writing; those are owned by the read-path stories.

## Acceptance Criteria

### Reference identity projection metadata (no referenced-table joins)

- Compiled read plans include (or reference) deterministic `DocumentReferenceBinding` metadata required to emit reference objects from local columns (no referenced-table joins).
- Plan compilation validates that, for every `DocumentReferenceBinding`:
  - the FK presence column and all identity-part binding columns exist on the referencing `DbTableModel`, and
  - those columns are included in the corresponding `TableReadPlan` select list so projection can read by ordinal.

### Descriptor URI projection plan (page-batched)

- Compiled plans include a deterministic descriptor-projection query plan that:
  - is page-batched (no per-row descriptor lookups),
  - returns `(DescriptorId, Uri)` for all descriptor ids referenced by the page,
  - avoids N+1 joins (does not left-join `dms.Descriptor` once per descriptor FK column into every hydration `SELECT`).
- Projection SQL is canonicalized and stable for a fixed selection key (pgsql + mssql).

### Testing

- Unit tests validate deterministic output and model reference integrity:
  - descriptor projection plans reference only embedded model elements and `dms.Descriptor`,
  - reference identity projection validation fails deterministically for missing columns/bindings.

## Tasks

1. Define a projection-plan contract shape consumable by the read executor (descriptor projection SQL + expected result-set shape).
2. Compile descriptor projection plans from `RelationalResourceModel.DescriptorEdgeSources`, producing stable, canonicalized SQL.
3. Compile and/or validate reference identity projection metadata from `RelationalResourceModel.DocumentReferenceBindings` and ensure required columns are present in `TableReadPlan` select lists.
4. Add unit tests for deterministic output and model reference integrity (pgsql + mssql).
