-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE TABLE dms.EducationOrganizationHierarchyTermsLookup(
    PrimaryKey BIGINT UNIQUE  GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    Id TEXT NOT NULL UNIQUE,
    Hierarchy JSONB NOT NULL
)
