-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.StudentSchoolAssociationAuthorization(
    Id BIGINT UNIQUE  GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StudentUniqueId VARCHAR(32) NOT NULL,
    SchoolId BIGINT NOT NULL REFERENCES dms.EducationOrganizationHierarchyTermsLookup(Id) ON DELETE CASCADE,
    StudentSchoolAssociationId BIGINT NOT NULL,
    StudentSchoolAssociationPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StudentSchoolAssociationAuthorization_Document FOREIGN KEY (StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IX_StudentSchoolAssociationAuthorization_StudentUniqueId
ON dms.StudentSchoolAssociationAuthorization(StudentUniqueId);
