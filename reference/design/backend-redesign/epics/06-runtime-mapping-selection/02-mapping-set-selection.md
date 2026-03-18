---
jira: DMS-977
jira_url: https://edfi.atlassian.net/browse/DMS-977
---

# Story: Select Mapping Set by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`

## Description

Implement mapping set selection based on the database’s `EffectiveSchemaHash`:

- Determine the runtime dialect (PGSQL vs MSSQL).
- Select a matching mapping set using:
  - `.mpack` loading when enabled (see `reference/design/backend-redesign/design-docs/aot-compilation.md` and `reference/design/backend-redesign/design-docs/mpack-format-v1.md`), or
  - runtime compilation fallback when allowed.
- Cache mapping sets by selection key to avoid repeat compilation/decoding.

The mapping set returned by this story is the unified `MappingSet` shape described in `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`.

Runtime compilation fallback (when enabled) uses the shared plan compiler + cache owned by `reference/design/backend-redesign/epics/15-plan-compilation/EPIC.md`.

## Acceptance Criteria

- Selection key includes:
  - `EffectiveSchemaHash`
  - dialect
  - `RelationalMappingVersion`
  - and validates `PackFormatVersion` when using packs.
- When mapping packs are enabled and required:
  - missing or invalid pack causes requests for that DB to fail fast.
- When runtime compilation fallback is allowed:
  - missing pack triggers runtime compilation for that schema hash.
- Mapping set selection is cached and concurrency-safe (multiple requests for same hash do not compile/decode repeatedly).

## Implementation Tasks

### Task 1: Define provider interfaces in `Backend.External`

Define the provider abstractions that coordinate mapping set selection. All interfaces go in
`src/dms/backend/EdFi.DataManagementService.Backend.External/` alongside the existing
`MappingSetContracts.cs`.

- **`IMappingSetProvider`** — main selection abstraction.
  - `Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken ct)`
  - Consumers call this with a selection key; the provider handles pack loading, runtime
    compilation fallback, and caching transparently.

- **`IMappingPackStore`** — pack loading contract (stubbed in this story; real implementation
  deferred to DMS-968).
  - `Task<object?> TryLoadPayloadAsync(MappingSetKey key, CancellationToken ct)`
  - Returns `null` when no pack is available for the key. The return type is `object?` as a
    placeholder until the protobuf contracts package
    (`EdFi.DataManagementService.MappingPacks.Contracts`) is created; DMS-968 will narrow this
    to `MappingPackPayload`.

- **`IRuntimeMappingSetCompiler`** — dialect-neutral runtime compilation contract.
  - `Task<MappingSet> CompileAsync(MappingSetKey expectedKey, CancellationToken ct)`
  - `MappingSetKey GetCurrentKey()`
  - Implementations are dialect-specific (one per `SqlDialect`), parameterized by
    `ISqlDialectRules`.

- **`MappingSetProviderOptions`** — minimal configuration POCO.
  - `bool PacksEnabled` (default `false`)
  - `bool PacksRequired` (default `false`)
  - `string? PackRootPath` (default `null`)
  - `bool AllowRuntimeCompileFallback` (default `true`)
  - DMS-978 will add the full configuration surface (appsettings binding, environment
    variables, validation). This task provides sensible defaults for compile-only mode.

### Task 2: Implement `MappingSetProvider` in `Backend.Plans`

Implement the core coordinator in
`src/dms/backend/EdFi.DataManagementService.Backend.Plans/`:

1. Check `MappingSetProviderOptions`:
   - If `PacksEnabled`, call `IMappingPackStore.TryLoadPayloadAsync(key)`.
   - If pack found → validate envelope fields, decode payload, construct `MappingSet` via
     `MappingSet.FromPayload(...)` (placeholder; real decode deferred to DMS-968).
   - If pack missing and `AllowRuntimeCompileFallback` → delegate to
     `IRuntimeMappingSetCompiler.CompileAsync(key)`.
   - If pack missing and `PacksRequired` → throw with actionable error (DB hash, expected
     dialect/version, "pack required but not found").
2. All results flow through the existing `MappingSetCache` for single-flight,
   concurrency-safe caching.
3. Log selection outcome at `Information` level: which path was taken (pack loaded, runtime
   compiled, cache hit) and the `MappingSetKey` fields.

### Task 3: Implement `NoOpMappingPackStore` stub

Create a stub `IMappingPackStore` implementation in `Backend.Plans` that always returns
`null`. This is the placeholder until DMS-968 (Pack Loader/Validator) delivers the real
file-based store.

Register as the default `IMappingPackStore` in DI.

### Task 4: Generalize runtime compilation (dialect-neutral)

Extract the compilation logic from
`Old.Postgresql/Startup/PostgresqlRuntimeMappingSetCompiler` into a generic
`RuntimeMappingSetCompiler` in `Backend.Plans` that:

- Takes `IEffectiveSchemaSetProvider`, `MappingSetCompiler`, `SqlDialect`, and
  `ISqlDialectRules` as constructor dependencies.
- Implements `IRuntimeMappingSetCompiler`.
- Performs `EffectiveSchemaSet` cloning, `DerivedRelationalModelSetBuilder` invocation, and
  `MappingSetCompiler.Compile()` — the same logic currently in
  `PostgresqlRuntimeMappingSetCompiler.CompileAsync()`.
- Validates that the resolved `MappingSetKey` matches the expected key (same guard as the
  existing Pgsql compiler).

### Task 5: Add MSSQL runtime compilation support

- Verify or create `MssqlDialectRules` (implementing `ISqlDialectRules`).
- Register an MSSQL `IRuntimeMappingSetCompiler` instance parameterized with
  `SqlDialect.Mssql` and `MssqlDialectRules`.
- Ensure the `MappingSetProvider` routes to the correct compiler based on
  `MappingSetKey.Dialect`.
- If a `Backend.Mssql` project already provides DI registration, add the MSSQL compiler
  registration there; otherwise add it alongside the PostgreSQL registration and gate on
  configured dialect.

### Task 6: Refactor `Old.Postgresql` to delegate to generic provider

- Remove `PostgresqlRuntimeMappingSetCompiler` and `PostgresqlRuntimeMappingSetAccessor`
  (their logic now lives in the generic `RuntimeMappingSetCompiler` and
  `MappingSetProvider`).
- Update `PostgresqlBackendMappingInitializer` to call
  `IMappingSetProvider.GetOrCreateAsync()` instead of directly wiring the Pgsql-specific
  compiler to the cache.
- Update DI registrations in `PostgresqlServiceExtensions`:
  - Register the generic `RuntimeMappingSetCompiler` for `SqlDialect.Pgsql` +
    `PgsqlDialectRules` as the `IRuntimeMappingSetCompiler`.
  - Register `MappingSetProvider` as `IMappingSetProvider`.
  - Remove registrations for the deleted Pgsql-specific classes.
- Adapt existing tests in `PostgresqlRuntimeMappingInitializationTests` to use the new
  generic types. Existing test scenarios (compile on first call, cache hit on second) must
  continue to pass.

### Task 7: Add `MappingSet` to `RequestInfo` and create `ResolveMappingSetMiddleware`

- Add `MappingSet? MappingSet` property to `RequestInfo` (in
  `Core/Pipeline/RequestInfo.cs`).
- Create `ResolveMappingSetMiddleware` in `Core/Middleware/`:
  1. Guard: if `!appSettings.UseRelationalBackend`, call `next()` immediately (no-op).
  2. Read `requestInfo.DatabaseFingerprint.EffectiveSchemaHash` (attached by prior
     middleware).
  3. Construct `MappingSetKey(hash, configuredDialect, RelationalMappingVersion)`.
     - `configuredDialect` comes from `IEffectiveSchemaSetProvider` or app configuration
       (same source the startup initializer uses).
     - `RelationalMappingVersion` from `IEffectiveSchemaSetProvider.EffectiveSchemaSet
       .EffectiveSchema.RelationalMappingVersion`.
  4. Call `IMappingSetProvider.GetOrCreateAsync(key)` — cache hit in steady state.
  5. Attach result to `requestInfo.MappingSet`.
  6. On failure: return 503 with remediation guidance (same pattern as fingerprint/seed
     middleware).
- Insert into pipeline after `ValidateResourceKeySeedMiddleware` in `ApiService.cs`.

### Task 8: Unit tests

Add tests in the appropriate `*.Tests.Unit` projects:

1. **`MappingSetProvider` tests** (`Backend.Plans.Tests.Unit`):
   - Pack found → returns pack-decoded mapping set (stubbed).
   - Pack missing + `AllowRuntimeCompileFallback=true` → delegates to runtime compiler.
   - Pack missing + `PacksRequired=true` → throws with actionable error.
   - `PacksEnabled=false` → skips pack store, goes straight to runtime compiler.

2. **Cache concurrency tests** (`Backend.Plans.Tests.Unit`):
   - Multiple concurrent `GetOrCreateAsync` calls for same key → single compilation
     invocation.
   - Verify `MappingSetCacheStatus` values (`Compiled`, `JoinedInFlight`,
     `ReusedCompleted`).
   - Failed compilation evicts cache entry; next call retries.

3. **`ResolveMappingSetMiddleware` tests** (`Core.Tests.Unit`):
   - Happy path: fingerprint present → resolves mapping set → attaches to `RequestInfo`.
   - `UseRelationalBackend=false` → no-op, `next()` called, `MappingSet` remains null.
   - Missing `DatabaseFingerprint` (upstream middleware already short-circuited or is disabled) → no-op, `next()` called, `MappingSet` remains null.
   - Provider failure → 503 with actionable error.

4. **`RuntimeMappingSetCompiler` tests** (`Backend.Plans.Tests.Unit`):
   - Compiles successfully for Pgsql dialect.
   - Compiles successfully for Mssql dialect.
   - Key mismatch between expected and resolved → throws `InvalidOperationException`.
