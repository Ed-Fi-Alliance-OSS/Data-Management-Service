# Story: Validate `dms.ResourceKey` Seed Mapping (Fast + Slow Path)

## Description

Validate that the database’s `dms.ResourceKey` contents match the mapping set’s expected seed mapping for the DB’s `EffectiveSchemaHash`.

Per `reference/design/backend-redesign/ddl-generation.md` and `reference/design/backend-redesign/mpack-format-v1.md`:

- Fast path: compare `ResourceKeyCount` and `ResourceKeySeedHash` from `dms.EffectiveSchema`.
- Slow path: on mismatch, read `dms.ResourceKey` ordered by `ResourceKeyId` and diff against the expected seed list for diagnostics.

## Acceptance Criteria

- When `ResourceKeyCount` and `ResourceKeySeedHash` match expected, validation succeeds without reading the full `dms.ResourceKey` table.
- When the fast path mismatches, validation performs a slow-path diff and fails fast with a diagnostic that includes:
  - missing rows,
  - extra rows,
  - and mismatched `(ResourceKeyId, ProjectName, ResourceName, ResourceVersion)` rows.
- Validation results are cached per connection string and invalidated only when the connection string changes.

## Tasks

1. Implement a `ResourceKeyValidator` that takes:
   - mapping set expected seed summary and list,
   - DB fingerprint from `dms.EffectiveSchema`.
2. Implement dialect-specific slow-path reads of `dms.ResourceKey` ordered by `ResourceKeyId`.
3. Implement a deterministic diff report format suitable for logs and test assertions.
4. Add unit tests covering:
   1. fast-path success,
   2. slow-path mismatch reporting,
   3. caching behavior.

