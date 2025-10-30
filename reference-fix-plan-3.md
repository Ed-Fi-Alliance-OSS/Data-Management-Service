# Reference Table Reverse Lookup Denormalization Plan

## Objective
- Reintroduce `ReferencedDocumentPartitionKey`/`ReferencedDocumentId` as derived columns on `dms.Reference`, populate them from the alias join inside `dms.InsertReferences`, and pivot reverse-lookup queries back to a partition-aligned `(ReferencedDocumentPartitionKey, ReferencedDocumentId)` filter so PostgreSQL can prune partitions without sacrificing the AliasId-based validation added in plan 2.

## Touchpoints Identified
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs`
- Optional parity updates for perf harness schema/loaders (`perf-dms-reference-table-direct-load/**`) once production DDL is finalized

## Detailed Implementation Steps

1. **Extend the Reference table DDL for fresh deployments**
   - In `0003_Create_Reference_Table.sql`, add the columns back immediately after `AliasId`:
     ```sql
     ReferencedDocumentPartitionKey SMALLINT NOT NULL,
     ReferencedDocumentId BIGINT NOT NULL,
     ```
   - Introduce a covering index tuned for reverse lookups:
     ```sql
     CREATE INDEX IF NOT EXISTS IX_Reference_ReferencedDocument
       ON dms.Reference (ReferencedDocumentPartitionKey, ReferencedDocumentId);
     ```
   - Keep `IX_Reference_AliasId` and the Alias FK from plan 2 intact.
   - Update inline comments to describe the new columns as denormalized document identity sourced from `dms.Alias` and required for partition-pruned reverse lookups.

2. **Update `dms.InsertReferences` to hydrate the new columns**
   - Expand the `payload` CTE in `0010_Create_Insert_References_Procedure.sql` to project `a.DocumentPartitionKey` and `a.DocumentId`.
   - Adjust the `INSERT INTO dms.Reference` target list to include the two new columns:
     ```sql
     INSERT INTO dms.Reference (
         ParentDocumentId,
         ParentDocumentPartitionKey,
         AliasId,
         ReferentialPartitionKey,
         ReferencedDocumentId,
         ReferencedDocumentPartitionKey
     )
     ```
   - Populate them in the `SELECT` clause from the payload (`documentId`, `documentPartitionKey`, `aliasId`, `referentialPartitionKey`, `aliasDocumentId`, `aliasDocumentPartitionKey`), retaining the `WHERE aliasId IS NOT NULL` guard so rows without a matching alias are excluded.
   - Return invalid referential GUIDs exactly as plan 2 currently does (rows where `aliasId IS NULL`).

3. **Rework reverse-lookup SQL to use the denormalized columns**
   - `FindReferencingResourceNamesByDocumentUuid` in `SqlAction.cs`:
     - Replace the inner subquery so it reads directly from `dms.Reference` and `dms.Document` using the new columns:
       ```csharp
       SELECT r.ParentDocumentId, r.ParentDocumentPartitionKey
         FROM dms.Reference r
         INNER JOIN dms.Document d2
           ON d2.Id = r.ReferencedDocumentId
          AND d2.DocumentPartitionKey = r.ReferencedDocumentPartitionKey
        WHERE d2.DocumentUuid = $1
          AND d2.DocumentPartitionKey = $2
       ```
     - Remove the `INNER JOIN dms.Alias` from the subquery; it is no longer needed at read time.
   - `FindReferencingDocumentsByDocumentId` in `SqlAction.cs`:
     - Collapse the join chain to two tables (`dms.Document` ↔ `dms.Reference`) and filter on the new columns:
       ```csharp
       WHERE r.ReferencedDocumentId = $1
         AND r.ReferencedDocumentPartitionKey = $2
       ```
     - Drop the `INNER JOIN dms.Alias` and its join predicates.
   - Confirm both queries still order by `d.ResourceName {SqlBuilder.SqlFor(...)}` and maintain existing parameters.

4. **Plan validation tasks**
   - After code changes, ensure `EXPLAIN (ANALYZE)` on both reverse-lookup queries shows `Bitmap Index Scan`/`Index Only Scan` on `IX_Reference_ReferencedDocument` with partition pruning (only one `reference_nn` child per call).
   - Rerun integration/E2E tests to confirm no regressions in reference validation or delete cascades.
   - Update the perf harness schema and loaders to mirror these columns/indexes once the production rollout is approved, keeping plan 3 in sync across environments.
