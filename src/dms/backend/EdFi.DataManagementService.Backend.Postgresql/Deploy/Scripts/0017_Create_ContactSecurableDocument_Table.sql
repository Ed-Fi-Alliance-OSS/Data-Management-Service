-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create table if not exists
CREATE TABLE IF NOT EXISTS dms.ContactSecurableDocument(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    ContactUniqueId VARCHAR(32) NOT NULL,
    ContactSecurableDocumentId BIGINT NOT NULL,
    ContactSecurableDocumentPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_ContactSecurableDocument_Document FOREIGN KEY (ContactSecurableDocumentId, ContactSecurableDocumentPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_FK_ContactSecurableDocument_Document ON dms.ContactSecurableDocument (ContactSecurableDocumentId, ContactSecurableDocumentPartitionKey);
CREATE INDEX IF NOT EXISTS IX_ContactSecurableDocument_ContactUniqueId ON dms.ContactSecurableDocument (ContactUniqueId);
