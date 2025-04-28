-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.ContactStudentSchoolAuthorization(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    ContactUniqueId VARCHAR(32) NOT NULL,
    StudentUniqueId VARCHAR(32) NOT NULL,
    HierarchySchoolId BIGINT NOT NULL,
    ContactStudentSchoolAuthorizationEducationOrganizationIds JSONB NOT NULL,
    StudentContactAssociationId BIGINT NOT NULL,
    StudentContactAssociationPartitionKey SMALLINT NOT NULL,
    StudentSchoolAssociationId BIGINT NOT NULL,
    StudentSchoolAssociationPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_ContactStudentSchoolAuthorization_SSA_Document FOREIGN KEY (StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE,

    CONSTRAINT FK_ContactStudentSchoolAuthorization_SCA_Document FOREIGN KEY (StudentContactAssociationId, StudentContactAssociationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IX_ContactStudentSchoolAuthorization_ContactUniqueId
ON dms.ContactStudentSchoolAuthorization(ContactUniqueId);
