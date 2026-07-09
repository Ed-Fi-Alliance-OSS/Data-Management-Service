---
jira: DMS-1047
jira_url: https://edfi.atlassian.net/browse/DMS-1047
---

# Story: Compile Projection Plans (Reference Identity + Descriptor URI + Lineage Anchors)

## Description

Compile the additional plan metadata (and SQL where needed) required for projection steps that sit alongside hydration:

- **Reference identity projection (no joins)**: metadata that maps reference-object identity fields to local propagated binding/path columns on the referencing table row (driven by `DocumentReferenceBindings`).
- **Descriptor URI projection (batched)**: deterministic page-batched lookup `(DescriptorId, Uri)` for all descriptor ids referenced by a page.
- **Demanded lineage-anchor projection (batched)**: one dialect-specific `LineageAnchorResolutionPlan` for every non-empty
  finalized `(target resource, AnchorSetId)` variant, projecting ordered intrinsic lineage `DocumentId` values from a set
  of stable target `DocumentId`s.

This story produces deterministic, executor-ready projection inputs. The relational read-path epic executes reference
identity/descriptor projection and JSON reconstitution; E07-S01 executes demanded lineage-anchor plans during write
reference resolution.

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
  - emits deterministic result ordering (e.g., `ORDER BY DescriptorId`) so projection can consume stably,
  - avoids N+1 joins (does not left-join `dms.Descriptor` once per descriptor FK column into every hydration `SELECT`).
- Projection SQL is canonicalized and stable for a fixed selection key (pgsql + mssql).

### Demanded lineage-anchor projection plans (write-batched)

- Plan compilation consumes the complete finalized artifact and emits exactly one global plan for every used non-empty
  `(target resource, AnchorSetId)` variant, and no plan for empty or unused variants.
- Each plan names the concrete or abstract target table, canonical provider set-input kind/parameter, deterministic batch
  limit, target-`DocumentId` result ordinal, and demanded lineage result bindings in `IdentityLineageId` order.
- PostgreSQL and SQL Server SQL is canonical, set-wise, and returns exactly one non-null result per requested target id;
  plan compilation does not infer lineages from equal public values or issue per-reference SQL.

### Testing

- Unit tests validate deterministic output and model reference integrity:
  - descriptor projection plans reference only embedded model elements and `dms.Descriptor`,
  - reference identity projection validation fails deterministically for missing columns/bindings.
  - lineage-anchor plans exactly cover finalized used variants, including concrete and abstract targets, and fail on a
    missing/duplicate/null or mismatched target/lineage result shape.
- When fixture-based artifacts are emitted, `mappingset.manifest.json` includes stable descriptor SQL hashes plus every
  lineage plan's target/`AnchorSetId`, set-input kind/name, batch limit, normalized SQL hash, target-id ordinal, and
  ordered lineage result ids/ordinals, enabling golden comparisons per `ddl-generator-testing.md`.

## Tasks

1. Compile descriptor projection plans from `RelationalResourceModel.DescriptorEdgeSources`, producing stable, canonicalized SQL (page-batched and deterministically ordered).
2. Compile and/or validate reference identity projection metadata from `RelationalResourceModel.DocumentReferenceBindings` and ensure required columns are present in `TableReadPlan` select lists.
3. Compile global `LineageAnchorResolutionPlan` values from the complete artifact, with deterministic provider set-input,
   batching, SQL, and result-ordinal metadata.
4. Add unit tests for deterministic output and model reference integrity (pgsql + mssql).
5. Add (or extend) a small fixture that exercises descriptor projection, reference identity projection, and concrete plus
   abstract demanded-lineage projection and validates output via `mappingset.manifest.json` golden comparisons.
