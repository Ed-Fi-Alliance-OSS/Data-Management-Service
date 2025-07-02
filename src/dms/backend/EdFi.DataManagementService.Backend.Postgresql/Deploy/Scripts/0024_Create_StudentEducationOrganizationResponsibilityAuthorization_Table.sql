-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.StudentEducationOrganizationResponsibilityAuthorization(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StudentUniqueId VARCHAR(32) NOT NULL,
    HierarchyEducationOrganizationId BIGINT NOT NULL REFERENCES dms.EducationOrganizationHierarchy(EducationOrganizationId) ON DELETE CASCADE,
    StudentEducationOrganizationResponsibilityAuthorizationEducationOrganizationIds JSONB NOT NULL,
    StudentEducationOrganizationResponsibilityAssociationId BIGINT NOT NULL,
    StudentEducationOrganizationResponsibilityAssociationPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StudentEducationOrganizationResponsibilityAuthorization_Document FOREIGN KEY (StudentEducationOrganizationResponsibilityAssociationId, StudentEducationOrganizationResponsibilityAssociationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IX_StudentEducationOrganizationResponsibilityAuthorization_StudentUniqueId
ON dms.StudentEducationOrganizationResponsibilityAuthorization(StudentUniqueId);
