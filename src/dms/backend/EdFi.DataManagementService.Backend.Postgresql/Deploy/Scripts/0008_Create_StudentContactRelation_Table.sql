-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create table if not exists
CREATE TABLE IF NOT EXISTS dms.StudentContactRelation(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StudentUniqueId VARCHAR(32) NOT NULL,
    ContactUniqueId VARCHAR(32) NOT NULL,
    StudentContactAssociationDocumentId BIGINT NOT NULL,
    StudentContactAssociationDocumentPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StudentContactAssociation_Document FOREIGN KEY (StudentContactAssociationDocumentId, StudentContactAssociationDocumentPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE,
    CONSTRAINT UQ_StudentContactRelation_Composite UNIQUE (
        StudentUniqueId,
        ContactUniqueId,
        StudentContactAssociationDocumentId,
        StudentContactAssociationDocumentPartitionKey
    )
);

CREATE INDEX IF NOT EXISTS IX_StudentContactRelation_StudentUniqueId ON dms.StudentContactRelation (StudentUniqueId);
CREATE INDEX IF NOT EXISTS IX_StudentContactRelation_ContactUniqueId ON dms.StudentContactRelation (ContactUniqueId);
