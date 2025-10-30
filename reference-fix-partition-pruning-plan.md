# Companion Table Plan for Partition-Pruned Reverse Lookups

## Objective
- Achieve true partition pruning for reverse lookups (finding parent documents that reference a given document) without regressing write and delete performance.
- Keep existing `dms.Reference` optimized for parent-oriented maintenance and validation; add a lean, companion table partitioned by the referenced key.

## Current State (Post-change from reference-fix-plan-3)
- `dms.Reference` (partitioned by `ParentDocumentPartitionKey`) stores:
  - `ParentDocumentId`, `ParentDocumentPartitionKey`
  - `ReferentialPartitionKey`, `AliasId`
  - `ReferencedDocumentPartitionKey`, `ReferencedDocumentId`
  - PK: `(ParentDocumentPartitionKey, Id)`
  - Indexes:
    - `(ParentDocumentPartitionKey, ParentDocumentId)` for maintenance
    - `(ReferentialPartitionKey, AliasId)` for validation FK
    - `(ReferencedDocumentPartitionKey, ReferencedDocumentId)` for selective reverse lookups
- `dms.InsertReferences` deletes old rows for parent(s), inserts new rows using a set-based `INSERT … SELECT` joined to `dms.Alias`.
- Reverse lookup queries in `SqlAction.cs` now filter on `r.ReferencedDocumentPartitionKey, r.ReferencedDocumentId` and no longer join to `Alias`.
- Limitation: reverse lookups still scan all 16 child partitions of `dms.Reference` because its partition key is the parent key.

## Design Summary
- Add `dms.ReferenceByReferenced`, partitioned by `ReferencedDocumentPartitionKey`, to support single-child partition pruning for reverse lookups.
- Maintain rows in the companion table inside `dms.InsertReferences` using set-based operations. Avoid triggers for clarity and performance.
- Use an FK from the companion to `dms.Reference` with `ON DELETE CASCADE` so parent-oriented deletes remain single-table operations (deleting from `dms.Reference` automatically removes companion rows).
- Update the two reverse lookup queries in `SqlAction.cs` to target the companion table.

## Schema Changes

### Create Companion Table
File: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/000X_Create_ReferenceByReferenced_Table.sql` (new)

```sql
-- Create main table if not exists
CREATE TABLE IF NOT EXISTS dms.ReferenceByReferenced (
  -- Partitioning key (referenced)
  ReferencedDocumentPartitionKey SMALLINT NOT NULL,
  ReferencedDocumentId BIGINT NOT NULL,

  -- Parent document identity (join target for reverse lookup results)
  ParentDocumentPartitionKey SMALLINT NOT NULL,
  ParentDocumentId BIGINT NOT NULL,

  -- Link back to reference row for cascading deletes
  ReferenceId BIGINT NOT NULL,

  -- Primary key covers lookups and enforces uniqueness of parent per referenced doc
  CONSTRAINT PK_ReferenceByReferenced PRIMARY KEY (
    ReferencedDocumentPartitionKey,
    ReferencedDocumentId,
    ParentDocumentPartitionKey,
    ParentDocumentId
  )
) PARTITION BY LIST(ReferencedDocumentPartitionKey);

-- Create partitions if not exists
DO $$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..15 LOOP
        partition_name := 'referencebyreferenced_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.ReferenceByReferenced FOR VALUES IN (%s);',
            partition_name, i
        );
    END LOOP;
END$$;

-- Support efficient ON DELETE CASCADE from dms.Reference via FK lookup
CREATE INDEX IF NOT EXISTS IX_ReferenceByReferenced_ReferenceFK
  ON dms.ReferenceByReferenced (ParentDocumentPartitionKey, ReferenceId);

-- Optional: aid joins back to parent documents (usually not needed since PK includes these columns)
-- CREATE INDEX IF NOT EXISTS IX_ReferenceByReferenced_Parent
--   ON dms.ReferenceByReferenced (ParentDocumentPartitionKey, ParentDocumentId);

-- FK for cascading deletes when a reference row is removed
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'referencebyreferenced' AND constraint_name = 'fk_rbr_reference'
    ) THEN
        ALTER TABLE dms.ReferenceByReferenced
        ADD CONSTRAINT FK_RBR_Reference FOREIGN KEY (ParentDocumentPartitionKey, ReferenceId)
        REFERENCES dms.Reference (ParentDocumentPartitionKey, Id) ON DELETE CASCADE;
    END IF;
END$$;

-- (Optional) FKs to Document for safety; omit if minimizing write overhead is paramount
-- ALTER TABLE dms.ReferenceByReferenced
--   ADD CONSTRAINT FK_RBR_ReferencedDocument FOREIGN KEY (ReferencedDocumentPartitionKey, ReferencedDocumentId)
--   REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;
-- ALTER TABLE dms.ReferenceByReferenced
--   ADD CONSTRAINT FK_RBR_ParentDocument FOREIGN KEY (ParentDocumentPartitionKey, ParentDocumentId)
--   REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;
```

Notes:
- The FK to `dms.Reference` plus the supporting index `IX_ReferenceByReferenced_ReferenceFK` makes cascaded deletes efficient.
- Keep the table lean. No `AliasId` or `ReferentialPartitionKey` needed here.

## Procedure Changes

File: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql`

Modify `dms.InsertReferences` to maintain the companion table without adding extra deletes:

1) Keep the existing delete on `dms.Reference` (cascades will clean up companion rows):
```sql
DELETE FROM dms.Reference r
USING unnest(parentDocumentIds, parentDocumentPartitionKeys) AS d (Id, DocumentPartitionKey)
WHERE d.Id = r.ParentDocumentId
  AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey;
```

2) Insert into `dms.Reference` as today, but capture inserted rows:
```sql
WITH payload AS (
  SELECT
    ids.documentId,
    ids.documentPartitionKey,
    ids.referentialId,
    ids.referentialPartitionKey,
    a.Id AS aliasId,
    a.DocumentId AS aliasDocumentId,
    a.DocumentPartitionKey AS aliasDocumentPartitionKey
  FROM unnest(parentDocumentIds, parentDocumentPartitionKeys, referentialIds, referentialPartitionKeys) AS
    ids(documentId, documentPartitionKey, referentialId, referentialPartitionKey)
  LEFT JOIN dms.Alias a ON a.ReferentialId = ids.referentialId
                        AND a.ReferentialPartitionKey = ids.referentialPartitionKey
), inserted AS (
  INSERT INTO dms.Reference (
    ParentDocumentId,
    ParentDocumentPartitionKey,
    AliasId,
    ReferentialPartitionKey,
    ReferencedDocumentId,
    ReferencedDocumentPartitionKey
  )
  SELECT
    documentId,
    documentPartitionKey,
    aliasId,
    referentialPartitionKey,
    aliasDocumentId,
    aliasDocumentPartitionKey
  FROM payload
  WHERE aliasId IS NOT NULL
  RETURNING Id,
            ParentDocumentId,
            ParentDocumentPartitionKey,
            ReferencedDocumentId,
            ReferencedDocumentPartitionKey
)
INSERT INTO dms.ReferenceByReferenced (
  ReferencedDocumentPartitionKey,
  ReferencedDocumentId,
  ParentDocumentPartitionKey,
  ParentDocumentId,
  ReferenceId
)
SELECT
  inserted.ReferencedDocumentPartitionKey,
  inserted.ReferencedDocumentId,
  inserted.ParentDocumentPartitionKey,
  inserted.ParentDocumentId,
  inserted.Id
FROM inserted;

-- Return invalid referential IDs as before
SELECT payload.referentialId
FROM payload
WHERE payload.aliasId IS NULL;
```

Notes:
- No explicit delete from `ReferenceByReferenced` is needed; cascading deletes from `dms.Reference` cover it.
- All operations remain set-based.

## Application Query Changes

File: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs`

Update reverse lookup queries to target the companion table (for single-partition scans):

1) `FindReferencingResourceNamesByDocumentUuid`
Change inner subquery to read from `dms.ReferenceByReferenced` and join to the referenced `Document` by its identity:

```csharp
SELECT d.ResourceName
  FROM dms.Document d
  INNER JOIN (
      SELECT rbr.ParentDocumentId, rbr.ParentDocumentPartitionKey
        FROM dms.ReferenceByReferenced rbr
        INNER JOIN dms.Document d2
          ON d2.Id = rbr.ReferencedDocumentId
         AND d2.DocumentPartitionKey = rbr.ReferencedDocumentPartitionKey
       WHERE d2.DocumentUuid = $1
         AND d2.DocumentPartitionKey = $2
  ) AS re
    ON re.ParentDocumentId = d.Id
   AND re.ParentDocumentPartitionKey = d.DocumentPartitionKey
 ORDER BY d.ResourceName {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};
```

2) `FindReferencingDocumentsByDocumentId`
Collapse to a two-table join using the companion:

```csharp
SELECT d.*
  FROM dms.Document d
  INNER JOIN dms.ReferenceByReferenced rbr
    ON d.Id = rbr.ParentDocumentId
   AND d.DocumentPartitionKey = rbr.ParentDocumentPartitionKey
 WHERE rbr.ReferencedDocumentId = $1
   AND rbr.ReferencedDocumentPartitionKey = $2
 ORDER BY d.ResourceName {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};
```

## Deployment Plan

1) Ship new DDL scripts:
   - `000X_Create_ReferenceByReferenced_Table.sql`

2) Update procedure and application code:
   - Deploy updated `0010_Create_Insert_References_Procedure.sql`.
   - Deploy the `SqlAction.cs` changes that switch reverse lookups to `ReferenceByReferenced`.

3) Verify:
   - EXPLAIN ANALYZE both reverse lookups; expect single-child `Append` with `Index Only Scan` on the companion’s PK.
   - Insert/update/delete documents and references; verify reverse lookup results and that deletes cascade from `dms.Reference` to the companion.

4) Optional optimizations:
   - Make the companion table UNLOGGED in non-production to reduce WAL during load testing.
   - Tune `autovacuum_vacuum_scale_factor` on small partitions if churn is high.

## Expected Performance
- Reverse lookups prune to exactly one partition of `dms.ReferenceByReferenced` given `(ReferencedDocumentPartitionKey, ReferencedDocumentId)`.
- Index-only scans on the companion PK typically avoid heap fetches; no `Alias` join on read path.
- Parent-side deletes unchanged in cost; cascades remove companion rows efficiently through the FK with supporting index.
