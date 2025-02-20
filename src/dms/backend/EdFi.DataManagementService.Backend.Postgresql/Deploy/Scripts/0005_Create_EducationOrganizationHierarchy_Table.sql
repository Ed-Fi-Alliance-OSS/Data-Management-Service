-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.EducationOrganizationHierarchy(
    Id BIGINT UNIQUE GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    ProjectName VARCHAR(256) NOT NULL,
    ResourceName VARCHAR(256) NOT NULL,
    EducationOrganizationId TEXT NOT NULL UNIQUE,
    ParentId BIGINT REFERENCES dms.EducationOrganizationHierarchy(Id) ON DELETE CASCADE
)
