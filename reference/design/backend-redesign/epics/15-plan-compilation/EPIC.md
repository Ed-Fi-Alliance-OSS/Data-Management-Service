---
jira: DMS-1027
jira_url: https://edfi.atlassian.net/browse/DMS-1027
---


# Epic: Runtime Plan Compilation + Caching (Shared with AOT Packs)

## Description

Own the required “plan compilation” layer described in `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Compile dialect-specific SQL plans (read/write/projection) and deterministic binding metadata from the derived relational model.
- Ensure write/read plan metadata carries authoritative ordering that runtime executors can reuse for persisted-row comparison during guarded no-op updates.
- Cache compiled plans/mapping sets in-process keyed by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)` so runtime requests do not recompile.
- Provide a single shared implementation used by both:
  - runtime compilation fallback (when packs are disabled or missing), and
  - mapping pack builders (optional AOT).

The shared in-memory target shape is `MappingSet` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`).

Authorization objects remain out of scope.

## Stories

- `DMS-1043` — `01-plan-sql-foundations.md` — Plan SQL foundations (shared canonical writer + dialect helpers)
- `DMS-1044` — `02-plan-contracts-and-deterministic-bindings.md` — Plan contracts + deterministic bindings (parameter naming, ordering, compare metadata)
- `DMS-1028` — `03-thin-slice-runtime-plan-compilation-and-cache.md` — Thin slice: runtime plan compilation + caching (root-only)
- `DMS-1045` — `04-write-plan-compiler-collections-and-extensions.md` — Write-plan compilation for child/extension tables (initial replace-semantics baseline)
- `DMS-1108` — `04b-stable-collection-merge-plans.md` — Retrofit write plans for stable-identity collection merge semantics
- `DMS-1046` — `05-read-plan-compiler-hydration.md` — Read/hydration plan compilation (`SelectByKeysetSql`) for all tables
- `DMS-1047` — `06-projection-plan-compilers.md` — Projection plan compilation (reference identity + descriptor URI)
- `DMS-1109` — `startup-failure-status-surfacing.md` — Startup failure status surfacing
