-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE IF NOT EXISTS dms.Document (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  DocumentPartitionKey SMALLINT NOT NULL,
  DocumentUuid UUID NOT NULL,
  ResourceName VARCHAR(256) NOT NULL,
  ResourceVersion VARCHAR(64) NOT NULL,
  IsDescriptor BOOLEAN NOT NULL,
  ProjectName VARCHAR(256) NOT NULL,
  EdfiDoc JSONB NOT NULL,
  SecurityElements JSONB NOT NULL,
  StudentSchoolAuthorizationEdOrgIds JSONB NULL,
  StudentEdOrgResponsibilityAuthorizationIds JSONB NULL,
  ContactStudentSchoolAuthorizationEdOrgIds JSONB NULL,
  StaffEducationOrganizationAuthorizationEdOrgIds JSONB NULL,
  CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  LastModifiedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  LastModifiedTraceId VARCHAR(128) NOT NULL,
  PRIMARY KEY (DocumentPartitionKey, Id)
) PARTITION BY LIST(DocumentPartitionKey);

-- Create partitions if not exists
DO $$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..15 LOOP
        partition_name := 'document_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Document FOR VALUES IN (%s);',
            partition_name, i
        );
    END LOOP;
END$$;

-- Create unique indexes if not exists
CREATE UNIQUE INDEX IF NOT EXISTS UX_Document_DocumentUuid ON dms.Document (DocumentPartitionKey, DocumentUuid);

-- Set REPLICA IDENTITY FULL to all partitions
DO $$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    EXECUTE 'ALTER TABLE IF EXISTS dms.Document REPLICA IDENTITY FULL;';
    FOR i IN 0..15 LOOP
        partition_name := 'document_' || to_char(i, 'FM00');
        EXECUTE format('ALTER TABLE IF EXISTS dms.%I REPLICA IDENTITY FULL;', partition_name);
    END LOOP;
END$$;


