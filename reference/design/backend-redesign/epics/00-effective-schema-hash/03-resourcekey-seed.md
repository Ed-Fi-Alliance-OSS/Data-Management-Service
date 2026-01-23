---
jira: DMS-926
jira_url: https://edfi.atlassian.net/browse/DMS-926
---

# Story: Deterministic `dms.ResourceKey` Seed Mapping + Seed Hash

## Description

Derive the deterministic `dms.ResourceKey` seed set and its fingerprint from the effective schema, per:

- `reference/design/backend-redesign/design-docs/ddl-generation.md` (seeding behavior)
- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (seed-hash manifest format)

This seed mapping becomes part of the runtime contract (DB validation gate) and must be stable for a given `EffectiveSchemaHash`.

## Acceptance Criteria

- The derived seed list includes entries for:
  - every concrete `resourceSchema` (including descriptors),
  - every `projectSchema.abstractResources[*]` name.
- Seed ordering is stable and uses ordinal string comparisons.
- `ResourceKeyId` values are assigned deterministically and fit within SQL `smallint` (≤ 32767); provisioning/generation fails fast if exceeded.
- `resource_key_seed_hash` matches the algorithm in `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (manifest string v1).
- Unit tests lock down seed ordering and `resource_key_seed_hash` for at least one small fixture.

## Tasks

1. Implement a `ResourceKeySeedDeriver` that extracts and sorts seed entries from the effective schema.
2. Define deterministic `ResourceKeyId` assignment (e.g., contiguous starting at 1 in sorted order) and enforce bounds.
3. Implement `resource_key_seed_hash` computation (SHA-256 over the v1 manifest string format).
4. Add unit tests covering:
   1. inclusion rules (concrete + abstract),
   2. deterministic ordering and ids,
   3. seed-hash correctness,
   4. bound overflow failure.
