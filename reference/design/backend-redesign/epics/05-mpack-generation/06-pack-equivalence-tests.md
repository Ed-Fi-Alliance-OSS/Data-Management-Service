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
  - Path B: pack build → decode → `MappingSet.FromPayload(...)` → `mappingset.manifest.json`
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
   - `_ext` mapping.
3. Add NUnit tests that compare manifests exactly for both dialects.
4. Add a negative test that intentionally drops/changes a payload field and asserts the equivalence test fails (guardrail).

