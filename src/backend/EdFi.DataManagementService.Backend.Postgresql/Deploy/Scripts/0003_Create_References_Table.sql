-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE "references" (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ParentDocumentId BIGINT NOT NULL,
  ParentDocumentPartitionKey SMALLINT NOT NULL,
  ReferentialId UUID NOT NULL,
  ReferentialPartitionKey SMALLINT NOT NULL,
  PRIMARY KEY (ParentDocumentPartitionKey, Id)
) PARTITION BY HASH(ParentDocumentPartitionKey);

CREATE TABLE References_00 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 0);
CREATE TABLE References_01 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 1);
CREATE TABLE References_02 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 2);
CREATE TABLE References_03 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 3);
CREATE TABLE References_04 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 4);
CREATE TABLE References_05 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 5);
CREATE TABLE References_06 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 6);
CREATE TABLE References_07 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 7);
CREATE TABLE References_08 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 8);
CREATE TABLE References_09 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 9);
CREATE TABLE References_10 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 10);
CREATE TABLE References_11 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 11);
CREATE TABLE References_12 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 12);
CREATE TABLE References_13 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 13);
CREATE TABLE References_14 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 14);
CREATE TABLE References_15 PARTITION OF "references" FOR VALUES WITH (MODULUS 16, REMAINDER 15);

-- DELETE/UPDATE by id lookup support
CREATE INDEX UX_References_DocumentId ON "references"(ParentDocumentPartitionKey, ParentDocumentId);

ALTER TABLE "references"
ADD CONSTRAINT FK_References_Documents FOREIGN KEY (ParentDocumentPartitionKey, ParentDocumentId)
REFERENCES Documents (DocumentPartitionKey, Id) ON DELETE CASCADE;
