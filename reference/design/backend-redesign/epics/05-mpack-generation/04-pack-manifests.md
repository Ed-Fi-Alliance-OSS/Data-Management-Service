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

Both production paths originate from plan compilation over
`DerivedRelationalModelArtifact(Model, Diagnostics, ExecutorRequirements)`. Compiling from `.Model` alone is invalid
because it drops provider-finalized same-statement reference requirements.

## Acceptance Criteria

- A tool/library can:
  1. read `.mpack`,
  2. validate and decode it,
  3. emit `pack.manifest.json` containing only stable semantics (not raw bytes).
- `pack.manifest.json` includes, at minimum:
  - envelope key fields,
  - uncompressed payload SHA-256,
  - resource key `(count, seed_hash)` summary,
  - per-resource plan summaries using normalized SQL hashes, binding order, and result ordinals,
  - each table's ordered persisted occurrence identity, typed source roles/site ids, and stable locator columns,
  - every global `LineageAnchorResolutionPlan` in target/`AnchorSetId` order, including target table, provider set-input
    kind/name, batch limit, normalized SQL SHA-256, target-id ordinal, and lineage result ids/ordinals in order,
  - every `DocumentReferenceBinding`'s `DocumentReferenceResolutionPolicy`, and
  - every `SameStatementReferenceResolutionPlan` in canonical order, including:
    - its exact owning-resource/binding/site/direct-PUT-origin/complete-mutation-case key,
    - stored-target-id source, occurrence-to-stored-row semantic correlation and locator result ordinals,
    - retained changed-target route with ordered physical FK hops/actions and target-row correlation,
    - the complete propagation vector and each value's typed source (`OriginWriteBinding` plus changed-value lineage,
      locked `StoredTargetColumn`, or stored target `DocumentId`), and
    - canonical correlation and post-write-verification command input/batching contracts, normalized SQL SHA-256 values,
      and every result ordinal.
- `mappingset.manifest.json` excludes runtime-only caches/delegates, uses the same semantic shape, and is stable across
  runs. It must expose policy/plan cardinality mismatches rather than normalizing them away.
- Snapshot/golden tests compare manifests byte-for-byte for small fixtures, including a certified-cycle fixture on both
  PostgreSQL and SQL Server. Runtime-compiled and pack-decoded manifests must match exactly for every plan field above.

## Tasks

1. Implement a pack decoder that returns a validated `MappingPackPayload` object model.
2. Implement deterministic manifest emitters for:
   - `pack.manifest.json` (from payload),
   - `mappingset.manifest.json` (from runtime mapping set).
3. Integrate manifest emission into the fixture runner when `buildMappingPack=true`.
4. Add snapshot tests for a small fixture that exercises pack build + decode + manifest emission.
5. Add PostgreSQL and SQL Server certified-cycle equivalence tests that compare runtime-compiled and pack-decoded
   manifests, plus negative tests that change a resolution policy, exact plan key, route hop/action, future-value source,
   correlation/post-verification SQL hash, result ordinal, persisted occurrence source/locator, or global lineage-plan
   input/SQL/result field and prove the manifests differ.
