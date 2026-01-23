---
jira: DMS-934
jira_url: https://edfi.atlassian.net/browse/DMS-934
---

# Story: Emit `relational-model.manifest.json`

## Description

Emit a deterministic, comparable `relational-model.manifest.json` describing the derived relational model inventory used for:

- DDL generation,
- compiled-plan generation (future),
- and verification harness comparisons.

The manifest must be emitted from the unified in-memory model (`DerivedRelationalModelSet`; see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`) so all downstream producers/consumers share the same derived inventory.

Important: index and trigger inventories are derived as “DDL intent” and embedded in `DerivedRelationalModelSet` (see `07-index-and-trigger-inventory.md`). The manifest emitter must **not** re-derive indexes/triggers independently from tables/constraints; it must serialize the shared inventories so `relational-model.manifest.json` cannot drift from DDL output.

The manifest is a *semantic* representation (not engine introspection) and must be stable across runs.

## Acceptance Criteria

- Manifest includes, at minimum, stable inventories for:
  - schemas,
  - tables (scope + name),
  - columns (name, type metadata, nullability, key participation),
  - constraints (PK/UK/FK/CHECK where applicable),
  - indexes (including FK-supporting indexes),
  - views (abstract union views),
  - triggers (derived trigger intent inventory: names + key columns).
- Output is byte-for-byte stable for the same inputs (stable ordering + `\n` line endings).
- Small fixture snapshot tests compare the manifest exactly.

## Tasks

1. Define a stable manifest schema for the derived model (properties and ordering).
2. Implement deterministic ordering rules matching `reference/design/backend-redesign/design-docs/ddl-generation.md`.
3. Emit `relational-model.manifest.json` for fixtures via a shared artifact emitter.
4. Ensure indexes/triggers are serialized from `DerivedRelationalModelSet` inventories (no divergent derivation logic).
5. Add snapshot/golden tests for at least one small fixture validating exact output.
