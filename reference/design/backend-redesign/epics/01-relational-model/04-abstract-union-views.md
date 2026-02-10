---
jira: DMS-933
jira_url: https://edfi.atlassian.net/browse/DMS-933
---

# Story: Derive Abstract Identity Table + Union View Models

## Description

Model abstract-resource artifacts per `reference/design/backend-redesign/design-docs/data-model.md` and `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`:

- Required: abstract identity tables (`{schema}.{AbstractResource}Identity`)
- Optional: abstract union views (`{schema}.{AbstractResource}_View`)

- Use `projectSchema.abstractResources[*].identityJsonPaths` order as the select-list contract.
- Determine participating concrete resources using `isSubclass`/superclass metadata.
- Handle identity rename cases for subclasses.
- Choose canonical SQL types for union columns and apply explicit casts per dialect.
- Ensure deterministic `UNION ALL` arm ordering and select-list ordering.

## Integration (ordered passes)

- Set-level (`DMS-1033`): implemented as a whole-schema pass that scans the effective schema set to discover abstract resources, their participating concrete members, and the required identity field contracts. The pass produces abstract identity-table (and optional union-view) models that other passes can reference when binding polymorphic document references.

## Acceptance Criteria

- For each abstract resource, the derived model includes a deterministic identity-table model:
  - table name `{schema}.{AbstractResource}Identity`,
  - `DocumentId` (PK; FK to `dms.Document(DocumentId)` ON DELETE CASCADE),
  - identity columns in `identityJsonPaths` order,
  - `Discriminator` column (NOT NULL; last) with value format `ProjectName:ResourceName` (fail fast if value length exceeds 256).
- When union views are enabled, the view model includes the same select-list contract:
  - `DocumentId`,
  - identity columns in `identityJsonPaths` order,
  - `Discriminator` column (NOT NULL; last) with value format `ProjectName:ResourceName`.
- `UNION ALL` arms are ordered by concrete `ResourceName` ordinal; fail fast if two participating members share the same `ResourceName` across projects.
- Each arm projects the correct concrete identity columns (including subclass rename rules).
- Model compilation fails fast if any participating concrete resource cannot supply all abstract identity fields.
- A small “polymorphic” fixture produces the expected identity-table and (when enabled) view inventory and select-list shape.

## Tasks

1. Implement abstract-resource hierarchy discovery from effective schema metadata.
2. Implement abstract identity-table model derivation:
   - identity column resolution and ordering,
   - deterministic naming and constraints.
3. Implement union view model derivation:
   - identity field resolution for each concrete resource arm (direct identity vs superclass rename mapping),
   - canonical type selection rules and model-level cast requirements.
4. Add unit tests for:
   1. arm ordering determinism,
   2. rename mapping correctness,
   3. identity-table shape and naming,
   4. fail-fast behavior when identity fields are missing.
5. Wire this derivation into the `DMS-1033` set-level builder as a whole-schema pass.
