-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create main table if not exists
CREATE TABLE IF NOT EXISTS dms.Reference (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ParentDocumentId BIGINT NOT NULL,
  ParentDocumentPartitionKey SMALLINT NOT NULL,
  ReferentialPartitionKey SMALLINT NOT NULL,
  AliasId BIGINT NOT NULL,
  PRIMARY KEY (ParentDocumentPartitionKey, Id)
) PARTITION BY LIST(ParentDocumentPartitionKey);

-- Create partitions if not exists
DO $$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..15 LOOP
        partition_name := 'reference_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Reference FOR VALUES IN (%s);',
            partition_name, i
        );
    END LOOP;
END$$;

-- Lookup support for DELETE/UPDATE by id
CREATE INDEX IF NOT EXISTS UX_Reference_ParentDocumentId ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId);

-- Lookup support for reverse reference resolution by alias
CREATE INDEX IF NOT EXISTS IX_Reference_AliasId ON dms.Reference (ReferentialPartitionKey, AliasId);

-- FK back to parent document
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'reference' AND constraint_name = 'fk_reference_parentdocument'
    ) THEN
        ALTER TABLE dms.Reference
        ADD CONSTRAINT FK_Reference_ParentDocument FOREIGN KEY (ParentDocumentPartitionKey, ParentDocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;
    END IF;
END$$;
