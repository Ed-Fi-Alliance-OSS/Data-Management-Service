---
jira: DMS-969
jira_url: https://edfi.atlassian.net/browse/DMS-969
---

# Story: Pack ↔ Runtime Compilation Equivalence Tests

## Description

Add mandatory tests that prove:

- runtime compilation and AOT pack compilation produce the same semantic mapping set for the same effective schema and dialect.

This is required to catch:
- missing fields in pack serialization,
- ordering/binding drift between producer and consumer,
- and “pack loads but differs” correctness issues.

## Acceptance Criteria

- For fixtures that enable pack build (`buildMappingPack=true`):
  - Path A: runtime compile → `mappingset.manifest.json`
  - Path B: pack build → decode → `MappingSet.FromPayload(...)` → `mappingset.manifest.json` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`)
  - The manifests match exactly (after normalization).
- Tests run for both dialects configured by the fixture.
- Failures show a useful diff of the manifest mismatch.

## Tasks

1. Implement a test helper that produces `mappingset.manifest.json` from:
   - runtime compilation output, and
   - pack decode output.
2. Add at least one small fixture that enables pack build and exercises:
   - nested collections,
   - polymorphic abstract view usage,
  - `_ext` mapping,
  - abstract-target propagation-key validation records, and
  - concrete and abstract non-empty anchor variants with their exact global `LineageAnchorResolutionPlan` set-input,
    batching, SQL hash, target-id ordinal, and ordered lineage result metadata, and
  - a direct-`CycleB` certified same-statement binding policy with every PUT mutation-case plan, retained route,
     complete future vector, and canonical pre/post command/result metadata on PostgreSQL and SQL Server.
3. Add NUnit tests that compare manifests exactly for both dialects.
4. Add negative tests that intentionally drop/change the abstract target-key record, certified resolution policy, one
   mutation-case plan, a future anchor/terminal item, a route hop/action, or a pre/post command ordinal and assert pack
   validation or exact equivalence fails. Also mutate a persisted occurrence source/locator and one global lineage-plan
   target/`AnchorSetId`, set-input kind, batch limit, SQL hash, target-id ordinal, or lineage result binding and require
   failure.
