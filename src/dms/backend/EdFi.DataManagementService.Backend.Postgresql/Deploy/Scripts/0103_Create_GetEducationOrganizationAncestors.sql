-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR REPLACE FUNCTION dms.GetEducationOrganizationAncestors(
    p_documentUuid UUID,
    p_documentPartitionKey SMALLINT
)
RETURNS TABLE (EducationOrganizationId BIGINT)
AS $$
BEGIN
    RETURN QUERY
    WITH RECURSIVE ParentHierarchy(Id, EducationOrganizationId, ParentId) AS (
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        WHERE h.EducationOrganizationId IN (
            SELECT jsonb_array_elements(d.securityelements->'EducationOrganization')::text::BIGINT
            FROM dms.document d
            WHERE d.DocumentUuid = p_documentUuid AND d.DocumentPartitionKey = p_documentPartitionKey
        )
        UNION ALL
        SELECT parent.Id, parent.EducationOrganizationId, parent.ParentId
        FROM dms.EducationOrganizationHierarchy parent
        JOIN ParentHierarchy child ON parent.Id = child.ParentId
    )
    SELECT ph.EducationOrganizationId
    FROM ParentHierarchy ph
    ORDER BY ph.EducationOrganizationId;
END;
$$ LANGUAGE plpgsql;
