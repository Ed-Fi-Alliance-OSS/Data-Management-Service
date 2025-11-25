-- One referential ID does not resolve; expect success=false and invalid UUID returned.
BEGIN;
    SELECT * FROM dms.InsertReferences(
        p_parentDocumentId        => (SELECT Id FROM dms.Document WHERE ResourceName = 'InsertReferences2Test' ORDER BY Id DESC LIMIT 1),
        p_parentDocumentPartitionKey => 1::smallint,
        p_referentialIds          => ARRAY[
            '9a5226cd-6f14-c117-73b0-575f5505790c',
            '00000000-0000-0000-0000-000000000000'
        ]::uuid[],
        p_referentialPartitionKeys => ARRAY[12,0]::smallint[],
        p_isPureInsert             => FALSE
    );
ROLLBACK;
