-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create table if not exists
CREATE TABLE IF NOT EXISTS dms.StudentEducationOrganizationResponsibilityAuthorization(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StudentUniqueId VARCHAR(32) NOT NULL,
    HierarchyEdOrgId BIGINT NOT NULL REFERENCES dms.EducationOrganizationHierarchy(EducationOrganizationId) ON DELETE CASCADE,
    StudentEdOrgResponsibilityAuthorizationEdOrgIds JSONB NOT NULL,
    StudentEdOrgResponsibilityAssociationId BIGINT NOT NULL,
    StudentEdOrgResponsibilityAssociationPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StudentEdOrgResponsibilityAuthorization_Document FOREIGN KEY (StudentEdOrgResponsibilityAssociationId, StudentEdOrgResponsibilityAssociationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_StudentEdOrgResponsibilityAuthorization_StudentUniqueId
ON dms.StudentEducationOrganizationResponsibilityAuthorization(StudentUniqueId);