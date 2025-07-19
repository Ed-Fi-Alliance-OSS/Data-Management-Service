-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Create table if not exists
CREATE TABLE IF NOT EXISTS dms.EducationOrganizationHierarchy(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    ProjectName VARCHAR(256) NOT NULL,
    ResourceName VARCHAR(256) NOT NULL,
    EducationOrganizationId BIGINT NOT NULL,
    ParentId BIGINT REFERENCES dms.EducationOrganizationHierarchy(Id) ON DELETE CASCADE,
    DocumentId BIGINT NOT NULL,
    DocumentPartitionKey SMALLINT NOT NULL,
    UNIQUE (ProjectName, ResourceName, EducationOrganizationId, ParentId)
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_EducationOrganizationHierarchy_EducationOrganizationId ON dms.EducationOrganizationHierarchy (EducationOrganizationId);
