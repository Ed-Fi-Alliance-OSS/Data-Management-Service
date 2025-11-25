-- Non-pure insert that should detect no changes and skip DML.
BEGIN;
    SELECT * FROM dms.InsertReferences(
        p_parentDocumentId        => (SELECT Id FROM dms.Document WHERE ResourceName = 'InsertReferences2Test' ORDER BY Id DESC LIMIT 1),
        p_parentDocumentPartitionKey => 1::smallint,
        p_referentialIds          => ARRAY[
            '9a5226cd-6f14-c117-73b0-575f5505790c',
            '0ae6e94d-d446-28f8-da03-240821ed958c',
            'b9c20540-8759-0edc-feec-1c7775711621',
            '0916dd27-b187-2c61-74c7-d88923aa800f',
            'e3f62f16-1c78-4d6d-4da0-9f10457a7f7a'
        ]::uuid[],
        p_referentialPartitionKeys => ARRAY[12,12,1,15,10]::smallint[],
        p_isPureInsert             => FALSE
    );
ROLLBACK;
