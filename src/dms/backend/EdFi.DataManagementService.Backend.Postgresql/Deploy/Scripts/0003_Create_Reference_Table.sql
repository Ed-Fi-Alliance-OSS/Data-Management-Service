-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create main table if not exists
CREATE TABLE IF NOT EXISTS dms.Reference (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ParentDocumentId BIGINT NOT NULL,
  ParentDocumentPartitionKey SMALLINT NOT NULL,
  -- ReferencedDocumentId BIGINT NULL,
  -- ReferencedDocumentPartitionKey SMALLINT NULL,
  ReferentialId UUID NOT NULL,
  ReferentialPartitionKey SMALLINT NOT NULL,
  PRIMARY KEY (ParentDocumentPartitionKey, Id)
) PARTITION BY HASH(ParentDocumentPartitionKey);

-- Create partitions if not exists
DO $$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..15 LOOP
        partition_name := 'reference_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER %s);',
            partition_name, i
        );
    END LOOP;
END$$;

-- Lookup support for DELETE/UPDATE by id
CREATE INDEX IF NOT EXISTS UX_Reference_ParentDocumentId ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId);

-- Lookup support for DELETE failure due to existing references - cross partition index
-- CREATE INDEX IF NOT EXISTS UX_Reference_ReferencedDocumentId ON dms.Reference (ReferencedDocumentPartitionKey, ReferencedDocumentId);

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

-- FK back to document being referenced - can be null if reference validation is turned off
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'reference' AND constraint_name = 'fk_reference_referenceddocument'
    ) THEN
        -- ALTER TABLE dms.Reference
        -- ADD CONSTRAINT FK_Reference_ReferencedDocument FOREIGN KEY (ReferencedDocumentPartitionKey, ReferencedDocumentId)
        -- REFERENCES dms.Document (DocumentPartitionKey, Id);
    END IF;
END$$;

