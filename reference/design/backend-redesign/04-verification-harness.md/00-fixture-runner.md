# Story: Fixture Layout + Runner (`expected/` vs `actual/`)

## Description

Implement the fixture layout and runner described in `reference/design/backend-redesign/ddl-generator-testing.md`:

- Fixtures live under a test project `Fixtures/` directory with `inputs/`, `fixture.json`, and `expected/`.
- Runner produces outputs into `actual/` (next to `expected/` or in a temp dir with the same structure).
- Runner compares outputs deterministically (byte-for-byte after normalization).

## Acceptance Criteria

- A fixture with `fixture.json` drives exactly which inputs are loaded (no filesystem enumeration).
- Runner emits standard filenames into `actual/`:
  - `effective-schema.manifest.json`
  - `relational-model.manifest.json`
  - `{dialect}.sql`
  - optional manifest files as configured
- `actual/` directories are ignored by git (via `.gitignore`).
- Failures show actionable diffs between `expected/` and `actual/`.

## Tasks

1. Create the fixture directory layout under the appropriate test project.
2. Implement `fixture.json` parsing and validation.
3. Implement a runner that:
   - loads inputs,
   - runs generator,
   - writes outputs,
   - compares expected vs actual.
4. Add `.gitignore` rules to prevent `actual/` outputs from being committed.

