# Story: Load and Normalize `ApiSchema.json` Inputs

## Description

Implement a deterministic loader for the “effective schema set” (core + extensions) as an explicit list of `ApiSchema.json` files. The loader produces an in-memory representation suitable for hashing, `dms.ResourceKey` seeding, and relational model derivation.

Key behaviors:
- Input list is explicit (fixture-driven and CLI-driven), not filesystem-enumeration driven.
- OpenAPI payloads are stripped from the surface used for hashing/model derivation.
- Fail fast when inputs are invalid or inconsistent (e.g., mismatched `apiSchemaVersion`).

## Acceptance Criteria

- Given an explicit ordered list of `ApiSchema.json` files, the loader deterministically returns the same ordered project list (sorted by `ProjectEndpointName` ordinal) regardless of input file ordering.
- Loader strips OpenAPI payload sections before passing data to hashing/model derivation:
  - `projectSchema.openApiBaseDocuments`
  - `projectSchema.resourceSchemas[*].openApiFragments`
  - `projectSchema.abstractResources[*].openApiFragment`
- Loader fails fast with actionable errors when:
  - any file is missing `projectSchema` or contains multiple `projectSchema` roots,
  - `apiSchemaVersion` differs across files,
  - `projectSchema.projectEndpointName` collisions occur after normalization.
- No authorization/`auth.*` inputs are required or assumed.

## Tasks

1. Add an “effective schema set” loader component (library-first) that takes an explicit list of file paths/streams.
2. Implement OpenAPI payload stripping (`removeOpenApiPayloads(...)`) exactly per `reference/design/backend-redesign/data-model.md`.
3. Validate and normalize `ProjectEndpointName` and `ProjectName` values needed by downstream stages.
4. Add unit tests covering:
   1. file ordering independence,
   2. OpenAPI payload exclusion,
   3. mismatch/fail-fast paths.
5. Wire the loader into the generator entry points used by CLI and test harness (no divergent implementations).

