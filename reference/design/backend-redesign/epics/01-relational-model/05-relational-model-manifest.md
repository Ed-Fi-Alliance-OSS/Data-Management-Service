# Story: Emit `relational-model.manifest.json`

## Description

Emit a deterministic, comparable `relational-model.manifest.json` describing the derived relational model inventory used for:

- DDL generation,
- compiled-plan generation (future),
- and verification harness comparisons.

The manifest is a *semantic* representation (not engine introspection) and must be stable across runs.

## Acceptance Criteria

- Manifest includes, at minimum, stable inventories for:
  - schemas,
  - tables (scope + name),
  - columns (name, type metadata, nullability, key participation),
  - constraints (PK/UK/FK/CHECK where applicable),
  - indexes (including FK-supporting indexes),
  - views (abstract union views),
  - triggers inventory required by update tracking (if modeled here; otherwise documented as dialect-generated artifacts).
- Output is byte-for-byte stable for the same inputs (stable ordering + `\n` line endings).
- Small fixture snapshot tests compare the manifest exactly.

## Tasks

1. Define a stable manifest schema for the derived model (properties and ordering).
2. Implement deterministic ordering rules matching `reference/design/backend-redesign/ddl-generation.md`.
3. Emit `relational-model.manifest.json` for fixtures via a shared artifact emitter.
4. Add snapshot/golden tests for at least one small fixture validating exact output.

