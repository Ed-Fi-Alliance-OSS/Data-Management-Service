---
jira: DMS-922
jira_url: https://edfi.atlassian.net/browse/DMS-922
---

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

- `DMS-923` — `00-schema-loader.md` — Load/normalize ApiSchema inputs (core + extensions)
- `DMS-924` — `01-canonical-json.md` — Deterministic canonical JSON serialization
- `DMS-925` — `02-effective-schema-hash.md` — `EffectiveSchemaHash` calculation + tests
- `DMS-926` — `03-resourcekey-seed.md` — Deterministic `dms.ResourceKey` seed mapping + hash
- `DMS-927` — `04-effective-schema-manifest.md` — Emit `effective-schema.manifest.json` (stable)
- `DMS-947` — `05-startup-schema-and-mapping-init.md` — Startup-time ApiSchema + mapping initialization
