-- Verify partition-scoped DML works for a different parent partition (7).
BEGIN;
    SELECT * FROM dms.InsertReferences(
        p_parentDocumentId           => (
            SELECT Id FROM dms.Document WHERE ResourceName = 'InsertReferences2Partition7Test' ORDER BY Id DESC LIMIT 1
        ),
        p_parentDocumentPartitionKey => 7::smallint,
        p_referentialIds             => ARRAY[
            '2d9c5f9b-7698-4c75-83e2-9e8de62bc111',
            '3cb4dfc8-271f-4d83-b9fb-95813bcd2222',
            '4ac3aa9d-8bd0-4f50-a42f-2d9131433333'
        ]::uuid[],
        p_referentialPartitionKeys   => ARRAY[7,8,9]::smallint[],
        p_isPureInsert               => TRUE
    );
ROLLBACK;
