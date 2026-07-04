# Single-Document Hydration Fast Path Plan

## Goal

Reduce PostgreSQL overhead in DMS Suite 3 write-volume runs by avoiding the `"page"` temp table for single-document hydration.

The current hydration batch always does this, even for `PageKeysetSpec.Single`:

```sql
DROP TABLE IF EXISTS "page";
CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY) ON COMMIT DROP;
INSERT INTO "page" ("DocumentId") VALUES (@DocumentId);
```

Then every hydration query joins to `"page"`. The 2026-07-03 performance run showed this temp-table lifecycle as a major `pg_stat_monitor` cost, while most Suite 3 write-path hydration appears to be single-document current-state or committed-representation hydration.

## Non-Goals

- Do not remove the `"page"` temp table for `PageKeysetSpec.Query` in the first implementation.
- Do not opt normal GET/read/query hydration or SQL Server hydration into the fast path in the first implementation.
- Do not change paging semantics, total-count behavior, authorization filtering, result-set order, or document reconstitution behavior.
- Do not combine multiple result-set queries into one JSON-producing SQL query.
- Do not tune `dms."Document"` delete/reference cleanup in this change. That is a separate hotspot.

## Preferred Design

Add a single-document hydration SQL path that emits direct `@DocumentId` predicates instead of materializing a keyset temp table.

For `PageKeysetSpec.Single`, the batch should start directly with document metadata:

```sql
SELECT
    d."DocumentId",
    d."DocumentUuid",
    d."ContentVersion",
    d."IdentityVersion",
    d."ContentLastModifiedAt",
    d."IdentityLastModifiedAt"
FROM "dms"."Document" d
WHERE d."DocumentId" = @DocumentId
ORDER BY d."DocumentId";
```

Table hydration should use the table's root scope locator column:

```sql
SELECT ...
FROM "edfi"."SomeTable" r
WHERE r."<RootScopeLocatorColumn>" = @DocumentId
ORDER BY ...;
```

For the root table this will usually be physical `"DocumentId"`. For child, nested collection, and extension tables it is often a propagated locator such as `"Student_DocumentId"`, not the table primary key and not necessarily `"DocumentId"`. Use the same root scope locator column currently resolved by:

```csharp
RelationalResourceModelCompileValidator.ResolveRootScopeLocatorColumnOrThrow(...)
```

Descriptor projection and document-reference lookup SQL should use the same direct predicate shape against each source table, with the non-null predicate targeting that source's descriptor or document-reference FK column:

```sql
FROM "edfi"."SomeTable" t0
WHERE t0."<RootScopeLocatorColumn>" = @DocumentId
  AND t0."<ProjectionFkColumn>" IS NOT NULL
```

For `PageKeysetSpec.Query`, keep the existing `"page"` temp table behavior unchanged.

The first implementation should be PostgreSQL-only. Populate the new single-document SQL fields for PostgreSQL read plans, leave them `null` for SQL Server read plans, and only select the fast path when the dialect is PostgreSQL, the keyset is `PageKeysetSpec.Single`, and the rollout switch is enabled. The fast path must preserve the existing result-set shapes and reader flow; the only intended SQL-shape change is replacing keyset materialization plus `"page"` joins with direct predicates.

Use one shared single-document parameter convention instead of repeating the `"DocumentId"` literal across compilers and the batch builder. This checkout already has `HydrationBatchBuilder.DocumentIdParameterName`; move that constant to a small shared convention type in the `EdFi.DataManagementService.Backend.Plans` assembly and update `HydrationBatchBuilder`, metadata SQL emission, and the compilers to use it. Keep this scoped to hydration SQL; do not merge it with similarly named authorization constants or introduce a second hydration `"DocumentId"` constant.

```csharp
internal static class HydrationSqlConventions
{
    public const string SingleDocumentIdParameterName = "DocumentId";
}
```

## Implementation Steps

### 1. Add a narrow rollout switch

Start with a local execution option instead of a global configuration setting:

```csharp
public sealed record HydrationExecutionOptions(
    bool IncludeDescriptorProjection = true,
    bool IncludeDocumentReferenceLookup = true,
    bool UseSingleDocumentFastPath = false
);
```

Recommended location: the existing `HydrationExecutionOptions` record in `EdFi.DataManagementService.Backend.External`, defaulted to `false`. This keeps the first experiment small and lets callers opt into the fast path deliberately without changing application-level configuration binding. Use named arguments at call sites after adding the new constructor parameter so the existing boolean options stay readable.

The first performance experiment must explicitly enable the option in the Suite 3 write-path hydration call sites, otherwise the default `false` means the run will still use the temp-table path. The two primary call sites from the current checkout are:

- `RelationalWriteCurrentStateLoader.LoadAsync(...)` in `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteCurrentState.cs`, which loads current state with `PageKeysetSpec.Single`.
- `RelationalCommittedRepresentationReader.ReadAsync(...)` in `src/dms/backend/EdFi.DataManagementService.Backend/RelationalCommittedRepresentationReader.cs`, which reads back the committed representation with `PageKeysetSpec.Single`.

Do not change `RelationalDocumentStoreRepository` query or GET hydration defaults for the first experiment. Configuration wiring and any broader default-on decision are follow-up work, not part of this implementation.

### 2. Extend plan contracts with single-document SQL

The compiled read plan currently stores keyset-join SQL in these contracts:

- `TableReadPlan.SelectByKeysetSql`
- `DescriptorProjectionPlan.SelectByKeysetSql`
- `DocumentReferenceLookupPlan.SelectByKeysetSql`

Relevant files in this checkout:

- `src/dms/backend/EdFi.DataManagementService.Backend.External/Plans/ReadPlanContracts.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.External/Plans/ProjectionPlanContracts.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.External/Plans/HydrationExecutionOptions.cs`

Add parallel optional SQL fields for the single-document path:

```csharp
string? SelectBySingleDocumentSql = null
```

Apply this to:

- `TableReadPlan`
- `DescriptorProjectionPlan`
- `DocumentReferenceLookupPlan`

Keep existing fields unchanged for the query-page path. These contracts live in `EdFi.DataManagementService.Backend.External`, so keep the new fields nullable and add optional constructor parameters after the existing constructor arguments to reduce fixture churn and preserve existing call sites:

```csharp
public TableReadPlan(
    DbTableModel TableModel,
    string SelectByKeysetSql,
    string? SelectBySingleDocumentSql = null
)
```

Apply the same constructor shape to `DescriptorProjectionPlan` and `DocumentReferenceLookupPlan`, with the nullable SQL parameter after `SourcesInOrder`. Do not run the nullable field through `RequireNotNull(...)`. When `UseSingleDocumentFastPath` is selected, require these fields to be populated and throw an `InvalidOperationException` if one is missing. Do not silently fall back to keyset SQL in the fast path, because that would make performance evidence hard to interpret.

For descriptor and document-reference lookup contracts, keep the existing argument order and append the optional parameter last:

```csharp
public DescriptorProjectionPlan(
    string SelectByKeysetSql,
    DescriptorProjectionResultShape ResultShape,
    IEnumerable<DescriptorProjectionSource> SourcesInOrder,
    string? SelectBySingleDocumentSql = null
)
```

Check the normalized plan contract helpers before finalizing the contract change. These live in the unit-test project, but they are the canonical golden-test representation for compiled plan contracts:

- `NormalizedPlanContractDtos.cs`
- `NormalizedPlanContractCodec.cs`
- `NormalizedPlanContractCodecTests.cs`
- `NormalizedPlanDtoJson.cs`
- `MappingSetManifestJsonEmitter.cs`

Because `ReadPlanCompiler` will emit this SQL deterministically, include the new fields in normalized DTOs, codec encode/decode, and canonical JSON output. Use normalized multiline text when present and explicit JSON `null` when absent, matching the existing nullable-field convention in `NormalizedPlanDtoJson`. `ResourceReadPlanDto.DocumentReferenceLookup` already exists but is not currently written by `NormalizedPlanDtoJson`; write that existing canonical JSON section as either an object or explicit `null` so both document-reference lookup SQL fields are covered by contract snapshots. Do not add a document-reference lookup section to the mapping-set manifest.

For `MappingSetManifestJsonEmitter`, keep the existing diagnostic-only convention: add nullable SHA-256 fields such as `select_by_single_document_sql_sha256` for table and descriptor plans, but do not emit raw SQL text into the manifest. Emit a hash string when the SQL is present and JSON `null` when it is absent. `DocumentReferenceLookupPlan` is not currently surfaced in the manifest, so keep it out of the manifest for this change. This will cause intentional golden-file churn, but it keeps the new table and descriptor SQL under manifest-level determinism without expanding the manifest's scope.

Rationale: runtime SQL string replacement would be brittle. Compiling a second form from the same metadata preserves the existing compiler validation model and keeps the runtime builder simple.

### 3. Compile table hydration single-document SQL

Update `ReadPlanCompiler.EmitSelectByKeysetSql(...)` by extracting shared helpers for table validation, select-list emission, and ordering emission, then add:

```csharp
EmitSelectBySingleDocumentSql(...)
```

The new SQL should:

- Select the same columns in the same order.
- Use the same table alias.
- Filter with `WHERE <tableAlias>.<RootScopeLocatorColumn> = @DocumentId`, emitted through `SqlWriter.AppendParameter(...)` and the shared parameter convention.
- Use the same deterministic `ORDER BY`.

The result-set shape must be identical to the keyset path so `HydrationReader.ReadTableRowsAsync(...)` remains unchanged. Resolve the root scope locator and hydration ordering through the existing `RelationalResourceModelCompileValidator` helpers in shared code used by both emitters; do not infer either from table primary keys.

Leave `SelectByKeysetSql` unchanged. The fast path adds a second compiled SQL string; it should not rewrite the existing page-temp-table query. In `CompileCore(...)`, pass `SelectBySingleDocumentSql` into `TableReadPlan` only when `_dialect is SqlDialect.Pgsql`; leave it `null` for SQL Server until the SQL Server path is separately tested.

### 4. Compile descriptor projection single-document SQL

Update `DescriptorProjectionPlanCompiler`.

The single-document form should:

- Preserve descriptor projection result columns and ordering.
- Preserve `DISTINCT` / `UNION` behavior exactly as the keyset form does.
- Use the same resolved descriptor storage column as the keyset path, including unified-alias canonical storage columns.
- Replace each `INNER JOIN "page" k ON ... = k."DocumentId"` with a direct predicate:

```sql
WHERE <tableAlias>.<RootScopeLocatorColumn> = @DocumentId
  AND <tableAlias>.<DescriptorColumn> IS NOT NULL
```

Be careful when the existing generated SQL has multiple descriptor sources. The direct predicate has to be applied inside each source SELECT before the `UNION`, not outside the derived projection table.

Preserve the existing single-source `DISTINCT` shape and multi-source `UNION` shape. The only intended difference is the source filter changing from a `"page"` join to a single-document predicate. Prefer a shared source-emission helper with a small mode parameter over copying the whole union loop.

Only populate `DescriptorProjectionPlan.SelectBySingleDocumentSql` for PostgreSQL plans in this implementation. Leave it `null` for SQL Server plans. `DescriptorProjectionPlanCompiler` currently stores `ISqlDialect`; also retain the source `SqlDialect` value so PostgreSQL-only field population is explicit and testable.

### 5. Compile document-reference lookup single-document SQL

Update `DocumentReferenceLookupPlanCompiler`.

The single-document form should:

- Return the same columns in the same order.
- Preserve source ordering and deduplication behavior.
- Filter each source table by its root scope locator column with `WHERE <tableAlias>.<RootScopeLocatorColumn> = @DocumentId`.
- Preserve the source FK non-null predicate with `AND <tableAlias>.<DocumentReferenceFkColumn> IS NOT NULL`.

This path is gated by `HydrationExecutionOptions.IncludeDocumentReferenceLookup`; the write-path callers in this plan opt out, but the fast-path batch should still behave correctly if document-reference lookup is included. Do not use this as a reason to opt GET/reconstitution callers into the fast path in the first experiment.

As with descriptor projection, preserve the existing single-source `DISTINCT` behavior and multi-source `UNION` behavior. Apply the direct predicate inside each source SELECT before any unioning or deduplication. Prefer reusing the same source-emission structure used by `EmitSelectByKeysetSql(...)` so the table alias allocation, ordering, and deduplication rules cannot drift.

Only populate `DocumentReferenceLookupPlan.SelectBySingleDocumentSql` for PostgreSQL plans in this implementation. Leave it `null` for SQL Server plans. As with descriptor projection, keep the dialect gate in the compiler rather than in the batch builder so missing PostgreSQL single-document SQL is a plan-compilation defect.

### 6. Add direct metadata SELECT support

Add a dialect method next to `AppendDocumentMetadataSelect(...)`:

```csharp
void AppendSingleDocumentMetadataSelect(SqlWriter writer, string documentIdParameterName);
```

For PostgreSQL, emit the same six metadata columns as the keyset form, then `WHERE d."DocumentId" = @DocumentId` and `ORDER BY d."DocumentId"`.

Implement the body in the shared `DocumentMetadataColumns` helper in `IPlanSqlDialect.cs` so keyset and single-document metadata selects stay column-order compatible with `HydrationReader.ReadDocumentMetadataAsync(...)`. Add a shared `DocumentId` column constant there, because the direct form no longer receives ordinal 0 from `KeysetTableContract.DocumentIdColumnName`. Pass the bare parameter name and let the helper format it with `SqlWriter.AppendParameter(...)`.

Update the `DocumentMetadataColumns` comments while making this change. `DocumentId` ordinal 0 will no longer always be supplied through a keyset contract; both metadata SELECT forms must still emit the same six columns in the same order.

Because `HydrationBatchBuilder` works through `IPlanSqlDialect`, add the method to both dialect classes for interface symmetry, but only call it from the PostgreSQL fast path in the first implementation. The SQL Server batch should continue to use its existing `#page` flow until separately tested.

### 7. Branch in `HydrationBatchBuilder`

Update `HydrationBatchBuilder.Build(...)`:

```csharp
if (
    dialect == SqlDialect.Pgsql
    && keyset is PageKeysetSpec.Single
    && executionOptions.UseSingleDocumentFastPath
)
{
    return BuildSingleDocumentBatch(...);
}

return BuildExistingKeysetBatch(...);
```

The single-document batch should emit result sets in the same sequence as today:

1. Document metadata
2. Table rows in dependency order
3. Descriptor URI rows, if enabled
4. Document-reference lookup rows, if enabled

It should not emit:

- `DROP TABLE IF EXISTS "page"`
- `CREATE TEMP TABLE "page"`
- `INSERT INTO "page"`
- `INNER JOIN "page"` or any other reference to the PostgreSQL temp-table keyset relation

`HydrationBatchBuilder.AddParameters(...)` already adds `@DocumentId` for `PageKeysetSpec.Single`, so parameter binding should not need major changes. Keep the existing public overload behavior intact and split the current implementation into an existing-keyset helper before adding `BuildSingleDocumentBatch(...)`. The single-document helper should call the new direct metadata-select method and then append `SelectBySingleDocumentSql` for table, descriptor, and document-reference plans according to the existing inclusion options.

Add a small helper such as `RequireSingleDocumentSql(planName, sql)` for emitted single-document plan SQL. It should throw a clear `InvalidOperationException` when the flag is enabled but a required `SelectBySingleDocumentSql` value is `null` or empty. Require it for every table plan, for descriptor projection plans only when `IncludeDescriptorProjection` is `true`, and for document-reference lookup only when `IncludeDocumentReferenceLookup` is `true` and `plan.DocumentReferenceLookup` is non-null.

Review `HydrationExecutor` XML comments, inline comments, and reader sequencing after this change. Its result-set reader should stay unchanged: it reads optional total count only for `PageKeysetSpec.Query`, then document metadata, then table rows, descriptor rows, and document-reference lookup. The fast path starts directly with document metadata, so comments should no longer claim every batch starts with temp-table DDL/DML.

### 8. Opt in the Suite 3 write-path callers

Once unit and integration tests pass, opt the performance experiment into the fast path at the two write-path single-document hydration call sites.

In `RelationalWriteCurrentStateLoader.LoadAsync(...)`:

```csharp
new HydrationExecutionOptions(
    IncludeDescriptorProjection: request.IncludeDescriptorProjection,
    IncludeDocumentReferenceLookup: false,
    UseSingleDocumentFastPath: true
)
```

In `RelationalCommittedRepresentationReader.ReadAsync(...)`:

```csharp
new HydrationExecutionOptions(
    IncludeDescriptorProjection: true,
    IncludeDocumentReferenceLookup: false,
    UseSingleDocumentFastPath: true
)
```

This can be a short-lived experiment commit/branch if the team does not want hard-coded opt-in behavior on `main`. Do not add application configuration in this implementation; that is a separate rollout decision after correctness and performance are proven.

### 9. Keep SQL Server behavior unchanged initially

This performance problem was measured on PostgreSQL. Keep the first branch PostgreSQL-only: do not populate SQL Server single-document SQL fields and do not select a SQL Server fast path in this implementation.

After the PostgreSQL change is stable, consider implementing the same direct-predicate path for SQL Server `#page` single-document hydration as separate follow-up work.

## Correctness Tests

Update or add unit tests in:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydrationBatchBuilderTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/DocumentReferenceLookupPlanCompilerTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ExternalPlanContractsTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PgsqlPlanDialectTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NormalizedPlanContractDtosTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NormalizedPlanContractCodecTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MappingSetManifestJsonEmitterTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MappingSetManifestGoldenFixtureTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/RelationalWriteCurrentStateLoaderTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/RelationalCommittedRepresentationReaderTests.cs`

Descriptor projection assertions currently live with the read-plan compiler coverage in this test project. Add a dedicated test file only if the new cases make `ReadPlanCompilerTests.cs` too broad.

Minimum assertions:

- `PageKeysetSpec.Single` with the feature flag emits no `DROP TABLE`, no `CREATE TEMP TABLE`, and no `INSERT INTO "page"`.
- `PageKeysetSpec.Single` with the feature flag emits no `INNER JOIN "page"` in table, descriptor, or document-reference SQL.
- `PageKeysetSpec.Single` emits `WHERE ... = @DocumentId` for document metadata and table hydration.
- Child table hydration uses root scope locator columns, not blindly `"DocumentId"`.
- Descriptor projection returns the same descriptor rows as the keyset path.
- Document-reference lookup returns the same rows as the keyset path.
- `PageKeysetSpec.Query` output is unchanged and still uses `"page"`.
- Feature flag disabled output is unchanged for `PageKeysetSpec.Single`.
- `SqlDialect.Mssql` output is unchanged and still uses `#page` even if the fast-path flag is set.
- Fast path enabled with a missing `SelectBySingleDocumentSql` throws `InvalidOperationException`.
- Fast path batch result-set order starts with document metadata and still matches `HydrationExecutor` / `HydrationReader` expectations.
- External plan contract constructors preserve existing call sites and expose nullable `SelectBySingleDocumentSql` without requiring it.
- Normalized contract DTO/codec/JSON round-trips include the new SQL when present, emit explicit `null` for SQL Server/absent single-document SQL fields and absent document-reference lookup, and mapping-set manifest diagnostics include the new hash fields for the plan types already surfaced there.
- The two Suite 3 write-path call sites pass `UseSingleDocumentFastPath: true` only at the intended experiment call sites.

Integration tests:

- Reuse PostgreSQL hydration executor tests that already call `PageKeysetSpec.Single`, primarily `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/HydrationExecutorTests.cs` and the existing PostgreSQL descriptor projection integration fixtures.
- Add one nested/child-table resource test where the root scope locator column is not just the selected table's primary `DocumentId`.
- Add descriptor projection coverage for null descriptor FK and multiple descriptor sources.
- Add document-reference lookup coverage with the fast path enabled.
- Compare hydrated output between the existing keyset path and the fast path for the same `DocumentId` on at least one resource with child rows, descriptor projection, and document-reference lookup.

## Performance Validation

Run the same measured workload used in the 2026-07-03 report:

```bash
cd /home/brad/work/dms-root/Suite-3-Performance-Testing/src/edfi-performance-test
poetry run python -m edfi_performance_test \
  --runTimeInMinutes 30 \
  --clientCount 20 \
  --spawnRate 20 \
  --output ../../DmsTestResults/<timestamp>-single-doc-fastpath \
  --testType volume
```

Keep these constant:

- DMS branch/build except for the fast-path change.
- Suite 3 branch/build.
- PostgreSQL version and settings.
- DMS rate-limit overrides.
- Seed data volume.
- Client count, spawn rate, duration, and test type.

Collect the same evidence:

- `volume_stats.csv`
- `volume_stats_history.csv`
- `volume_failures.csv`
- `volume_exceptions.csv`
- Early/mid/late/final PostgreSQL evidence via `collect_pg_evidence.sh`
- DMS `.NET` counters via `collect_dms_dotnet_telemetry.sh`

Success criteria:

- Zero Suite 3 failures and exceptions.
- `pg_stat_monitor` call volume for `CREATE TEMP TABLE "page"` / `DROP TABLE IF EXISTS "page"` drops materially in the write-volume run; any remaining calls should correlate with query hydration or intentionally non-opted-in single-document paths such as `RelationalDocumentStoreRepository` GET hydration.
- Read/paging or query-keyset evidence still shows the `"page"` path for `PageKeysetSpec.Query`, proving the query route remained unchanged.
- `pg_stat_io` temp relation reads and temp relation extends drop materially.
- Throughput improves versus the configured local baseline, or PostgreSQL CPU drops at comparable throughput.
- Median, P95, and P99 latency do not regress.
- DMS Kestrel queue length and request queue length remain zero.

## Risks And Mitigations

| Risk | Mitigation |
| --- | --- |
| Result-set shape changes break hydration reader assumptions | Keep column order identical and add unit tests comparing fast-path/keyset result shapes. |
| Child table filtering uses wrong column | Always use resolved root scope locator column. Add nested table tests. |
| Descriptor projection semantics change | Preserve `DISTINCT`, `UNION`, ordering, and null filtering. Add descriptor integration tests. |
| Query-page hydration accidentally changes | Branch only on `PageKeysetSpec.Single` plus feature flag. Add unchanged SQL tests for `PageKeysetSpec.Query`. |
| SQL Server behavior regresses | Keep first branch PostgreSQL-only or prove equivalent SQL Server tests before enabling there. |
| Contract changes cause broad fixture churn | Add optional constructor parameters after existing fields and update normalized contract helpers deliberately. |
| Fast path masks missing generated SQL | Throw when fast path is selected and any required `SelectBySingleDocumentSql` is missing. |
| Performance gain is hidden by other hotspots | Use pg_stat evidence to verify temp-table work disappears even if `dms."Document"` delete remains dominant. |

## Rollback Plan

Disable the feature flag. The existing keyset temp-table path remains intact and should continue to be used for all hydration.

With this plan's `HydrationExecutionOptions` switch, rollback is a code change to pass `false` or remove the option after reverting the two write-path call sites.

## Follow-Up Work

Outside this plan, the next database hotspot is still:

```sql
DELETE FROM dms."Document"
WHERE "DocumentId" = $1
RETURNING "DocumentId"
```

If the single-document fast path improves temp-table metrics but total throughput remains flat, move next to a focused delete/reference cleanup deep dive.

## Clarifying Questions and Answers

### Questions 1

1. Should the story merge with `UseSingleDocumentFastPath: true` hard-coded at the two Suite 3 write-path hydration call sites, or should that call-site opt-in live only on a performance experiment branch with the default `false` behavior preserved on `main`?
2. Should `ReadPlanProjectionContractValidator` or another compile-time validator enforce that PostgreSQL read plans always populate every required `SelectBySingleDocumentSql` field and SQL Server read plans leave those fields `null`, or is the runtime `HydrationBatchBuilder` missing-SQL check the only required guard?
3. Are mapping-pack/protobuf contract and `reference/design/backend-redesign/design-docs/mpack-format-v1.md` updates in scope for the new read-plan SQL fields, or should this story update only the runtime contracts plus the current test-only normalized DTOs and manifests?

### Answers 1

1. Merge the story with `UseSingleDocumentFastPath: true` at exactly the two Suite 3 write-path hydration call sites named in the plan, after the correctness tests pass. Keep the `HydrationExecutionOptions` default `false` and leave GET/query/read hydration call sites unchanged. This makes the performance validation runnable from `main` while preserving a small rollback: change those two named arguments back to `false` or remove them.
2. Add a dedicated compile-time/read-plan contract validator for the new single-document SQL fields, and keep the `HydrationBatchBuilder` runtime missing-SQL guard. The validator should run from `ReadPlanCompiler` after projection validation and enforce that PostgreSQL relational read plans populate every required `SelectBySingleDocumentSql` field for table, descriptor projection, and document-reference lookup plans, while SQL Server read plans leave those fields `null`. The batch-builder guard remains necessary for hand-built, decoded, or otherwise externally supplied plans when the execution option is enabled.
3. Limit this story to active runtime contracts, compilers, batch execution, current test-only normalized DTO/codecs/canonical JSON, and mapping-set manifest diagnostics. Do not implement mapping-pack/protobuf contract changes or edit `mpack-format-v1.md` in this story, because the repository still has placeholder mapping-pack payload/decode support and the protobuf contracts are owned by the E05 mapping-pack stories. The follow-up mapping-pack contract work must carry both the existing document-reference lookup plan and the new nullable single-document SQL fields into the PackFormatVersion 1 schema before AOT pack support ships.
