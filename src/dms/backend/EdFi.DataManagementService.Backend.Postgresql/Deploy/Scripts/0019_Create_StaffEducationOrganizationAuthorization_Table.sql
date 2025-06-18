-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.StaffEducationOrganizationAuthorization(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    StaffUniqueId VARCHAR(32) NOT NULL,
    HierarchyEdOrgId BIGINT NOT NULL REFERENCES dms.EducationOrganizationHierarchy(EducationOrganizationId) ON DELETE CASCADE,
    StaffEducationOrganizationAuthorizationEdOrgIds JSONB NOT NULL,
    StaffEducationOrganizationId BIGINT NOT NULL,
    StaffEducationOrganizationPartitionKey SMALLINT NOT NULL,
    CONSTRAINT FK_StaffEducationOrganizationAuthorization_Document FOREIGN KEY (StaffEducationOrganizationId, StaffEducationOrganizationPartitionKey)
        REFERENCES dms.Document(Id, DocumentPartitionKey) ON DELETE CASCADE
);

CREATE INDEX IX_StaffEducationOrganizationAuthorization_StaffUniqueId
ON dms.StaffEducationOrganizationAuthorization(StaffUniqueId);

