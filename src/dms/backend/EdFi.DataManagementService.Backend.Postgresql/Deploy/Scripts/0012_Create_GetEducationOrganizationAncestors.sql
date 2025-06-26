-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR REPLACE FUNCTION dms.GetEducationOrganizationAncestors(
    p_educationOrganizationId BIGINT
)
RETURNS TABLE (EducationOrganizationId BIGINT)
AS $$
BEGIN
    RETURN QUERY
    WITH RECURSIVE OrganizationHierarchy AS (
    -- Base case: start with the given organization
    SELECT
        eoh.Id,
        eoh.EducationOrganizationId,
        eoh.ParentId
    FROM
        dms.EducationOrganizationHierarchy eoh
    WHERE
        eoh.EducationOrganizationId = p_educationOrganizationId

    UNION ALL

    -- Recursive case: get all ancestors
    SELECT
        parent.Id,
        parent.EducationOrganizationId,
        parent.ParentId
    FROM
        dms.EducationOrganizationHierarchy parent
    JOIN
        OrganizationHierarchy child ON child.ParentId = parent.Id
)
-- Return all unique ancestor organization IDs including the starting organization
SELECT DISTINCT oh.EducationOrganizationId
FROM OrganizationHierarchy oh
ORDER BY oh.EducationOrganizationId;
END;
$$ LANGUAGE plpgsql;
