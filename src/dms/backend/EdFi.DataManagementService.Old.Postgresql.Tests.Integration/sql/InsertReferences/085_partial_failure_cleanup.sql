-- Mixed resolved/unresolved referentials should return false, persist no references, and clear ReferenceStage for the session.
BEGIN;
    WITH doc AS (
        SELECT Id
        FROM dms.Document
        WHERE ResourceName = 'InsertReferences2Test'
        ORDER BY Id DESC
        LIMIT 1
    ), resolved AS (
        SELECT
            '9a5226cd-6f14-c117-73b0-575f5505790c'::uuid AS referential_id,
            12::smallint AS partition_key
    ), missing AS (
        SELECT gen_random_uuid() AS referential_id
    )
    SELECT *
    FROM dms.InsertReferences(
        p_parentDocumentId           => (SELECT Id FROM doc),
        p_parentDocumentPartitionKey => 1::smallint,
        p_referentialIds             => ARRAY[
            (SELECT referential_id FROM resolved),
            (SELECT referential_id FROM missing)
        ]::uuid[],
        p_referentialPartitionKeys   => ARRAY[
            (SELECT partition_key FROM resolved),
            12::smallint
        ]::smallint[],
        p_isPureInsert               => FALSE
    );

    -- Verify no references were persisted after the failed call.
    SELECT COUNT(*) AS reference_count_after_failure
    FROM dms.Reference
    WHERE ParentDocumentPartitionKey = 1
      AND ParentDocumentId = (SELECT Id FROM doc);

    -- Ensure staging rows were cleared even though the call failed.
    SELECT COUNT(*) AS staged_rows_post_clear
    FROM dms.ReferenceStage
    WHERE SessionId = pg_backend_pid()
      AND ParentDocumentPartitionKey = 1
      AND ParentDocumentId = (SELECT Id FROM doc);
ROLLBACK;
