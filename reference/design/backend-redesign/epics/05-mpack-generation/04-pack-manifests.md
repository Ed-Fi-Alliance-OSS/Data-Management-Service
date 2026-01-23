---
jira: DMS-967
jira_url: https://edfi.atlassian.net/browse/DMS-967
---

# Story: Emit `pack.manifest.json` and `mappingset.manifest.json`

## Description

Implement deterministic semantic manifests for mapping packs and mapping sets to support testing and diagnostics without comparing raw `.mpack` bytes, per:

- `reference/design/backend-redesign/design-docs/ddl-generator-testing.md` (normative artifacts/filenames)
- `reference/design/backend-redesign/design-docs/aot-compilation.md` (determinism scope guidance)

Manifests are derived from:
- decoded payload (`pack.manifest.json`), and
- the in-memory mapping set used by runtime execution (`mappingset.manifest.json`; see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`).

## Acceptance Criteria

- A tool/library can:
  1. read `.mpack`,
  2. validate and decode it,
  3. emit `pack.manifest.json` containing only stable semantics (not raw bytes).
- `pack.manifest.json` includes, at minimum:
  - envelope key fields,
  - uncompressed payload SHA-256,
  - resource key `(count, seed_hash)` summary,
  - per-resource plan summaries (normalized SQL hashes preferred).
- `mappingset.manifest.json` excludes runtime-only caches/delegates and is stable across runs.
- Snapshot/golden tests compare manifests byte-for-byte for small fixtures.

## Tasks

1. Implement a pack decoder that returns a validated `MappingPackPayload` object model.
2. Implement deterministic manifest emitters for:
   - `pack.manifest.json` (from payload),
   - `mappingset.manifest.json` (from runtime mapping set).
3. Integrate manifest emission into the fixture runner when `buildMappingPack=true`.
4. Add snapshot tests for a small fixture that exercises pack build + decode + manifest emission.
