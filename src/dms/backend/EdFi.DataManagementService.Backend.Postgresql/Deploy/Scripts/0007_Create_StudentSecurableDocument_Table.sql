-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.StudentSecurableDocument(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StudentUniqueId VARCHAR(32) NOT NULL,
    StudentSecurableDocumentId BIGINT NOT NULL,
    StudentSecurableDocumentPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StudentSecurableDocument_Document FOREIGN KEY (StudentSecurableDocumentId, StudentSecurableDocumentPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);
