## Purpose
- OFFSET/LIMIT pagination on `dms.Document` was forced to merge-scan all 16 partitions, so each high-offset request re-read hundreds of thousands of rows and spent ~450 ms.
- A narrow helper table lets the database satisfy ORDER BY/OFFSET against a cache-friendly structure and only fetch the requested JSONB rows.
- Push QueryField-based filtering onto the helper table via a compact JSONB projection and per-resource partitioning, so GIN scans stay small while removing the need for a wide GIN on `Document.EdfiDoc`.

## DDL (augmented)
```sql
-- Hash-partitioned by ResourceName so each query touches exactly one partition.
CREATE TABLE IF NOT EXISTS dms.DocumentIndex (
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceName varchar(256) NOT NULL,
    CreatedAt timestamp without time zone NOT NULL,
    QueryFields JSONB NOT NULL, -- compact projection of QueryField values
    PRIMARY KEY (DocumentPartitionKey, DocumentId, ResourceName)
) PARTITION BY HASH (ResourceName);

ALTER TABLE dms.DocumentIndex
    ADD CONSTRAINT DocumentIndex_document_fk
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.document(DocumentPartitionKey, id)
        ON DELETE CASCADE;

-- Create 32 hash partitions for ResourceName
DO $$
DECLARE i int;
BEGIN
  FOR i IN 0..31 LOOP
    EXECUTE format(
      'CREATE TABLE IF NOT EXISTS dms.DocumentIndex_p%s PARTITION OF dms.DocumentIndex FOR VALUES WITH (MODULUS 32, REMAINDER %s);',
      i, i
    );
  END LOOP;
END$$;

-- Per-partition indexes; planner prunes to one partition per query.
DO $$
DECLARE i int;
BEGIN
  FOR i IN 0..31 LOOP
    EXECUTE format(
      'CREATE INDEX IF NOT EXISTS DocumentIndex_ResourceName_CreatedAt_idx_p%s ON dms.DocumentIndex_p%s (ResourceName, CreatedAt, DocumentPartitionKey, DocumentId);',
      i, i
    );
  END LOOP;
END$$;

-- GIN on the compact QueryFields JSONB using path_ops to stay lean.
DO $$
DECLARE i int;
BEGIN
  FOR i IN 0..31 LOOP
    EXECUTE format(
      'CREATE INDEX IF NOT EXISTS DocumentIndex_QueryFields_gin_p%s ON dms.DocumentIndex_p%s USING GIN (QueryFields jsonb_path_ops);',
      i, i
    );
  END LOOP;
END$$;

-- Inserts into both tables
CREATE OR REPLACE PROCEDURE dms.InsertNewDocument(
    p_DocumentPartitionKey smallint,
    p_DocumentUuid uuid,
    p_ResourceName varchar,
    p_ResourceVersion varchar,
    p_IsDescriptor boolean,
    p_ProjectName varchar,
    p_EdfiDoc JSONB,
    p_QueryFields JSONB, -- precomputed compact projection
    p_LastModifiedTraceId varchar,
    p_CreatedAt timestamp without time zone DEFAULT NULL,
    p_LastModifiedAt timestamp without time zone DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_created_at timestamp without time zone := COALESCE(p_CreatedAt, now());
    v_last_modified_at timestamp without time zone := COALESCE(p_LastModifiedAt, v_created_at);
    v_document_id bigint;
BEGIN
    INSERT INTO dms.document (
        DocumentPartitionKey,
        DocumentUuid,
        ResourceName,
        ResourceVersion,
        IsDescriptor,
        ProjectName,
        EdfiDoc,
        CreatedAt,
        LastModifiedAt,
        LastModifiedTraceId
    )
    VALUES (
        p_DocumentPartitionKey,
        p_DocumentUuid,
        p_ResourceName,
        p_ResourceVersion,
        p_IsDescriptor,
        p_ProjectName,
        p_EdfiDoc,
        v_created_at,
        v_last_modified_at,
        p_LastModifiedTraceId
    )
    RETURNING id INTO v_document_id;

    INSERT INTO dms.DocumentIndex (
        DocumentPartitionKey,
        DocumentId,
        ResourceName,
        CreatedAt,
        QueryFields
    )
    VALUES (
        p_DocumentPartitionKey,
        v_document_id,
        p_ResourceName,
        v_created_at,
        p_QueryFields
    );
END;
$$;
```

An example backfill from existing Document table data
```sql
INSERT INTO dms.DocumentIndex (DocumentPartitionKey, DocumentId, ResourceName, CreatedAt, QueryFields)
SELECT DocumentPartitionKey,
       id,
       ResourceName,
       CreatedAt,
       /* Something like projector(QueryFields, EdfiDoc) -> JSONB, maybe in C# though */ projected_QueryFields
FROM dms.document;

ANALYZE dms.DocumentIndex;
```

New writes call `CALL dms.InsertNewDocument(...)` with the precomputed `QueryFields`.  Updates will need a similar stored proc. Deletes cascade from the FK.

## Deterministic QueryFields projection in C#
- Source of truth: `ResourceSchema.QueryFields` (see `ApiSchema/Model/QueryField.cs`) that already drive request validation.
- For each `QueryField`:
  - Enumerate its `DocumentPathsWithType`. Evaluate each JSONPath against the stored `EdfiDoc`.
  - Collect all matching values, coerce to canonical form (see below), deduplicate, and sort lexicographically to get a stable array.
  - Store as `"<queryFieldName>": <value>` where `<value>` is:
    - A primitive (string/number/boolean) when there is exactly one scalar value.
    - An array of primitives when multiple values match (sorted, unique).
  - If no value is present, omit the property entirely to avoid meaningless `null` matches.
- Canonical typing (mirrors `ValidateQueryMiddleware`):
  - `boolean`: lowercase `true`/`false`.
  - `number`: text parsed to decimal in C#, stored as JSON number.
  - `date`: `yyyy-MM-dd` string.
  - `date-time`: UTC `yyyy-MM-ddTHH:mm:ssZ` string.
  - `time`: `HH:mm:ss` string.
  - `string`: stored as-is after trimming outer whitespace.
- Multi-path fields (a QueryField mapped to multiple JSONPaths) accumulate values across all paths into the same array so `@>` remains accurate.
- Scalar vs array policy:
  - `JSONB @>` is type-sensitive. A stored array only matches an array on the RHS while a stored scalar only matches a scalar on the RHS.
  - Default policy: store scalars for single-valued fields. Store arrays for fields that can yield multiple values (collections or multi-path). When storing arrays, wrap the filter value as `["val"]` on the RHS so `@>` matches.
  - Alternative “always arrays” simplifies query building (always wrap) at a small storage/index cost. Choose if you want to avoid tracking multi vs single.

Example projection for a StudentSchoolAssociation:
```json
{
  "studentUniqueId": "004585",
  "schoolId": 255901,
  "entryDate": "2024-08-15"
}
```
If `studentUniqueId` appeared in multiple nested spots, the value would become `["004585","004585-legacy"]` (unique + sorted).

## Query pattern with filtering + paging

This uses a CTE to only touch the relevant rows on Document when getting the EdfiDoc.
```sql
WITH page AS (
    SELECT DocumentPartitionKey, DocumentId, CreatedAt
    FROM dms.DocumentIndex
    WHERE ResourceName = $1
      AND QueryFields @> $2::JSONB -- e.g., {"studentUniqueId":"004585","entryDate":"2024-08-15"}
    ORDER BY CreatedAt
    OFFSET $3 ROWS
    FETCH FIRST $4 ROWS ONLY
)
SELECT d.EdfiDoc
FROM page p
JOIN dms.document d
  ON d.DocumentPartitionKey = p.DocumentPartitionKey
 AND d.id = p.DocumentId
ORDER BY p.CreatedAt;
```
- Planner prunes to the hash partition for `$1` and uses `QueryFields` GIN to filter before the OFFSET/LIMIT walk of the ordering index.
- `totalCount` (if requested) runs the same WHERE against `DocumentIndex`.

## C# changes to support the design
- Projection:
  - Add a projection utility that takes `ResourceSchema.QueryFields` + `EdfiDoc` and returns the deterministic JSONB described above.
  - Invoke during upsert/update before calling the DB proc, and pass the projection as `p_QueryFields`.
  - For backfill, reuse the same projector to avoid drift.
- Query execution:
  - Adjust `SqlAction` query builders to target `DocumentIndex.QueryFields` instead of `Document.EdfiDoc` for filter predicates, building the `@>` filter JSON from `QueryElements` by `QueryFieldName` (document paths are no longer needed in SQL).
  - Keep pagination on `DocumentIndex.CreatedAt` and join to `Document` only for the selected page IDs.
  - Apply `totalCount` on `DocumentIndex` with identical WHERE.
- Canonical typing:
  - Reuse the existing `ValidateQueryMiddleware` canonicalization to build the filter JSON, ensuring it matches the stored projection (boolean lowercased, date/time formats, numbers as JSON numbers, UTC date-times).
  - Shared code path for projection + query filter creation prevents mismatches that would bypass the GIN.

## End-to-end example
1) Client calls `GET /.../studentSchoolAssociations?studentUniqueId=004585&entryDate=2024-08-15&offset=0&limit=100`.
2) Core validation canonicalizes the values and builds `QueryElements` for `studentUniqueId` and `entryDate`.
3) Backend builds filter JSON `{"studentUniqueId":"004585","entryDate":"2024-08-15"}` and queries `DocumentIndex` with `@>` plus `ResourceName = 'studentSchoolAssociations'`, paginating on `CreatedAt`.
4) The pruned partition uses the `QueryFields` GIN, returns ordered IDs, then joins to `Document` for the 100 rows to return.

## Next Steps
1. Implement the projector (shared by writes and backfill) and plumb `p_QueryFields` through Core to `InsertNewDocument` (and update/replace logic).
2. Add the hash-partitioned `DocumentIndex` table, per-partition indexes, and GIN on `QueryFields`, then run backfill.
3. Update query code to filter on `DocumentIndex.QueryFields` and to compute `totalCount` from `DocumentIndex`.
4. After validation, drop the wide GIN on `Document.EdfiDoc`.
