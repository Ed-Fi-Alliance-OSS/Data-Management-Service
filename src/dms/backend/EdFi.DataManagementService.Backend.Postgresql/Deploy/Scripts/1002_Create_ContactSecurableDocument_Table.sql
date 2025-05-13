-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.ContactSecurableDocument(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    ContactUniqueId VARCHAR(32) NOT NULL,
    ContactSecurableDocumentId BIGINT NOT NULL,
    ContactSecurableDocumentPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_ContactSecurableDocument_Document FOREIGN KEY (ContactSecurableDocumentId, ContactSecurableDocumentPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IX_ContactSecurableDocument_ContactSecurableDocumentId ON dms.ContactSecurableDocument (ContactUniqueId);
