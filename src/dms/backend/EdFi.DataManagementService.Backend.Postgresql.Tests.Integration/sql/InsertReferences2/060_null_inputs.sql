-- Null referential id or partition key should raise 23502 matching temp table constraint.
DO $$
DECLARE
    doc_id BIGINT := (
        SELECT Id FROM dms.Document WHERE ResourceName = 'InsertReferences2Test' ORDER BY Id DESC LIMIT 1
    );
BEGIN
    BEGIN
        PERFORM dms.InsertReferences(
            p_parentDocumentId           => doc_id,
            p_parentDocumentPartitionKey => 1::smallint,
            p_referentialIds             => ARRAY[NULL::uuid]::uuid[],
            p_referentialPartitionKeys   => ARRAY[1]::smallint[],
            p_isPureInsert               => FALSE
        );
        RAISE EXCEPTION 'Expected null referentialId to fail';
    EXCEPTION
        WHEN others THEN
            RAISE NOTICE 'Expected failure (null referentialId): %', SQLERRM;
    END;

    BEGIN
        PERFORM dms.InsertReferences(
            p_parentDocumentId           => doc_id,
            p_parentDocumentPartitionKey => 1::smallint,
            p_referentialIds             => ARRAY['9a5226cd-6f14-c117-73b0-575f5505790c']::uuid[],
            p_referentialPartitionKeys   => ARRAY[NULL::smallint]::smallint[],
            p_isPureInsert               => FALSE
        );
        RAISE EXCEPTION 'Expected null partition key to fail';
    EXCEPTION
        WHEN others THEN
            RAISE NOTICE 'Expected failure (null partition key): %', SQLERRM;
    END;
END $$;
