-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.Reference (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ParentDocumentId BIGINT NOT NULL,
  ParentDocumentPartitionKey SMALLINT NOT NULL,
  ReferencedDocumentId BIGINT NULL,
  ReferencedDocumentPartitionKey SMALLINT NULL,
  ReferentialId UUID NOT NULL,
  ReferentialPartitionKey SMALLINT NOT NULL,
  PRIMARY KEY (ParentDocumentPartitionKey, Id)
) PARTITION BY HASH(ParentDocumentPartitionKey);

CREATE TABLE dms.Reference_00 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 0);
CREATE TABLE dms.Reference_01 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 1);
CREATE TABLE dms.Reference_02 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 2);
CREATE TABLE dms.Reference_03 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 3);
CREATE TABLE dms.Reference_04 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 4);
CREATE TABLE dms.Reference_05 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 5);
CREATE TABLE dms.Reference_06 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 6);
CREATE TABLE dms.Reference_07 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 7);
CREATE TABLE dms.Reference_08 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 8);
CREATE TABLE dms.Reference_09 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 9);
CREATE TABLE dms.Reference_10 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 10);
CREATE TABLE dms.Reference_11 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 11);
CREATE TABLE dms.Reference_12 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 12);
CREATE TABLE dms.Reference_13 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 13);
CREATE TABLE dms.Reference_14 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 14);
CREATE TABLE dms.Reference_15 PARTITION OF dms.Reference FOR VALUES WITH (MODULUS 16, REMAINDER 15);

-- Lookup support for DELETE/UPDATE by id
CREATE INDEX UX_Reference_ParentDocumentId ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId);

-- Lookup support for DELETE failure due to existing references - cross partition index
CREATE INDEX UX_Reference_ReferencedDocumentId ON dms.Reference (ReferencedDocumentPartitionKey, ReferencedDocumentId);

-- FK back to parent document
ALTER TABLE dms.Reference
ADD CONSTRAINT FK_Reference_ParentDocument FOREIGN KEY (ParentDocumentPartitionKey, ParentDocumentId)
REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;

-- FK back to document being referenced - can be null if reference validation is turned off
ALTER TABLE dms.Reference
ADD CONSTRAINT FK_Reference_ReferencedDocument FOREIGN KEY (ReferencedDocumentPartitionKey, ReferencedDocumentId)
REFERENCES dms.Document (DocumentPartitionKey, Id);
