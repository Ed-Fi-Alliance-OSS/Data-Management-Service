# Story: Derive Abstract Resource Union View Models

## Description

Model abstract resource union views (`{schema}.{AbstractResource}_View`) per `reference/design/backend-redesign/data-model.md`:

- Use `projectSchema.abstractResources[*].identityPathOrder` as the select-list contract.
- Determine participating concrete resources using `isSubclass`/superclass metadata.
- Handle identity rename cases for subclasses.
- Choose canonical SQL types for union columns and apply explicit casts per dialect.
- Ensure deterministic `UNION ALL` arm ordering and select-list ordering.

## Acceptance Criteria

- For each abstract resource, the view model includes:
  - `DocumentId`,
  - identity columns in `identityPathOrder` order,
  - optional `Discriminator` column (as specified in the design).
- `UNION ALL` arms are ordered by concrete `ResourceName` ordinal.
- Each arm projects the correct concrete identity columns (including subclass rename rules).
- Model compilation fails fast if any participating concrete resource cannot supply all abstract identity fields.
- A small “polymorphic” fixture produces the expected view inventory and select-list shape.

## Tasks

1. Implement abstract-resource hierarchy discovery from effective schema metadata.
2. Implement identity field resolution for each concrete resource arm (direct identity vs superclass rename mapping).
3. Implement canonical type selection rules and model-level cast requirements.
4. Add unit tests for:
   1. arm ordering determinism,
   2. rename mapping correctness,
   3. fail-fast behavior when identity fields are missing.

