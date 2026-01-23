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
            p_referentialIds             => ARRAY[
                '9a5226cd-6f14-c117-73b0-575f5505790c',
                '9a5226cd-6f14-c117-73b0-575f5505790c'
            ]::uuid[],
            p_referentialPartitionKeys   => ARRAY[12,12]::smallint[],
            p_isPureInsert               => FALSE
        );
        RAISE EXCEPTION 'Expected duplicate payload to fail';
    EXCEPTION
        WHEN others THEN
            RAISE NOTICE 'Expected failure (duplicate payload): %', SQLERRM;
    END;
END $$;
