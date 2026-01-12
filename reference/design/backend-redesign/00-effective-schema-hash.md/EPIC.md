# Epic: Effective Schema Fingerprinting & `dms.ResourceKey` Seeding

## Description

Define and implement the deterministic “effective schema” contract used by the DDL/Schema generator (and any future mapping-pack/runtime gates):

- Load an explicit set of `ApiSchema.json` inputs (core + extensions).
- Strip OpenAPI payloads from the hashed surface.
- Canonicalize JSON deterministically and compute `EffectiveSchemaHash`.
- Derive deterministic `dms.ResourceKey` seed rows (including abstract resources).
- Emit `effective-schema.manifest.json` for fixture-based testing and diagnostics.

Authorization objects remain out of scope.

## Stories

- `00-schema-loader.md` — Load/normalize ApiSchema inputs (core + extensions)
- `01-canonical-json.md` — Deterministic canonical JSON serialization
- `02-effective-schema-hash.md` — `EffectiveSchemaHash` calculation + tests
- `03-resourcekey-seed.md` — Deterministic `dms.ResourceKey` seed mapping + hash
- `04-effective-schema-manifest.md` — Emit `effective-schema.manifest.json` (stable)

