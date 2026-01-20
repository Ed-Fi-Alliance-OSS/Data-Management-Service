# Story: Remove In-Process Schema Reload / Hot Reload

## Description

Remove the legacy “schema reload/hot-reload” behavior from DMS runtime. Under the relational primary store redesign:

- DMS does not mutate or swap schemas in-process.
- DMS validates schema compatibility per database connection string using `dms.EffectiveSchema` and selects a mapping set by `EffectiveSchemaHash`.
- Schema changes are an operational concern handled via provisioning new databases/schemas (create-only), not via in-place reloads.

Align with:
- `reference/design/backend-redesign/overview.md` (“Related Changes Implied by This Redesign”)
- `reference/design/backend-redesign/transactions-and-concurrency.md` (“Schema Validation (EffectiveSchema)”)
- `reference/design/backend-redesign/ddl-generation.md` (create-only provisioning semantics)

## Acceptance Criteria

- No runtime code path attempts to “reload” or “swap” schemas for an already-running DMS process to serve an existing database.
- Any existing “reload schema” endpoints, toggles, or test-only hooks are removed (or fail fast with a clear message) so the behavior cannot be relied upon.
- Tests and local workflows no longer depend on schema reload/hot-reload; schema changes are exercised via provisioning separate databases/containers per effective schema hash.
- Documentation and error messages consistently describe the new model: validate-only + fail-fast per connection string.

## Tasks

1. Identify existing schema reload/hot-reload mechanisms in DMS (endpoints, configuration flags, test hooks, internal services).
2. Remove the mechanisms and update call sites to rely on:
   - per-connection-string `dms.EffectiveSchema` validation, and
   - mapping set selection by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`.
3. Update any tests and dev scripts that relied on hot reload to use provisioning of separate DB instances instead.
4. Add/adjust integration coverage ensuring schema mismatch fails fast and does not trigger reload attempts.
