-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.Document (
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
) PARTITION BY HASH(DocumentPartitionKey);

CREATE TABLE dms.Document_00 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 0);
CREATE TABLE dms.Document_01 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 1);
CREATE TABLE dms.Document_02 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 2);
CREATE TABLE dms.Document_03 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 3);
CREATE TABLE dms.Document_04 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 4);
CREATE TABLE dms.Document_05 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 5);
CREATE TABLE dms.Document_06 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 6);
CREATE TABLE dms.Document_07 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 7);
CREATE TABLE dms.Document_08 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 8);
CREATE TABLE dms.Document_09 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 9);
CREATE TABLE dms.Document_10 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 10);
CREATE TABLE dms.Document_11 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 11);
CREATE TABLE dms.Document_12 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 12);
CREATE TABLE dms.Document_13 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 13);
CREATE TABLE dms.Document_14 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 14);
CREATE TABLE dms.Document_15 PARTITION OF dms.Document FOR VALUES WITH (MODULUS 16, REMAINDER 15);

-- GET/UPDATE/DELETE by id lookup support, DocumentUuid uniqueness validation
CREATE UNIQUE INDEX UX_Document_DocumentUuid ON dms.Document (DocumentPartitionKey, DocumentUuid);
-- Authorization tables cascade delete support
CREATE UNIQUE INDEX UX_Document_DocumentId ON dms.Document (DocumentPartitionKey, Id);

-- Set REPLICA IDENTITY FULL to all partitions so all columns are
-- available through replication to e.g. Debezium
ALTER TABLE dms.Document REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_00 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_01 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_02 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_03 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_04 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_05 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_06 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_07 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_08 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_09 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_10 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_11 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_12 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_13 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_14 REPLICA IDENTITY FULL;
ALTER TABLE dms.Document_15 REPLICA IDENTITY FULL;

