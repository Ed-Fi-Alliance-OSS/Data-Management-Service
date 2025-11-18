-- Uses a large payload (25 references) to exercise duplicate detection and upsert path under realistic volume.
BEGIN;
    WITH doc AS (
        SELECT Id
        FROM dms.Document
        WHERE ResourceName = 'InsertReferences2Test'
        ORDER BY Id DESC
        LIMIT 1
    ), payload AS (
        SELECT
            array_agg(sub.referentialid ORDER BY sub.referentialid) AS referential_ids,
            array_agg(sub.referentialpartitionkey ORDER BY sub.referentialid) AS referential_partition_keys
        FROM (
            SELECT a.referentialid, a.referentialpartitionkey
            FROM dms.Alias a
            WHERE a.DocumentPartitionKey = 1
              AND a.DocumentId = (SELECT Id FROM doc)
            ORDER BY a.referentialid
            LIMIT 25
        ) sub
    )
    SELECT *
    FROM dms.InsertReferences(
        p_parentDocumentId           => (SELECT Id FROM doc),
        p_parentDocumentPartitionKey => 1::smallint,
        p_referentialIds             => (SELECT referential_ids FROM payload),
        p_referentialPartitionKeys   => (SELECT referential_partition_keys FROM payload),
        p_isPureInsert               => TRUE
    );
ROLLBACK;
