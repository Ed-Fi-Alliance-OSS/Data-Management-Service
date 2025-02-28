-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- This table will hold the hierarchy of education organization ids as a means of efficient search filtering.
-- The hierarchy is stored as a JSONB array of ids with its own id as the first element in the array.
CREATE TABLE dms.EducationOrganizationHierarchyTermsLookup(
    PrimaryKey BIGINT UNIQUE  GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
    Id INT NOT NULL UNIQUE,
    Hierarchy JSONB NOT NULL
);

-- Set REPLICA IDENTITY FULL so all columns are
-- available through replication to e.g. Debezium
ALTER TABLE dms.EducationOrganizationHierarchyTermsLookup REPLICA IDENTITY FULL;
