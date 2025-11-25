-- Forces an error after InsertReferences runs to ensure subtransactions keep reference counts unchanged.
BEGIN;
    DO $$
    DECLARE
        doc_id BIGINT := (
            SELECT Id FROM dms.Document WHERE ResourceName = 'InsertReferences2Test' ORDER BY Id DESC LIMIT 1
        );
        before_count BIGINT;
        after_count BIGINT;
    BEGIN
        SELECT count(*)
        INTO before_count
        FROM dms.Reference
        WHERE ParentDocumentPartitionKey = 1
          AND ParentDocumentId = doc_id;

        BEGIN
            PERFORM dms.InsertReferences(
                p_parentDocumentId           => doc_id,
                p_parentDocumentPartitionKey => 1::smallint,
                p_referentialIds             => ARRAY[
                    '9a5226cd-6f14-c117-73b0-575f5505790c',
                    '0ae6e94d-d446-28f8-da03-240821ed958c'
                ]::uuid[],
                p_referentialPartitionKeys   => ARRAY[12,12]::smallint[],
                p_isPureInsert               => TRUE
            );
            RAISE EXCEPTION 'forcing rollback for test';
        EXCEPTION
            WHEN others THEN
                RAISE NOTICE 'Captured expected failure: %', SQLERRM;
        END;

        SELECT count(*)
        INTO after_count
        FROM dms.Reference
        WHERE ParentDocumentPartitionKey = 1
          AND ParentDocumentId = doc_id;

        IF after_count <> before_count THEN
            RAISE EXCEPTION 'Row count changed despite rollback (before %, after %)', before_count, after_count;
        ELSE
            RAISE NOTICE 'Counts unchanged at % rows after forced rollback.', after_count;
        END IF;
    END $$;
ROLLBACK;
