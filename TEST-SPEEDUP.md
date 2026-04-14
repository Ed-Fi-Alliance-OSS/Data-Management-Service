# Relational Backend Integration Test Speedup Plan

## Summary

The PostgreSQL and SQL Server relational backend integration tests are slow primarily because many fixtures:

- create a brand-new database in `SetUp`
- apply full generated DDL before each test
- reseed data from scratch
- drop the database in `TearDown`
- are marked `NonParallelizable` even when they already use isolated databases

The largest gains will come from changing test lifecycle boundaries, not from small query-level optimizations.

## Main Problems

### 1. Per-test database provisioning

Many fixtures pay full database creation and DDL application cost for every test case.

Representative examples:

- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlReferenceResolverTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlReferenceResolverTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs`

Supporting database helpers:

- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlGeneratedDdlTestDatabase.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlGeneratedDdlTestDatabase.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlReferenceResolverTestDatabase.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlReferenceResolverTestDatabase.cs`

### 2. Serialized execution of isolated fixtures

A large portion of the suite is marked `NonParallelizable` even though the fixtures create unique database names and do not need to share state.

### 3. Rebuilding immutable fixture artifacts

Generated DDL, effective schema fixtures, and compiled mapping sets are rebuilt repeatedly:

- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlGeneratedDdlFixtureLoader.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlGeneratedDdlFixtureLoader.cs`

### 4. SQL Server reset path is expensive

PostgreSQL already has a relatively efficient reset pattern based on a single truncate command. SQL Server reset currently loops over tables multiple times to disable triggers, disable constraints, delete rows, reseed identities, restart sequences, and re-enable everything.

## Goals

- Reduce wall-clock runtime of PostgreSQL and SQL Server integration suites substantially.
- Preserve test isolation and determinism.
- Keep failures easy to reproduce locally.
- Avoid rewriting assertions unless required by the new harness.

## Non-Goals

- Replacing these tests with unit tests.
- Changing production relational behavior.
- Optimizing individual repository queries before fixing lifecycle overhead.

## Plan

## Phase 1: Move from per-test DB provisioning to per-fixture baselines

### Objective

Create the database and apply DDL once per fixture, not once per test.

### Changes

- Convert fixtures that currently provision in `SetUp` to use `OneTimeSetUp`.
- Keep per-test isolation by using `ResetAsync` plus reseeding between tests.
- For fixtures that perform one write and then assert many read-only outcomes, do the write once in `OneTimeSetUp` and keep the fixture immutable afterward.

### Initial target fixtures

- Reference resolver tests
- Generated DDL smoke tests
- Large relational write smoke tests
- Compatibility gate tests
- Fingerprint reader tests that currently provision a full database per test

### Examples to convert

- `PostgresqlReferenceResolverTests`
- `MssqlReferenceResolverTests`
- `PostgresqlGeneratedDdlFocusedSmokeTests`
- `MssqlGeneratedDdlFocusedSmokeTests`
- `PostgresqlRelationalWriteGuardedNoOpTests`
- `PostgresqlRelationalWritePostAsUpdateSmokeTests`
- `PostgresqlRelationalWriteAuthoritativeSampleSmokeTests`
- `MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests`

### Expected impact

This should produce the biggest runtime reduction because it removes the repeated create/apply/drop cycle from the hottest part of the suite.

## Phase 2: Introduce a reusable baseline database harness

### Objective

Avoid rebuilding the same schema repeatedly for fixtures that use the same effective schema and generated DDL.

### Changes

- Add a baseline concept per `(dialect, fixture path, seed profile)`.
- Provision the baseline once.
- Create disposable test databases from that baseline.

### PostgreSQL approach

Preferred options, in order:

1. Template database cloning for fixture baselines.
2. Shared per-fixture database plus fast `TRUNCATE ... RESTART IDENTITY CASCADE`.

### SQL Server approach

Preferred options, in order:

1. Database snapshot restore for fixture baselines.
2. Shared per-fixture database plus a single dynamic reset batch.

### Notes

- PostgreSQL is already close to this model for reset semantics.
- SQL Server needs a stronger baseline/reset design before it can safely become the default path.

## Phase 3: Make reset cheap enough to be the default

### PostgreSQL

- Reuse the existing truncate-based reset strategy broadly.
- Ensure every generated-DDL test database helper has a reset path, not just create/dispose.
- Standardize sequence reset and seed reload behavior.

### SQL Server

- Replace the current multi-loop reset implementation with one batched dynamic SQL reset.
- If batching is still too slow, move directly to snapshot restore.
- Keep trigger and constraint handling centralized in one helper.

### Expected impact

This is the key enabler for sharing one provisioned database per fixture.

## Phase 4: Parallelize isolated fixtures

### Objective

Use the isolation already provided by unique databases or per-fixture baselines.

### Changes

- Remove `NonParallelizable` from fixtures that no longer use shared state.
- Keep only truly shared-db tests serialized.
- Add assembly-level NUnit parallelism once the fixtures are safe for it.

### Candidates to keep isolated from the rest

- Assembly setup fixtures that touch a shared database
- Shared-schema fingerprint tests
- Any tests that intentionally mutate the same persistent database

### Expected impact

Once per-fixture setup cost is reduced, parallel execution should provide the second major runtime reduction.

## Phase 5: Cache immutable fixture artifacts in-process

### Objective

Stop reloading and recompiling the same fixture data for every test.

### Changes

- Cache `EffectiveSchemaSet` by fixture directory.
- Cache generated DDL by `(dialect, fixture directory)`.
- Cache compiled `MappingSet` by `(dialect, fixture directory)`.
- Use `Lazy<T>` or equivalent thread-safe caching so parallel execution stays safe.

### Target code

- `PostgresqlGeneratedDdlFixtureLoader`
- `MssqlGeneratedDdlFixtureLoader`
- Any repeated `new MappingSetCompiler().Compile(...)` call sites inside fixture setup

### Expected impact

Smaller than lifecycle changes, but worthwhile after Phases 1 to 4.

## Phase 6: Isolate the truly shared-database tests

### Objective

Prevent a small number of special-case tests from forcing the whole assembly to serialize.

### Changes

- Move shared-db fingerprint/setup tests into dedicated fixtures or a separate project if needed.
- Keep tests that rely on assembly-level setup away from isolated-database tests.
- Minimize use of shared connection strings that point to a single persistent integration database.

### Examples

- `DatabaseSetupFixture` in the PostgreSQL and SQL Server integration projects
- PostgreSQL fingerprint reader tests that currently touch the shared configured database

## Implementation Order

1. Convert reference resolver fixtures to one-time provision plus reset.
2. Add reset support to generated-DDL test database helpers.
3. Convert generated-DDL smoke tests to one-time provision.
4. Convert the largest write smoke fixtures with many assertions after one setup path.
5. Introduce loader and mapping-set caching.
6. Enable fixture-level parallelism.
7. Split out or isolate remaining shared-db fixtures.

## Success Metrics

- End-to-end runtime for PostgreSQL integration project before and after.
- End-to-end runtime for SQL Server integration project before and after.
- Number of fixtures still provisioning a database in `SetUp`.
- Number of fixtures still marked `NonParallelizable`.
- Number of fixture directories still rebuilding DDL and mapping sets repeatedly.

## Verification Strategy

- Measure baseline project runtime before changes.
- Convert one representative PostgreSQL fixture and one representative SQL Server fixture first.
- Re-measure after each phase instead of landing all changes at once.
- Run the full relational integration suites after each lifecycle change.
- Run a repeated loop of the converted fixtures to confirm determinism.

## Recommended First Slice

Start with the reference resolver tests.

Reasons:

- They already have explicit `ResetAsync` and `SeedAsync` concepts.
- Their lifecycle is simple and representative.
- They exist in both PostgreSQL and SQL Server projects.
- They provide a clean pattern to copy into the larger write smoke fixtures.

After that, convert the generated-DDL smoke fixtures, then the largest write smoke fixtures.
