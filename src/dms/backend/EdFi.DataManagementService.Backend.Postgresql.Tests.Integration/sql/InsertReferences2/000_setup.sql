-- Creates helper data used by the InsertReferences2 test suite.
-- Run once per test run; safe to rerun thanks to ON CONFLICT clauses.
DO $$
DECLARE
    doc_id BIGINT;
    doc_id_partition7 BIGINT;
BEGIN
    INSERT INTO dms.Document (
        DocumentPartitionKey,
        DocumentUuid,
        ResourceName,
        ResourceVersion,
        IsDescriptor,
        ProjectName,
        EdfiDoc,
        LastModifiedTraceId
    ) VALUES (
        1,
        gen_random_uuid(),
        'InsertReferences2Test',
        '1.0',
        FALSE,
        'InsertReferences2',
        '{}'::jsonb,
        'insert-references-cte'
    )
    ON CONFLICT (DocumentPartitionKey, DocumentUuid) DO NOTHING
    RETURNING Id
    INTO doc_id;

    IF doc_id IS NULL THEN
        SELECT Id
        INTO doc_id
        FROM dms.Document
        WHERE DocumentPartitionKey = 1
          AND ResourceName = 'InsertReferences2Test'
        ORDER BY Id DESC
        LIMIT 1;
    END IF;

    INSERT INTO dms.Alias (
        ReferentialPartitionKey,
        ReferentialId,
        DocumentId,
        DocumentPartitionKey
    ) VALUES
        (12, '9a5226cd-6f14-c117-73b0-575f5505790c', doc_id, 1),
        (12, '0ae6e94d-d446-28f8-da03-240821ed958c', doc_id, 1),
        (1,  'b9c20540-8759-0edc-feec-1c7775711621', doc_id, 1),
        (15, '0916dd27-b187-2c61-74c7-d88923aa800f', doc_id, 1),
        (10, 'e3f62f16-1c78-4d6d-4da0-9f10457a7f7a', doc_id, 1)
    ON CONFLICT (ReferentialPartitionKey, ReferentialId) DO NOTHING;

    INSERT INTO dms.Document (
        DocumentPartitionKey,
        DocumentUuid,
        ResourceName,
        ResourceVersion,
        IsDescriptor,
        ProjectName,
        EdfiDoc,
        LastModifiedTraceId
    ) VALUES (
        7,
        gen_random_uuid(),
        'InsertReferences2Partition7Test',
        '1.0',
        FALSE,
        'InsertReferences2',
        '{}'::jsonb,
        'insert-references-cte'
    )
    ON CONFLICT (DocumentPartitionKey, DocumentUuid) DO NOTHING
    RETURNING Id
    INTO doc_id_partition7;

    IF doc_id_partition7 IS NULL THEN
        SELECT Id
        INTO doc_id_partition7
        FROM dms.Document
        WHERE DocumentPartitionKey = 7
          AND ResourceName = 'InsertReferences2Partition7Test'
        ORDER BY Id DESC
        LIMIT 1;
    END IF;

    INSERT INTO dms.Alias (
        ReferentialPartitionKey,
        ReferentialId,
        DocumentId,
        DocumentPartitionKey
    ) VALUES
        (7, '2d9c5f9b-7698-4c75-83e2-9e8de62bc111', doc_id_partition7, 7),
        (8, '3cb4dfc8-271f-4d83-b9fb-95813bcd2222', doc_id_partition7, 7),
        (9, '4ac3aa9d-8bd0-4f50-a42f-2d9131433333', doc_id_partition7, 7)
    ON CONFLICT (ReferentialPartitionKey, ReferentialId) DO NOTHING;

    INSERT INTO dms.Alias (
        ReferentialPartitionKey,
        ReferentialId,
        DocumentId,
        DocumentPartitionKey
    )
    SELECT
        (gs % 16)::smallint,
        gen_random_uuid(),
        doc_id,
        1
    FROM generate_series(1, 40) AS gs
    ON CONFLICT (ReferentialPartitionKey, ReferentialId) DO NOTHING;
END $$;
