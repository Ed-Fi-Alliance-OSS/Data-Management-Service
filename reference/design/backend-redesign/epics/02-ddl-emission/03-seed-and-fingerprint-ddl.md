---
jira: DMS-939
jira_url: https://edfi.atlassian.net/browse/DMS-939
---

# Story: Emit Seed + Fingerprint Recording SQL (Insert-if-Missing + Validate)

## Description

Emit deterministic seed/recording DML as part of provisioning scripts:

- `dms.ResourceKey` seed inserts with explicit `ResourceKeyId` values.
- Singleton `dms.DataStoreIdentity` insert-if-absent initialization with a database-generated
  random `SourceIdentity`.
- Singleton `dms.EffectiveSchema` insert-if-missing with:
  - `ApiSchemaFormatVersion`
  - `EffectiveSchemaHash`
  - `ResourceKeyCount`
  - `ResourceKeySeedHash`
- `dms.SchemaComponent` inserts for the current `EffectiveSchemaHash`.

The generated SQL must validate expected seed sets and fail fast on mismatches (no migrations).

## Acceptance Criteria

- Generated SQL uses insert-if-missing semantics (no truncate) and validates:
  - `dms.DataStoreIdentity` contains exactly the singleton row with a valid UUID,
  - `dms.ResourceKey` contents match expected exactly,
  - `dms.SchemaComponent` contents match expected exactly.
- Provisioning fails fast if `dms.EffectiveSchema` exists with a different `EffectiveSchemaHash`.
- Re-running the same provisioning script completes successfully and does not change recorded fingerprints.
- Re-running the same provisioning script preserves `SourceIdentity`; independently
  provisioned databases receive different UUIDs while emitted SQL text remains deterministic.
- Negative tests exist for:
  - mismatched `EffectiveSchemaHash`,
  - tampered `dms.ResourceKey` contents.

## Tasks

1. Implement dialect-specific insert-if-absent `dms.DataStoreIdentity` initialization and
   singleton/UUID validation.
2. Implement dialect-specific DML emission for `dms.ResourceKey` seeding and full-table validation.
3. Implement `dms.EffectiveSchema` insert-if-missing and mismatch preflight logic per `ddl-generation.md`.
4. Implement `dms.SchemaComponent` insert-if-missing and exact-match validation.
5. Add unit/snapshot tests validating:
   1. deterministic emitted SQL text,
   2. presence of validation logic,
   3. fail-fast mismatch paths (at least at the SQL generation level).
