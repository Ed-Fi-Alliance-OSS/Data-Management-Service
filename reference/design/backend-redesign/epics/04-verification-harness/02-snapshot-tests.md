# Story: Snapshot Tests for Small Fixtures (No DB)

## Description

Implement snapshot tests for small fixtures that compare deterministic outputs:

- `effective-schema.manifest.json`
- `relational-model.manifest.json`
- `pgsql.sql` and `mssql.sql`

Use normalized outputs and a consistent “update snapshots” workflow.

## Acceptance Criteria

- Snapshot tests compare normalized outputs byte-for-byte (post-normalization).
- At least these small fixture types exist (per `ddl-generator-testing.md` taxonomy):
  - minimal
  - nested collections with nested reference
  - polymorphic (abstract view)
  - ext (root + collection `_ext`)
  - naming-stress
- Developers can intentionally update snapshots via a documented env var or switch.

## Tasks

1. Add small fixtures under `Fixtures/small/*` with `inputs/`, `fixture.json`, and `expected/`.
2. Add snapshot tests (Snapshooter or repo-standard equivalent) for each dialect output and manifests.
3. Implement/standardize output normalization used by snapshots (line endings, whitespace trimming).
4. Add “update snapshots” mode and document it.

