-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create main table if not exists
CREATE TABLE IF NOT EXISTS dms.Alias (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ReferentialPartitionKey SMALLINT NOT NULL,
  ReferentialId UUID NOT NULL,
  DocumentId BIGINT NOT NULL,
  DocumentPartitionKey SMALLINT NOT NULL,
  PRIMARY KEY (ReferentialPartitionKey, Id)
) PARTITION BY HASH(ReferentialPartitionKey);

-- Create partitions if not exists
DO $$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..15 LOOP
        partition_name := 'alias_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Alias FOR VALUES WITH (MODULUS 16, REMAINDER %s);',
            partition_name, i
        );
    END LOOP;
END$$;

-- Referential ID uniqueness validation and reference insert into References support
CREATE UNIQUE INDEX IF NOT EXISTS UX_Alias_ReferentialId ON dms.Alias (ReferentialPartitionKey, ReferentialId);

-- Add FK constraint if not exists
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'alias' AND constraint_name = 'fk_alias_document'
    ) THEN
        ALTER TABLE dms.Alias
        ADD CONSTRAINT FK_Alias_Document FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;

        CREATE INDEX IF NOT EXISTS IX_FK_Alias_Document ON dms.Alias (DocumentPartitionKey, DocumentId);
    END IF;
END$$;
