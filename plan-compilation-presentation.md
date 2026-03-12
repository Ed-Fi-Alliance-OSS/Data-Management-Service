# Plan Compilation

## Runtime Plan Compilation + Caching

Branch and design context:
- `reference/design/backend-redesign/epics/15-plan-compilation`
- Primary implementation: `src/dms/backend/EdFi.DataManagementService.Backend.Plans`

---

## 1. Problem We Needed To Solve

- DMS needed a shared layer that compiles executor-ready plans from the derived relational model.
- Those plans needed to be deterministic so runtime fallback and future AOT packs could use the same shapes.
- Runtime execution needed explicit bindings and metadata instead of inferring behavior by parsing SQL text.

Key outcome:
- compile once for a schema/dialect/version,
- cache the result,
- reuse it everywhere in the request path.

---

## 2. What Was Delivered

- Canonical SQL generation for plan and query SQL.
- Stable plan contracts for write, read, and projection execution.
- Full relational write-plan compilation across root, child, collection, and extension tables.
- Hydration read-plan compilation for all tables in dependency order.
- Projection plan compilation for reference identity metadata and descriptor URI lookup plans.
- Process-local mapping set cache with compile-once concurrency behavior.
- Runtime PostgreSQL startup wiring that compiles and validates the active mapping set.

---

## 3. Core Architecture

Main compile target:
- `MappingSet`

Selection key:
- `EffectiveSchemaHash`
- `Dialect`
- `RelationalMappingVersion`

Compile flow:
1. Build the derived relational model set.
2. Compile per-resource write plans.
3. Compile per-resource read and projection plans.
4. Freeze the compiled plans into a `MappingSet`.
5. Cache and reuse by key.

Primary code:
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/MappingSetCompiler.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/MappingSetCache.cs`

---

## 4. Determinism Foundations

- Shared SQL writer enforces canonical output.
- Parameter names are stable and derived from model elements.
- SQL aliases are deterministic.
- Bulk insert batching is deterministic and dialect-aware.
- Projection metadata uses explicit ordering instead of dictionary iteration or SQL parsing.

Why this matters:
- repeatable runtime behavior,
- stable fixture outputs,
- clean future path to AOT pack encoding and decoding.

Primary code:
- `PlanSqlWriterExtensions.cs`
- `PlanNamingConventions.cs`
- `PlanWriteBatchingConventions.cs`
- `PageDocumentIdSqlCompiler.cs`

---

## 5. Write Plan Compilation

- Every relational table gets an `InsertSql`.
- 1:1 tables can also get `UpdateSql`.
- Non-root tables get `DeleteByParentSql` for replace semantics.
- Column bindings are emitted in authoritative order with explicit value sources.
- Key unification is compiled as precompute metadata, not ad hoc runtime behavior.

Important behaviors:
- child collections use delete-then-bulk-insert semantics,
- extension tables follow the same model,
- unified alias columns are rejected from direct writes,
- precomputed bindings must have exactly one producer.

Primary code:
- `WritePlanCompiler.cs`
- `KeyUnificationWritePlanCompiler.cs`

---

## 6. Read And Projection Compilation

- Hydration SQL is compiled per table in dependency order.
- Each read plan joins against a materialized keyset of `DocumentId` values.
- `ORDER BY` is deterministic and aligned to table keys.
- Reference identity projection is compiled as ordinal metadata over hydration rows.
- Descriptor projection is compiled as a page-batched lookup plan, avoiding per-row joins.

Primary code:
- `ReadPlanCompiler.cs`
- `ReferenceIdentityProjectionPlanCompiler.cs`
- `DescriptorProjectionPlanCompiler.cs`
- `ReadPlanProjectionContractValidator.cs`

---

## 7. Runtime Integration

- Runtime PostgreSQL wiring now compiles the active mapping set from the current effective schema.
- Mapping sets are cached per process.
- Concurrent callers share one in-flight compilation.
- Startup logs whether the mapping set was `Compiled`, `Joined in-flight`, or `Reused completed`.
- Startup validation then checks loaded instances against the compiled mapping set.

Primary code:
- `src/dms/backend/EdFi.DataManagementService.Old.Postgresql/Startup/PostgresqlRuntimeMappingSetCompiler.cs`
- `src/dms/backend/EdFi.DataManagementService.Old.Postgresql/Startup/PostgresqlRuntimeMappingSetAccessor.cs`
- `src/dms/backend/EdFi.DataManagementService.Old.Postgresql/Startup/PostgresqlBackendMappingInitializer.cs`

---

## 8. Evidence That It Works

- Golden fixture coverage for canonical SQL output.
- Golden fixture coverage for runtime plan manifests.
- Determinism tests verify repeated compiles and reordered inputs produce the same outputs.
- Cache tests verify compile-once behavior under concurrency.
- Read and write compiler tests cover key behaviors, projection metadata, batching, and key unification.
- Authoritative DS 5.2 fixture shows the approach scales beyond minimal samples.

Representative tests:
- `PlanSqlFoundationsGoldenFixtureTests.cs`
- `RuntimePlanCompilationDeterminismTests.cs`
- `MappingSetCacheTests.cs`
- `ReadPlanCompilerTests.cs`
- `Given_WritePlanCompiler_KeyUnification.cs`
- `ProjectionRuntimePlanCompilationGoldenFixtureTests.cs`
- `AuthoritativeDs52RuntimePlanCompilationGoldenFixtureTests.cs`

---

## 9. Suggested Demo Flow

1. Start with the problem: why plan compilation exists.
2. Show `MappingSetCompiler` and explain the compile target and cache key.
3. Show one write-plan example with ordered bindings, `InsertSql`, `DeleteByParentSql`, and key unification metadata.
4. Show one read-plan example with hydration SQL, reference identity metadata, and the descriptor projection plan.
5. Show `MappingSetCache` and the runtime PostgreSQL initializer.
6. Close on golden tests and determinism evidence.

Target length:
- `8-10` minutes

---

## 10. Scope Boundaries To Mention

- This epic established the shared runtime compilation layer and plan contracts.
- AOT mapping-pack decode is not implemented yet.
- Authorization objects remained out of scope.
- The runtime wiring reviewed here is on the PostgreSQL path.

---

## Closing Summary

- We now have a deterministic plan-compilation layer for DMS.
- The system compiles write, read, and projection plans from the relational model instead of inferring behavior at runtime.
- The compiled output is cacheable, testable, and structured for reuse by both runtime compilation and future AOT scenarios.
