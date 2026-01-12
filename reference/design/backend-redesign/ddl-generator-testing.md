# Backend Redesign: DDL Generator Verification Harness (DDL + AOT Packs)

## Status

Draft.

## Purpose

Define an end-to-end verification harness for the new relational-primary-store DDL generator and the optional AOT mapping-pack builder:

- determinism (same inputs → same outputs),
- cross-engine correctness (PostgreSQL + SQL Server),
- and runtime compatibility gates (a provisioned DB selects/validates the matching mapping pack by `EffectiveSchemaHash`).

Authorization-related objects are explicitly out of scope for this harness until the authorization design is finalized.

## Design principles

- **Layered tests**: fast unit/contract tests catch most issues; DB-apply and “runtime selection” tests catch integration issues.
- **Deterministic artifacts**: normalize outputs (line endings, whitespace) and compare exact text/structure.
- **Authoritative fixtures**: treat some outputs as a contract (“goldens”), updated only intentionally.
- **No Testcontainers**: DB-apply tests use docker compose (consistent with repo guidance).

## Artifacts and fixtures (normative)

This harness only works if it has concrete, checked-in **inputs** and stable, comparable **outputs**. This section defines the on-disk layout and the exact filenames that tests compare.

### Fixture directory layout

Fixtures are checked in as directories with this structure:

```text
{TestProjectRoot}/
  Fixtures/
    small/
      minimal/
        inputs/
          ... ApiSchema.json files ...
        fixture.json
        expected/
          effective-schema.manifest.json
          relational-model.manifest.json
          pgsql.sql
          mssql.sql
          pack.manifest.json
          mappingset.manifest.json
    authoritative/
      ds-core/
        inputs/
          ... ApiSchema.json files ...
        fixture.json
        expected/
          effective-schema.manifest.json
          relational-model.manifest.json
          pgsql.sql
          mssql.sql
          pack.manifest.json
          mappingset.manifest.json
```

Notes:
- Tests write outputs to an `actual/` directory next to `expected/` (or into a temp dir with the same structure) and compare `expected/` vs `actual/`.
- `actual/` MUST NOT be checked in (add a `.gitignore` entry).

### `fixture.json` (normative)

Each fixture includes a `fixture.json` that declares **exactly** which `ApiSchema.json` files participate in the effective schema set (do not rely on filesystem enumeration order).

Minimal schema (field names are normative; extra fields are allowed for future expansion):

```json
{
  "apiSchemaFiles": [
    "edfi/ApiSchema.json",
    "tpdm/ApiSchema.json"
  ],
  "dialects": ["pgsql", "mssql"],
  "buildMappingPack": true
}
```

Semantics:
- `apiSchemaFiles`: relative paths under `inputs/` in the exact order listed; the harness still canonicalizes/sorts for hashing per `data-model.md`, but this makes the fixture explicit and debuggable.
- `dialects`: which dialect outputs must be produced and compared.
- `buildMappingPack`: when `true`, the harness MUST also produce and compare `pack.manifest.json` and `mappingset.manifest.json`.

### Standard outputs (normative filenames)

Every fixture’s `expected/` directory uses consistent filenames. Some outputs are conditional based on `fixture.json`:

Always required:

- `effective-schema.manifest.json`: schema fingerprint + schema components + deterministic `dms.ResourceKey` seed mapping summary (and optionally the full seed list).
- `relational-model.manifest.json`: derived relational model summary (schemas/tables/columns/constraints/indexes/views/triggers) used to generate DDL and compile plans.
- `{dialect}.sql`: normalized DDL text, where `{dialect}` is `pgsql` or `mssql` (one file per dialect listed in `fixture.json.dialects`).

Optional (recommended for diagnostics and fast diffs):

- `ddl.manifest.json`: stable per-dialect DDL summary (normalized SQL hashes and statement counts).

Required when `fixture.json.buildMappingPack=true`:

- `pack.manifest.json`: semantic manifest derived from *decoded protobuf payload* (never compare `.mpack` bytes).
- `mappingset.manifest.json`: semantic manifest derived from the in-memory mapping set used by runtime execution (after pack decode, or runtime compilation).

DB-apply tests add an additional expected artifact (per dialect):

- `provisioned-schema.{dialect}.manifest.json`: stable introspection manifest emitted **after** applying DDL to an empty database.

### Manifest formats (normative)

The harness compares manifests byte-for-byte after normalization. Manifests MUST be serialized deterministically (stable property ordering, stable indentation, `\n` line endings, no trailing whitespace).

Minimum required fields:

- `effective-schema.manifest.json`
  - `api_schema_format_version`
  - `relational_mapping_version`
  - `effective_schema_hash`
  - `resource_key_count`
  - `resource_key_seed_hash`
  - `schema_components[]`: `project_endpoint_name`, `project_name`, `project_version`, `is_extension_project`, `project_hash`
  - Optional (recommended): `resource_keys[]` list with `resource_key_id`, `project_name`, `resource_name`, `resource_version`
- `relational-model.manifest.json`
  - `effective_schema_hash`, `dialect`, `relational_mapping_version`
  - `resources[]` ordered by `(project_name, resource_name)` ordinal:
    - physical schema/table names
    - columns (in physical column order)
    - constraints/indexes/views/triggers (names + key columns)
- `pack.manifest.json`
  - `effective_schema_hash`, `dialect`, `relational_mapping_version`, `pack_format_version`
  - `payload_sha256`
  - `resource_key_count`, `resource_key_seed_hash`
  - `resources[]` plan summaries:
    - per plan: `normalized_sql_sha256` (store SQL hashes, not the raw SQL text for large fixtures)
    - binding order metadata required for correctness (parameter order, keyset ordering, etc.)
- `mappingset.manifest.json`
  - Same semantic shape as `pack.manifest.json` for the runtime mapping-set object graph
  - MUST match pack-derived mapping sets exactly (after normalization)
- `ddl.manifest.json` (when emitted)
  - `effective_schema_hash`, `relational_mapping_version`
  - `ddl[]` ordered by dialect:
    - `dialect`
    - `normalized_sql_sha256`
    - `statement_count`

### Artifact emitter (required component)

All test layers share one implementation that produces normalized artifacts from a fixture input set:

- Inputs: `inputs/` + `fixture.json`
- Outputs: an `actual/` directory containing the standardized files above

Design requirement:
- The CLI(s) and tests MUST reuse the same “artifact emitter” library code to avoid “tests validate something the CLI doesn’t produce” drift.

## Test layers

### 1) Contract/unit tests (fast, no DB)

Goal: lock down the rules that define “the same schema” and “the same generated objects”.

Coverage:

- **`EffectiveSchemaHash` determinism**
  - stable across:
    - file ordering,
    - JSON property ordering,
    - whitespace,
    - platform line endings,
  - and stable exclusion of OpenAPI payload sections (per `data-model.md`).
- **`dms.ResourceKey` seed determinism**
  - stable ordering rules,
  - stable `ResourceKeyId` assignment,
  - stable `ResourceKeyCount` and `ResourceKeySeedHash` manifest calculation.
- **Naming determinism**
  - identifier normalization rules,
  - max-length truncation + hash suffix rules per engine,
  - reserved words handling,
  - collision detection → fail-fast errors.
- **Type mapping determinism**
  - JSON-schema → SQL type mapping (incl. decimals, formats, max lengths),
  - nullability and required-ness rules,
  - consistent behavior across dialects (differences only where explicitly intended).
- **Negative-path contracts**
  - unknown `nameOverrides` keys fail fast,
  - ambiguous/colliding overrides fail fast,
  - schema sets with mismatched `apiSchemaVersion` fail fast,
  - effective schema too large for `ResourceKeyId` bounds fails fast.

Implementation notes:
- Use NUnit with small fixture inputs (in-memory JSON or tiny `ApiSchema.json` files).
- Favor direct comparisons of computed values and normalized “manifest strings” over large snapshots.

### 2) Snapshot tests (fast, no DB)

Goal: lock down generated output shapes for representative “small” models without bringing in large authoritative fixtures.

Artifacts snapshot-tested:

- **Generated DDL text** per dialect for small fixtures.
- **Effective schema manifest** (`effective-schema.manifest.json`) for small fixtures (hash + schema components + resource key seed summary).
- **Derived relational model manifest** (a normalized, stable text/JSON view of:
  schemas, tables, columns, constraints, indexes, views, triggers).
- **DDL manifest** (`ddl.manifest.json`) when emitted (stable per-dialect hashes and counts).
- **Mapping pack manifest** (see “AOT pack testing”): stable, human-readable output describing pack semantics.

Implementation notes:
- Snapshooter is already used in existing schema generator tests; reuse it for multi-line text artifacts.
- Always snapshot **normalized** outputs:
  - `\n` line endings,
  - trim trailing whitespace,
  - stable ordering of statements where ordering is not semantically required.

### 3) Authoritative “golden” tests (no DB, but large fixtures)

Goal: verify “real-world” end-to-end outputs match a checked-in authoritative baseline, similar to MetaEd’s authoritative compare approach (core DS alone and core+extensions combinations).

Approach:

- Create a dedicated NUnit test project (or a separate test category) such as:
  - `...DdlGenerator.Tests.Authoritative` with `[Category("Authoritative")]`.
- Use the standard fixture layout in `Fixtures/authoritative/*` (see “Artifacts and fixtures (normative)”).

- Test behavior:
  1. Run the generator against `inputs/` (prefer in-process; CLI is acceptable).
  2. Write outputs into a temp `actual/` directory.
  3. Compare `expected/` vs `actual/` using a directory diff.

Comparison mechanism (recommended):
- Use `git diff --no-index` (directory-to-directory), ignoring only CR/EOL differences:
  - `--ignore-space-at-eol --ignore-cr-at-eol`
- Fail the test with the diff output so developers can see exactly what changed.

“Bless/update goldens” mode:
- If `UPDATE_GOLDENS=1`, copy `actual/` → `expected/` and pass.
- CI never sets `UPDATE_GOLDENS`.

Practical considerations:
- Authoritative fixtures can be large; keep them scoped to a small set of “canonical combinations” that matter most.
- If file size becomes problematic, consider storing compressed inputs and expanding to temp at test runtime, but prefer checked-in plain JSON for debuggability.

### 4) DB-apply smoke tests (docker compose; pgsql + mssql)

Goal: prove the emitted DDL actually provisions an empty database on each engine and creates the expected objects.

Test shape:

- A script-first harness (so it’s usable outside tests), e.g.:
  - `eng/verify-relational-ddl.ps1` (and/or `.sh`)
- A small NUnit wrapper can call the script (optional), but the script should be runnable standalone.

Workflow per engine:
1. Start a fresh DB via docker compose (separate compose files or profiles for pgsql/mssql).
2. Provision the database using the generated DDL:
   - PostgreSQL: `psql -v ON_ERROR_STOP=1 -f ...`
   - SQL Server: `sqlcmd -b -i ...`
   - Recommended additional check: run the **same** DDL a second time and assert it succeeds and the provisioned schema manifest is unchanged (validates existence-check patterns and insert-if-missing seed semantics).
3. Run a minimal journaling smoke check (required because journaling triggers are correctness-critical):
   - insert one row into `dms.Document` (using a seeded `ResourceKeyId`) and assert:
     - `dms.DocumentChangeEvent` has one new row for that `DocumentId`
     - `dms.IdentityChangeEvent` has one new row for that `DocumentId`
   - update `ContentVersion` and assert one new `dms.DocumentChangeEvent` row is emitted
   - update `IdentityVersion` and assert one new `dms.IdentityChangeEvent` row is emitted
4. Run engine-specific introspection queries and emit a stable **provisioned schema manifest** artifact:
   - tables, columns, types, nullability,
   - PK/UK/FK constraints,
   - indexes,
   - views,
   - triggers (required for journaling),
   - rows in `dms.EffectiveSchema`, `dms.SchemaComponent`, `dms.ResourceKey` (as applicable).
5. Compare the manifest to `expected/provisioned-schema.{dialect}.manifest.json` committed alongside the fixture.

### 5) Runtime compatibility tests (pack selection + DB validation gate)

Goal: prove the “runtime contract” the server will depend on:

- DB records `EffectiveSchemaHash` and the resource key seed fingerprint.
- Runtime selects the matching mapping set (pack or runtime compiled).
- Runtime validates that `dms.ResourceKey` matches the mapping set (fast path using `ResourceKeySeedHash/Count`).

Test workflow:
1. Provision DB using DDL (layer 4).
2. Build/load the mapping pack for the same inputs (or compile runtime mapping set).
3. Execute the validation logic:
   - read DB `EffectiveSchemaHash`,
   - load pack by `(hash, dialect, mapping version)`,
   - validate `ResourceKeySeedHash/Count`,
   - (optional) slow-path diff on mismatch for diagnostics.
4. Include a negative test:
   - tamper `dms.ResourceKey` (or the recorded seed hash) and assert validation fails with a useful mismatch report.
5. Include a generator/CLI preflight negative test (if provisioning via CLI is supported):
   - provision a DB for effective hash `A`,
   - attempt to provision a different effective hash `B` to the same DB,
   - assert the tool fails fast with a clear “hash mismatch” error (no in-place upgrade semantics).
## AOT pack testing (avoid brittle byte-for-byte checks)

Unless we explicitly guarantee deterministic protobuf + zstd output bytes, avoid comparing `.mpack` files directly.

Instead:

- Add a tool/library function that emits a **pack manifest** (stable JSON/text) containing:
  - envelope key fields (`effective_schema_hash`, `dialect`, `relational_mapping_version`, `pack_format_version`),
  - uncompressed payload SHA-256,
  - resource key list summary (`count`, `seed_hash`),
  - per-resource plan summaries (e.g., counts + SHA-256 of normalized SQL strings).
- Snapshot and/or golden-compare the manifest, not the raw bytes.

## AOT pack object-graph tests (build → decode → expected graph)

Goal: validate that a set of `ApiSchema.json` inputs produces a mapping pack that:

1. decodes successfully into the expected payload, and
2. deserializes into the expected in-memory mapping set shape used by runtime execution.

### 1) Make the payload represent a stable object graph

Pack format guidance (so tests are not brittle and comparisons are meaningful):

- Avoid protobuf `map<...>` fields in the payload where ordering matters for comparisons.
- Prefer `repeated` lists and require the producer to emit a canonical ordering, for example:
  - resources sorted by `(ProjectName, ResourceName)` (ordinal string compare),
  - per-resource tables in dependency order (root → children depth-first),
  - columns in the physical column order used by plans/bindings.

### 2) Canonical manifests for comparison

Introduce two “semantic manifests” that are deterministic and comparable:

- **`pack.manifest.json`**: derived from *decoded protobuf payload*, containing only stable semantics (not raw bytes).
- **`mappingset.manifest.json`**: derived from the in-memory mapping set used at runtime (the result of `MappingSet.FromPayload(...)`), excluding runtime-only caches and delegates.

Manifests should include:
- envelope key fields (`effective_schema_hash`, `dialect`, `relational_mapping_version`, `pack_format_version`),
- `resource_keys` list + derived `(count, seed_hash)`,
- per-resource derived model shape (tables/columns/constraints/views as applicable),
- per-resource plan summaries:
  - either normalized SQL strings, or stable hashes (preferred if SQL is large),
  - plus binding/parameter ordering metadata needed to guarantee correctness.

### 3) Golden comparison tests (authoritative expected graphs)

For each fixture set that exercises AOT packs:

1. Build the pack from the fixture `ApiSchema.json` inputs.
2. Decode the pack into a payload and emit `pack.manifest.json`.
3. Load the payload into the runtime mapping set and emit `mappingset.manifest.json`.
4. Compare each manifest to `expected/` (Snapshooter snapshots for small fixtures; authoritative directory compares for large fixtures).

### 4) Mandatory strong equivalence test (pack vs runtime compilation)

For each fixture set that exercises AOT packs, run both compilation paths and require semantic equivalence:

- Path A: `ApiSchema.json` → runtime compilation → `mappingset.manifest.json`
- Path B: `ApiSchema.json` → pack build → decode → `MappingSet.FromPayload(...)` → `mappingset.manifest.json`

Requirement:
- The manifests from Path A and Path B must match exactly (after normalization).

This equivalence test is required because it catches:
- missing/forgotten fields in pack serialization,
- ordering/binding drift between producer and consumer,
- and “pack loads but is semantically different” bugs.

## Fixture taxonomy (small → authoritative)

Small fixtures (used by unit + snapshot tests):
- `minimal`: 1 resource + 1 reference + 1 descriptor
- `nested`: nested collections + reference inside nested collection (ordinal-path binding)
- `polymorphic`: abstract + subclasses (union view)
- `ext`: `_ext` at root and in a collection; multi-extension projects
- `naming-stress`: long identifiers + overrides + reserved words + collision detection

Authoritative fixtures (real schemas):
- DS core only
- DS + TPDM
- DS + Sample
- DS + Homograph

## CI wiring (recommended)

- Default PR job: unit + snapshot tests only (fast, deterministic).
  - `dotnet test ... --filter "TestCategory!=Authoritative&TestCategory!=DbApply"`
- Separate CI jobs (or scheduled builds):
  - `Authoritative` (runs on `dms-schema` changes and nightly)
  - `DbApply` (runs on `dms-schema` changes and nightly; requires docker availability)

## Developer workflow

- Run fast checks: `dotnet test` (unit + snapshot).
- Update goldens intentionally:
  - `UPDATE_GOLDENS=1 dotnet test ... --filter TestCategory=Authoritative`
  - review diffs and commit `expected/` changes alongside the code change.

## Acceptance criteria for “harness complete”

- A new developer can:
  - run fast tests locally without docker,
  - run authoritative comparisons and see a clean diff on failure,
  - run DB-apply smoke tests for both engines with a single script,
  - validate pack ↔ DB compatibility gates and see actionable failure messages.
