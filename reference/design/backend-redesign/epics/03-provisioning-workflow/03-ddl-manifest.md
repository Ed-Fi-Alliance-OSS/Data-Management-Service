# Story: Emit `ddl.manifest.json` (Deterministic DDL Summary)

## Description

Emit a deterministic `ddl.manifest.json` artifact as described in `reference/design/backend-redesign/ddl-generator-testing.md` and referenced by `reference/design/backend-redesign/ddl-generation.md`.

The manifest provides a stable summary of emitted DDL per dialect for diagnostics and fast diffs without relying solely on large SQL snapshots.

## Acceptance Criteria

- When enabled (fixture or CLI option), `ddl.manifest.json` is emitted alongside `{dialect}.sql`.
- The manifest is deterministic (byte-for-byte stable for the same inputs) and uses `\n` line endings only.
- For each dialect emitted, the manifest includes at minimum:
  - dialect id (`pgsql`/`mssql`),
  - SHA-256 (or equivalent) of the **normalized** SQL text,
  - statement count (post-normalization, using a deterministic splitter),
  - and any additional stable metadata needed for troubleshooting (optional).
- Snapshot/golden tests validate `ddl.manifest.json` output for at least one small fixture.

## Tasks

1. Define the manifest schema and normalization rules used to compute:
   1. normalized SQL bytes/text,
   2. DDL hash,
   3. statement counting.
2. Implement emission of `ddl.manifest.json` from the same pipeline that emits `{dialect}.sql` (no duplicated logic).
3. Add unit tests that validate:
   1. determinism across runs,
   2. determinism across input ordering permutations.
4. Add snapshot/golden coverage for `ddl.manifest.json` on at least one fixture.

