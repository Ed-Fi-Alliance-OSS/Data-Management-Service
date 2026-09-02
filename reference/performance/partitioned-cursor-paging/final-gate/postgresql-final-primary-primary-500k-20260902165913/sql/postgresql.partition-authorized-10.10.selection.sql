WITH candidates AS (
SELECT r."DocumentId"
FROM "edfi"."Student" r
WHERE
    ((r."DocumentId" IN (SELECT t0."Student_DocumentId" FROM "auth"."EducationOrganizationIdToStudentDocumentId" t0 WHERE t0."SourceEducationOrganizationId" = ANY(@ClaimEducationOrganizationIds))))
),
ranked AS (
    SELECT
        pc."DocumentId",
        ROW_NUMBER() OVER (ORDER BY pc."DocumentId") AS "row_number",
        COUNT(*) OVER () AS "candidate_count"
    FROM candidates pc
),
sized AS (
    SELECT
        pr."DocumentId",
        pr."row_number",
        GREATEST(CAST(CEIL(CAST(pr."candidate_count" AS numeric) / CAST(@number AS numeric)) AS bigint), @minimumPartitionSize) AS "partition_size"
    FROM ranked pr
)
SELECT ps."DocumentId"
FROM sized ps
WHERE (ps."row_number" - 1) % ps."partition_size" = 0
ORDER BY ps."DocumentId" ASC;
