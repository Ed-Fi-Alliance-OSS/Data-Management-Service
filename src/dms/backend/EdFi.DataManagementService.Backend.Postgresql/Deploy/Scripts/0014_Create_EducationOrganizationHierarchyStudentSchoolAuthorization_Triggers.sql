-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When EducationOrganizationHierarchy rows are inserted, updated, or deleted,
-- this trigger re-evaluates the StudentSchoolAuthorizationEducationOrganizationIds array
-- and StudentEdOrgResponsibilityAuthorizationEdOrgIds array for affected rows.
CREATE OR REPLACE FUNCTION dms.EducationOrganizationHierarchyStudentSchoolAuthorizationTriggerFunction()
RETURNS TRIGGER
AS $$
DECLARE
    affected_school_id BIGINT;
    affected_ssa RECORD;
    affected_seora RECORD;
BEGIN
    -- Determine which education organization ID was affected
    IF (TG_OP = 'DELETE') THEN
        affected_school_id := OLD.EducationOrganizationId;
    ELSE
        affected_school_id := NEW.EducationOrganizationId;
    END IF;

    -- Find all StudentSchoolAssociationAuthorization records that have the affected school ID
    -- either directly as HierarchySchoolId or as part of StudentSchoolAuthorizationEducationOrganizationIds
    FOR affected_ssa IN
        SELECT
            ssa.StudentSchoolAssociationId,
            ssa.StudentSchoolAssociationPartitionKey,
            ssa.StudentUniqueId,
            ssa.HierarchySchoolId
        FROM
            dms.StudentSchoolAssociationAuthorization ssa
        WHERE
            ssa.HierarchySchoolId = affected_school_id
            OR
            jsonb_path_exists(ssa.StudentSchoolAuthorizationEducationOrganizationIds, '$[*] ? (@ == $id)',
                             jsonb_build_object('id', affected_school_id))
    LOOP
        -- Update each affected record with the new hierarchy information
        UPDATE dms.StudentSchoolAssociationAuthorization
        SET StudentSchoolAuthorizationEducationOrganizationIds = (
            SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
            FROM dms.GetEducationOrganizationAncestors(affected_ssa.HierarchySchoolId)
        )
        WHERE
            StudentSchoolAssociationId = affected_ssa.StudentSchoolAssociationId
            AND StudentSchoolAssociationPartitionKey = affected_ssa.StudentSchoolAssociationPartitionKey;
    END LOOP;

    -- Find all StudentEducationOrganizationResponsibilityAuthorization records that have the affected education organization ID
    -- either directly as HierarchyEdOrgId or as part of StudentEdOrgResponsibilityAuthorizationEdOrgIds
    FOR affected_seora IN
        SELECT
            seora.StudentEdOrgResponsibilityAssociationId,
            seora.StudentEdOrgResponsibilityAssociationPartitionKey,
            seora.StudentUniqueId,
            seora.HierarchyEdOrgId
        FROM
            dms.StudentEducationOrganizationResponsibilityAuthorization seora
        WHERE
            seora.HierarchyEdOrgId = affected_school_id
            OR
            jsonb_path_exists(seora.StudentEdOrgResponsibilityAuthorizationEdOrgIds, '$[*] ? (@ == $id)',
                             jsonb_build_object('id', affected_school_id))
    LOOP
        -- Update each affected record with the new hierarchy information
        UPDATE dms.StudentEducationOrganizationResponsibilityAuthorization
        SET StudentEdOrgResponsibilityAuthorizationEdOrgIds = (
            SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
            FROM dms.GetEducationOrganizationAncestors(affected_seora.HierarchyEdOrgId)
        )
        WHERE
            StudentEdOrgResponsibilityAssociationId = affected_seora.StudentEdOrgResponsibilityAssociationId
            AND StudentEdOrgResponsibilityAssociationPartitionKey = affected_seora.StudentEdOrgResponsibilityAssociationPartitionKey;
    END LOOP;

    -- Also update records that might be affected by changes in ancestor relationships
    -- Find all StudentSchoolAssociationAuthorization records where the school might be in the affected hierarchy
    WITH RECURSIVE SchoolsInChangedHierarchy AS (
        -- Base case: start with the affected school
        SELECT
            Id,
            EducationOrganizationId,
            ParentId
        FROM
            dms.EducationOrganizationHierarchy
        WHERE
            EducationOrganizationId = affected_school_id

        UNION ALL

        -- Find all descendants of the affected school
        SELECT
            child.Id,
            child.EducationOrganizationId,
            child.ParentId
        FROM
            dms.EducationOrganizationHierarchy child
        JOIN
            SchoolsInChangedHierarchy parent ON child.ParentId = parent.Id
    )
    UPDATE dms.StudentSchoolAssociationAuthorization ssa
    SET StudentSchoolAuthorizationEducationOrganizationIds = (
        SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
        FROM dms.GetEducationOrganizationAncestors(ssa.HierarchySchoolId)
    )
    FROM SchoolsInChangedHierarchy sch
    WHERE ssa.HierarchySchoolId = sch.EducationOrganizationId;

    -- Also update StudentEducationOrganizationResponsibilityAuthorization records for schools in the changed hierarchy
    WITH RECURSIVE SchoolsInChangedHierarchy AS (
        -- Base case: start with the affected school
        SELECT
            Id,
            EducationOrganizationId,
            ParentId
        FROM
            dms.EducationOrganizationHierarchy
        WHERE
            EducationOrganizationId = affected_school_id

        UNION ALL

        -- Find all descendants of the affected school
        SELECT
            child.Id,
            child.EducationOrganizationId,
            child.ParentId
        FROM
            dms.EducationOrganizationHierarchy child
        JOIN
            SchoolsInChangedHierarchy parent ON child.ParentId = parent.Id
    )
    UPDATE dms.StudentEducationOrganizationResponsibilityAuthorization seora
    SET StudentEdOrgResponsibilityAuthorizationEdOrgIds = (
        SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
        FROM dms.GetEducationOrganizationAncestors(seora.HierarchyEdOrgId)
    )
    FROM SchoolsInChangedHierarchy sch
    WHERE seora.HierarchyEdOrgId = sch.EducationOrganizationId;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS EducationOrganizationHierarchy_StudentSchoolAuthorizationTrigger ON dms.EducationOrganizationHierarchy;

CREATE TRIGGER EducationOrganizationHierarchy_StudentSchoolAuthorizationTrigger
AFTER UPDATE OR DELETE ON dms.EducationOrganizationHierarchy
    FOR EACH ROW
    EXECUTE PROCEDURE dms.EducationOrganizationHierarchyStudentSchoolAuthorizationTriggerFunction();
