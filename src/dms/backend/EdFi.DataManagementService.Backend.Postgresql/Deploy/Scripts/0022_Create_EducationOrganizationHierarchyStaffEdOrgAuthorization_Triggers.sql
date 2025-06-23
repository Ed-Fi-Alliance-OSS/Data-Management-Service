-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When EducationOrganizationHierarchy rows are inserted, updated, or deleted,
-- this trigger re-evaluates the StaffEducationOrganizationAuthorizationEdOrgIds array
-- for affected rows.
CREATE OR REPLACE FUNCTION dms.EducationOrganizationHierarchyStaffEdOrgAuthorizationTriggerFunction()
RETURNS TRIGGER
AS $$
DECLARE
    affected_ed_org_id BIGINT;
    affected_record RECORD;
BEGIN
    -- Determine which education organization ID was affected
    IF (TG_OP = 'DELETE') THEN
        affected_ed_org_id := OLD.EducationOrganizationId;
    ELSE
        affected_ed_org_id := NEW.EducationOrganizationId;
    END IF;

    -- Find all StaffEducationOrganizationAuthorization records that have the affected education organization ID
    -- either directly as HierarchyEdOrgId or as part of StaffEducationOrganizationAuthorizationEdOrgIds
    FOR affected_record IN
        SELECT
            ssoa.StaffEducationOrganizationId,
            ssoa.StaffEducationOrganizationPartitionKey,
            ssoa.StaffUniqueId,
            ssoa.HierarchyEdOrgId
        FROM
            dms.StaffEducationOrganizationAuthorization ssoa
        WHERE
            ssoa.HierarchyEdOrgId = affected_ed_org_id
            OR
            jsonb_path_exists(ssoa.StaffEducationOrganizationAuthorizationEdOrgIds, '$[*] ? (@ == $id)',
                             jsonb_build_object('id', affected_ed_org_id))
    LOOP
        -- Update each affected record with the new hierarchy information
        UPDATE dms.StaffEducationOrganizationAuthorization
        SET StaffEducationOrganizationAuthorizationEdOrgIds = (
            SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
            FROM dms.GetEducationOrganizationAncestors(affected_record.HierarchyEdOrgId)
        )
        WHERE
            StaffEducationOrganizationId = affected_record.StaffEducationOrganizationId
            AND StaffEducationOrganizationPartitionKey = affected_record.StaffEducationOrganizationPartitionKey;
    END LOOP;

    -- Also update records that might be affected by changes in ancestor relationships
    -- Find all StaffEducationOrganizationAuthorization records where the education organization might be in the affected hierarchy
    WITH RECURSIVE EdOrgsInChangedHierarchy AS (
        -- Base case: start with the affected education organization
        SELECT
            Id,
            EducationOrganizationId,
            ParentId
        FROM
            dms.EducationOrganizationHierarchy
        WHERE
            EducationOrganizationId = affected_ed_org_id

        UNION ALL

        -- Find all descendants of the affected education organization
        SELECT
            child.Id,
            child.EducationOrganizationId,
            child.ParentId
        FROM
            dms.EducationOrganizationHierarchy child
        JOIN
            EdOrgsInChangedHierarchy parent ON child.ParentId = parent.Id
    )
    UPDATE dms.StaffEducationOrganizationAuthorization ssoa
    SET StaffEducationOrganizationAuthorizationEdOrgIds = (
        SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
        FROM dms.GetEducationOrganizationAncestors(ssoa.HierarchyEdOrgId)
    )
    FROM EdOrgsInChangedHierarchy edorg
    WHERE ssoa.HierarchyEdOrgId = edorg.EducationOrganizationId;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS EducationOrganizationHierarchy_StaffEdOrgAuthorizationTrigger ON dms.EducationOrganizationHierarchy;

CREATE TRIGGER EducationOrganizationHierarchy_StaffEdOrgAuthorizationTrigger
AFTER UPDATE OR DELETE ON dms.EducationOrganizationHierarchy
    FOR EACH ROW
    EXECUTE PROCEDURE dms.EducationOrganizationHierarchyStaffEdOrgAuthorizationTriggerFunction();
