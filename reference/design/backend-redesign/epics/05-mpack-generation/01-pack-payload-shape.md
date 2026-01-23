---
jira: DMS-965
jira_url: https://edfi.atlassian.net/browse/DMS-965
---

# Story: Payload Object Graph + Deterministic Ordering Rules

## Description

Implement the in-memory-to-payload mapping that builds a `MappingPackPayload` from:

- the unified `MappingSet` object graph (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`),

ensuring the payload satisfies the determinism and ordering rules in `reference/design/backend-redesign/design-docs/mpack-format-v1.md`.

Note: the payload is a *runtime-required subset* of the full derived model inventory; DDL-only details remain in `DerivedRelationalModelSet` and are not required for pack execution.

## Acceptance Criteria

- Payload contains:
  - `resource_keys` entries including `resource_key_count` and `resource_key_seed_hash` semantics,
  - one `ResourcePack` per resource (sorted by `(project_name, resource_name)` ordinal),
  - per-resource model and plan structures required by the consumer.
- All repeated fields are emitted in the required canonical order (no dictionary iteration order dependence).
- Payload invariants are validated at build time:
  - unique resources by `(project_name, resource_name)`,
  - `resource_key_count == resource_keys.Count`,
  - recomputed `resource_key_seed_hash` matches the embedded value,
  - plan references (tables/columns/edge sources) exist in the embedded model.
- Unit tests lock down ordering for at least one small fixture payload.

## Tasks

1. Define a payload builder that takes a `MappingSet` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`) and emits `MappingPackPayload`.
2. Implement canonical ordering for:
   - `resource_keys`,
   - `resources`,
   - per-resource tables/columns/constraints/edge sources,
   - per-plan parameter/binding order.
3. Implement payload invariant validation per `mpack-format-v1.md` (“Consumer validation algorithm”).
4. Add unit tests that:
   1. build payload for a small fixture,
   2. assert stable ordering,
   3. assert invariant failures on intentional corruption.
