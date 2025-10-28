# Reference Table Schema Refinement Plan

## Objective
- Drop `Reference.ReferencedDocumentId` and `Reference.ReferencedDocumentPartitionKey`, remove the related FK/index, and shift reverse-lookup logic to resolve via `Reference → Alias → Document` as outlined in `brad-report.md` lines 56–63.

## Touchpoints Identified
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql:7-63`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql:25-44`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:575-643`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/DeleteTests.cs:432-601`
- `perf-dms-reference-table-direct-load/scripts/setup-test-db.sh:156-224`
- `perf-dms-reference-table-direct-load/scripts/generate-test-data.sh:432-447`
- Documentation touchpoint: `docs/REFERENCE-VALIDATION.md:13-20`

## Detailed Implementation Steps

1. **Update the base table definition for new installs**
   - In `0003_Create_Reference_Table.sql:7-63`, remove the two referenced-document columns and their FK/index definitions.
   - Replace lines 11-14 with:
     ```sql
     ParentDocumentPartitionKey SMALLINT NOT NULL,
     ReferentialId UUID NOT NULL,
     ReferentialPartitionKey SMALLINT NOT NULL,
     ```
   - Delete the `CREATE INDEX ... UX_Reference_ReferencedDocumentId` block (lines 37-37) and the `FK_Reference_ReferencedDocument` block (lines 52-62).
   - Add a new supporting index after line 34 to keep reverse lookups efficient post-change:
     ```sql
     CREATE INDEX IF NOT EXISTS IX_Reference_ReferentialId ON dms.Reference (ReferentialPartitionKey, ReferentialId);
     ```

2. **Simplify `dms.InsertReferences` to stop populating dropped columns**
   - In `0010_Create_Insert_References_Procedure.sql:25-43`, shrink the insert list to four columns and drop the alias join output columns.
   - Replace the insert target list with:
     ```sql
     INSERT INTO dms.Reference (
         ParentDocumentId,
         ParentDocumentPartitionKey,
         ReferentialId,
         ReferentialPartitionKey
     )
     ```
   - Replace the select clause with:
     ```sql
     SELECT
         ids.documentId,
         ids.documentPartitionKey,
         ids.referentialId,
         ids.referentialPartitionKey
     FROM unnest(...) AS ids(...)
     ```
   - Remove lines 38-43 that join to `dms.Alias`; the FK to `dms.Alias` will enforce referential integrity.
   - Confirm the exception handler still references `fk_reference_referencedalias` only; no further change required.

3. **Rework reverse-lookup queries in `SqlAction`**
   - `FindReferencingResourceNamesByDocumentUuid` (lines 575-605):
     - Replace the inner subquery so it joins `dms.Reference` to `dms.Alias` and filters by the target document UUID via `dms.Document`.
     - Proposed SQL body:
       ```csharp
       $@"SELECT d.ResourceName FROM dms.Document d
              INNER JOIN (
                  SELECT r.ParentDocumentId, r.ParentDocumentPartitionKey
                  FROM dms.Reference r
                  INNER JOIN dms.Alias a ON a.ReferentialId = r.ReferentialId
                                         AND a.ReferentialPartitionKey = r.ReferentialPartitionKey
                  INNER JOIN dms.Document d2 ON d2.Id = a.DocumentId
                                            AND d2.DocumentPartitionKey = a.DocumentPartitionKey
                  WHERE d2.DocumentUuid = $1 AND d2.DocumentPartitionKey = $2
              ) AS re ON re.ParentDocumentId = d.Id
                      AND re.ParentDocumentPartitionKey = d.DocumentPartitionKey
              ORDER BY d.ResourceName {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};"
       ```
     - Keep existing parameter array (lines 589-593) unchanged.
   - `FindReferencingDocumentsByDocumentId` (lines 618-639):
     - Update the query body (currently using `ReferencedDocumentId`) to:
       ```csharp
       $@"SELECT d.*
           FROM dms.Document d
           INNER JOIN dms.Reference r ON d.Id = r.ParentDocumentId
                                       AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey
           INNER JOIN dms.Alias a ON a.ReferentialId = r.ReferentialId
                                   AND a.ReferentialPartitionKey = r.ReferentialPartitionKey
           WHERE a.DocumentId = $1 AND a.DocumentPartitionKey = $2
           ORDER BY d.ResourceName {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};"
       ```
     - Retain the parameter bindings (lines 629-631).
     - Optional follow-up: consider deleting the stub `return []` to enable the real query once cascade updates are ready.

4. **Align performance/utilities scripts with the new schema**
   - In `perf-dms-reference-table-direct-load/scripts/setup-test-db.sh:156-224`, drop the referenced-document column definitions, index, and FK, mirroring the production schema. Add `IX_Reference_ReferentialId` to match Step 1.
   - In `perf-dms-reference-table-direct-load/scripts/generate-test-data.sh:432-447`, remove the referenced-document columns from both the insert column list and select list; only project the four remaining fields.
   - Audit other SQL under `perf-dms-reference-table-direct-load/sql/**` that explicitly lists the dropped columns (e.g., `sql/alternatives/merge_pattern.sql:117-143`) and adjust them to the four-column layout so exploratory workloads remain runnable.

5. **Update documentation**
   - Refresh `docs/REFERENCE-VALIDATION.md:13-20` to note that the only FK governing reference validation is `FK_Reference_ReferencedAlias` after this change and that the referenced-document FK no longer exists.
