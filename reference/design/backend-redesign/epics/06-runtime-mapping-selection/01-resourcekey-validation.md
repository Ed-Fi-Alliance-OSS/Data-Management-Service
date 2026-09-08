---
jira: DMS-976
jira_url: https://edfi.atlassian.net/browse/DMS-976
---

# Story: Validate `dms.ResourceKey` Seed Mapping (Fast + Slow Path)

## Description

Validate that the database’s `dms.ResourceKey` contents match the mapping set’s expected seed mapping for the DB’s `EffectiveSchemaHash`.

Per `reference/design/backend-redesign/design-docs/ddl-generation.md` and `reference/design/backend-redesign/design-docs/mpack-format-v1.md`:

- Fast path: compare smallint-bounded `ResourceKeyCount` and `ResourceKeySeedHash` from `dms.EffectiveSchema`.
- Slow path: on mismatch, read `dms.ResourceKey` ordered by `ResourceKeyId` and diff against the expected seed list for diagnostics.

## Acceptance Criteria

- When `ResourceKeyCount` and `ResourceKeySeedHash` match expected, validation succeeds without reading the full `dms.ResourceKey` table.
- When the fast path mismatches, validation performs a slow-path diff and fails fast. The diff report is logged server-side and includes:
  - missing rows (expected but not in database),
  - unexpected rows (in database but not expected),
  - and mismatched `(ResourceKeyId, ProjectName, ResourceName, ResourceVersion)` rows.
- The HTTP 503 response body contains only high-level remediation guidance (reprovisioning instructions) and a `correlationId` for log correlation — **not** the diff report itself.
- Validation results are cached per connection string and invalidated only when the connection string changes.

## Tasks

1. Implement a `ResourceKeyValidator` that takes:
   - mapping set expected seed summary and list,
   - DB fingerprint from `dms.EffectiveSchema`.
2. Implement dialect-specific slow-path reads of `dms.ResourceKey` ordered by `ResourceKeyId`.
3. Implement a deterministic diff report format suitable for server logs and test assertions. The diff report is not included in HTTP response bodies.
4. Add unit tests covering:
   1. fast-path success,
   2. slow-path mismatch reporting,
   3. caching behavior.
