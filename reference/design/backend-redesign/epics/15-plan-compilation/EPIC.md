---
jira: DMS-1027
jira_url: https://edfi.atlassian.net/browse/DMS-1027
---


# Epic: Runtime Plan Compilation + Caching (Shared with AOT Packs)

## Description

Own the required “plan compilation” layer described in `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Compile dialect-specific SQL plans (read/write/projection) and deterministic binding metadata from the derived relational model.
- Cache compiled plans/mapping sets in-process keyed by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)` so runtime requests do not recompile.
- Provide a single shared implementation used by both:
  - runtime compilation fallback (when packs are disabled or missing), and
  - mapping pack builders (optional AOT).

The shared in-memory target shape is `MappingSet` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`).

Authorization objects remain out of scope.

## Stories

- `DMS-1028` — `00-plan-compilation.md` — Runtime plan compilation + caching (shared)
