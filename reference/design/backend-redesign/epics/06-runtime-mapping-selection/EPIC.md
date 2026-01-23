---
jira: DMS-974
jira_url: https://edfi.atlassian.net/browse/DMS-974
---


# Epic: Runtime Schema Validation & Mapping Set Selection

## Description

Implement the runtime behavior described in `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (“Schema Validation (EffectiveSchema)”), `reference/design/backend-redesign/design-docs/aot-compilation.md`, and the unified `MappingSet` shape in `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`:

- On first use of a given database connection string, read the database’s recorded schema fingerprint from `dms.EffectiveSchema` (and seed fingerprint).
- Select a matching mapping set by `EffectiveSchemaHash` (runtime-compiled or `.mpack`), keyed by dialect and `RelationalMappingVersion`.
- Validate `dms.ResourceKey` matches the mapping set (fast path via `ResourceKeySeedHash/Count`, slow-path diff for diagnostics).
- Cache per connection string and fail fast on mismatch without preventing serving other databases.

Authorization remains out of scope.

## Stories

- `DMS-975` — `00-read-effective-schema.md` — Read and cache DB fingerprint per connection string
- `DMS-976` — `01-resourcekey-validation.md` — Validate `dms.ResourceKey` seed mapping (fast + slow path)
- `DMS-977` — `02-mapping-set-selection.md` — Select mapping set by `(hash, dialect, mapping version)` (pack vs runtime compile)
- `DMS-978` — `03-config-and-failure-modes.md` — Configuration surface and fail-fast behaviors
- `DMS-979` — `04-remove-hot-reload.md` — Remove in-process schema reload/hot-reload behavior
