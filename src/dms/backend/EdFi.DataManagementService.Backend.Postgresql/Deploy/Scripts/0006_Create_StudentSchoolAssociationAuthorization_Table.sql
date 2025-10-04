-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create table if not exists
CREATE TABLE IF NOT EXISTS dms.StudentSchoolAssociationAuthorization(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StudentUniqueId VARCHAR(32) NOT NULL,
    HierarchySchoolId BIGINT NOT NULL REFERENCES dms.EducationOrganizationHierarchy(EducationOrganizationId) ON DELETE CASCADE,
    StudentSchoolAuthorizationEducationOrganizationIds JSONB NOT NULL,
    StudentSchoolAssociationId BIGINT NOT NULL,
    StudentSchoolAssociationPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StudentSchoolAssociationAuthorization_Document FOREIGN KEY (StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_FK_StudentSchoolAssociationAuthorization_EdOrgHrchy
ON dms.StudentSchoolAssociationAuthorization(HierarchySchoolId);

CREATE INDEX IF NOT EXISTS IX_FK_StudentSchoolAssociationAuthorization_Document
ON dms.StudentSchoolAssociationAuthorization(StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey);

CREATE INDEX IF NOT EXISTS IX_StudentSchoolAssociationAuthorization_StudentUniqueId
ON dms.StudentSchoolAssociationAuthorization(StudentUniqueId);
