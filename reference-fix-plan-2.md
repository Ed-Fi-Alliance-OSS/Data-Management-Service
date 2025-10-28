# Reference Table AliasId Refactor Plan

## Objective
- Replace `(ReferentialPartitionKey, ReferentialId)` on `dms.Reference` with `(ReferentialPartitionKey, AliasId)` that points at the `dms.Alias` primary key (Brad report §Schema Refinements #2, lines 65-68). This requires coordinated updates to schema DDL, PL/pgSQL routines, C# data-access helpers, performance harness loaders, and documentation so that references persist alias identities while preserving current validation behavior.

## Touchpoints Identified
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql:7-48`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql:21-58`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0015_Create_Reference_Validation_FKs.sql:10-18`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Model/BulkReferences.cs:11-24`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/ReferenceHelper.cs:13-75`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/ISqlAction.cs:104-110`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:493-524`, `:575-643`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/UpsertDocument.cs:125-221`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/UpdateDocumentById.cs:235-321`
- `perf-dms-reference-table-direct-load/scripts/setup-test-db.sh:117-264`
- `perf-dms-reference-table-direct-load/scripts/generate-test-data.sh:188-472`
- `perf-dms-reference-table-direct-load/scripts/load-test-data-from-csv.sh:62-113`
- `perf-dms-reference-table-direct-load/data/generate_deterministic_data.py:188-320`
- `perf-dms-reference-table-direct-load/load-tests/concurrent_load_test.py:49-119`
- `perf-dms-reference-table-direct-load/scripts/run-all-tests.sh:132-165`
- `perf-dms-reference-table-direct-load/sql/alternatives/*.sql` (alias-heavy scripts at `merge_pattern.sql:17-132`, `differential_update.sql:11-118`, `scenarios/test_problematic_patterns.sql:244-341`)
- Docs referencing the reverse-lookup index: `docs/REFERENCE-VALIDATION.md:9-16`, `perf-dms-reference-table-direct-load/README.md:135-148`, `TEST_STRATEGY.md:17-135`, `TUNING_EXPLORATIONS.md:33-70`, `scripts/generate-report.sh:265-269`

## Detailed Implementation Steps

1. **Revise Reference table DDL for new foreign key**
   - `0003_Create_Reference_Table.sql:11-35`: swap `ReferentialId UUID NOT NULL` for `AliasId BIGINT NOT NULL`. Rename supporting index to `IX_Reference_AliasId` and target columns `(ReferentialPartitionKey, AliasId)`.
   - `0003_Create_Reference_Table.sql:37-45`: ensure FK back to parent document unchanged.
   - `0015_Create_Reference_Validation_FKs.sql:10-18`: change FK definition to `FOREIGN KEY (ReferentialPartitionKey, AliasId) REFERENCES dms.Alias (ReferentialPartitionKey, Id) ON DELETE RESTRICT ON UPDATE CASCADE`.
   - Update any inline comments describing the column purpose to reference alias Id storage.

2. **Update `dms.InsertReferences` to resolve AliasId**
   - `0010_Create_Insert_References_Procedure.sql:21-54`:
     - Extend the SELECT to left join `dms.Alias` on `(ids.referentialPartitionKey, ids.referentialId)` so we can project `a.Id AS aliasId`.
     - Insert column list becomes `(ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey)`; use `COALESCE(a.Id, 0)` only for detection, but insert must fail when `a.Id` is null. Instead, filter to `WHERE a.Id IS NOT NULL` for the insert, and collect rows with `a.Id IS NULL` for the return payload.
     - Adjust FK violation handler to keep returning the original `Guid` values: gather invalid IDs via a CTE capturing entries where `a.Id IS NULL` before the insert attempt; return them directly rather than relying on constraint failure.
     - Continue to delete existing references for the parent prior to insert.

3. **Align C# data access contracts to alias storage**
   - `Model/BulkReferences.cs:11-24`: document that the payload still contains referential GUIDs (used solely for alias resolution) while the database stores alias identifiers.
   - `ReferenceHelper.cs:13-35`: no functional change, but update XML doc comments to clarify the GUID arrays feed alias lookups.
   - `ISqlAction.cs:104-110` and `SqlAction.cs:493-524`: update the method summary to state the function stores alias Ids; keep the signature returning `Guid[]` for invalid referential IDs (relies on updated SQL).
   - `SqlAction.cs:575-637`: update joins in `FindReferencingResourceNamesByDocumentUuid` and `FindReferencingDocumentsByDocumentId` so that the predicates use `a.Id = r.AliasId` and `a.ReferentialPartitionKey = r.ReferentialPartitionKey`. Remove UUID comparisons in the join body and adjust alias columns selected (no longer pull `a.DocumentId` via referential join).
   - `UpsertDocument.cs:125-221` & `UpdateDocumentById.cs:235-321`: no signature changes, but update inline comments/error messages to indicate alias validation; ensure the failure logging still references the invalid GUIDs surfaced from the stored procedure.

4. **Performance harness schema & loaders**
   - `scripts/setup-test-db.sh:117-210`: mirror the production DDL changes—column rename, index rename, FK definition, and stored procedure adjustments identical to Step 2. Ensure helper FK index `IX_FK_Reference_ReferencedAlias` also targets `(ReferentialPartitionKey, AliasId)`.
   - `scripts/generate-test-data.sh:188-472`: augment `dms.alias_lookup` to include `alias_id BIGINT`; populate it from `dms.Alias.Id`. When inserting into `dms.Reference`, select `al.alias_id` instead of UUID.
   - `scripts/load-test-data-from-csv.sh:62-113`:
     - Emit alias CSV with explicit `Id` column and load using `COPY dms.Alias (Id, ReferentialPartitionKey, ReferentialId, DocumentId, DocumentPartitionKey) FROM STDIN WITH (FORMAT csv, HEADER true) OVERRIDING SYSTEM VALUE;`.
     - After load, call `SELECT setval(pg_get_serial_sequence('dms.Alias','Id'), (SELECT max(Id) FROM dms.Alias))` to sync the identity sequence.
     - Replace the direct `COPY` into `dms.Reference` with a two-stage load: copy CSV into staging table `(ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey)`, then `INSERT ... SELECT` into `dms.Reference`.
   - `data/generate_deterministic_data.py:188-320`: extend the alias writer to include a deterministic `AliasId` column (monotonic counter), write reference CSV rows with that alias id instead of the referential Guid, and update header metadata accordingly.
   - `load-tests/concurrent_load_test.py:49-119`: fetch `(Id, ReferentialPartitionKey)` from `dms.Alias`, store tuples as `(alias_id, partition_key)`, generate `alias_ids` arrays of `int64` (no UUID strings), and adjust the procedure call to pass `bigint[]` alias ids. Update docstrings describing the payload.
   - `scripts/run-all-tests.sh:132-165` and `sql/scenarios/test_problematic_patterns.sql:325-335`: rewrite reference aggregation queries to collect alias Ids (`a.Id`) and pass them as `bigint[]`. Ensure helper subqueries continue sorting by partition key for determinism.
   - `sql/alternatives/merge_pattern.sql:17-132` & `sql/alternatives/differential_update.sql:11-118`: adapt temporary staging tables, unique constraints, and DML logic to work with `AliasId BIGINT` instead of GUIDs. For example, change the unique constraint to `(ParentDocumentId, ParentDocumentPartitionKey, AliasId)` and update all inserts/joins/selects accordingly.

5. **Documentation, tuning scripts, and tooling**
   - `docs/REFERENCE-VALIDATION.md:9-16`: document that `FK_Reference_ReferencedAlias` now binds the alias identity `(ReferentialPartitionKey, AliasId)` and mention the removal of referential GUID storage.
   - `perf-dms-reference-table-direct-load/README.md:135-148`, `TEST_STRATEGY.md:17-135`, `TUNING_EXPLORATIONS.md:33-70`, `scripts/generate-report.sh:265-269`: rename index references to `IX_Reference_AliasId`, update performance notes to reflect alias-id lookups, and adjust any SQL snippets displayed in documentation.
   - Sweep the perf project for literal `ReferentialId` mentions referencing the old column (e.g., `sql/alternatives/ALTERNATIVES-ANALYSIS.md` sections at lines 56-211) and revise narrative text and sample SQL to the alias-id terminology.

6. **Verification & follow-up**
   - After code changes, regenerate database via `0003`/`0010` scripts and run integration tests in `EdFi.DataManagementService.Backend.Postgresql.Tests.Integration` to ensure delete/reference flows remain intact.
   - Rebuild performance fixtures with the updated generators, rerun `scripts/run-all-tests.sh`, and confirm the new index (`IX_Reference_AliasId`) appears in explain plans and documentation outputs.
