-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.


-- Helper function to set StaffEducationOrganizationAuthorizationEdOrgIds for a staff member
CREATE OR REPLACE FUNCTION dms.SetStaffEducationOrganizationAuthorizationEdOrgIds(
    staff_id text,
    ed_org_ids jsonb
)
RETURNS VOID
AS $$
BEGIN
    UPDATE dms.Document d
    SET StaffEducationOrganizationAuthorizationEdOrgIds = ed_org_ids
    FROM dms.StaffSecurableDocument ssd
    WHERE
        ssd.StaffUniqueId = staff_id AND
        d.Id = ssd.StaffSecurableDocumentId AND
        d.DocumentPartitionKey = ssd.StaffSecurableDocumentPartitionKey;
END;
$$ LANGUAGE plpgsql;

-- Helper function to remove specific educationOrganizationId(s) from StaffEducationOrganizationAuthorizationEdOrgIds
CREATE OR REPLACE FUNCTION dms.RemoveStaffEducationOrganizationAuthorizationEdOrgIds(
    staff_id text,
    ed_org_id_to_remove jsonb
)
RETURNS VOID
AS $$
BEGIN
    UPDATE dms.Document d
    SET StaffEducationOrganizationAuthorizationEdOrgIds = (
        SELECT jsonb_agg(value)
        FROM jsonb_array_elements(d.StaffEducationOrganizationAuthorizationEdOrgIds) AS value
        WHERE value::text NOT IN (
            SELECT jsonb_array_elements_text(ed_org_id_to_remove)
        )
    )
    FROM dms.StaffEducationOrganizationAuthorization ssoa
    WHERE
        ssoa.StaffUniqueId = staff_id AND
        d.Id = ssoa.StaffEducationOrganizationId AND
        d.DocumentPartitionKey = ssoa.StaffEducationOrganizationPartitionKey;
END;
$$ LANGUAGE plpgsql;

-- Function for INSERT operations
CREATE OR REPLACE FUNCTION dms.StaffEducationOrganizationAuthorizationInsertFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    staff_id text;
BEGIN
    -- Extract staff unique ID
    staff_id := NEW.EdfiDoc->'staffReference'->>'staffUniqueId';

    -- Calculate Ed Org IDs once and store in variable
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors((NEW.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT)
    INTO ed_org_ids;

    INSERT INTO dms.StaffEducationOrganizationAuthorization (
        StaffUniqueId,
        HierarchyEdOrgId,
        StaffEducationOrganizationAuthorizationEdOrgIds,
        StaffEducationOrganizationId,
        StaffEducationOrganizationPartitionKey
    )
    VALUES (
        staff_id,
        (NEW.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT,
        ed_org_ids,
        NEW.Id,
        NEW.DocumentPartitionKey
    );

    UPDATE dms.Document
    SET StaffEducationOrganizationAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    -- Update all staff-securable documents for this staff member
    PERFORM dms.SetStaffEducationOrganizationAuthorizationEdOrgIds(staff_id, ed_org_ids);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for DELETE operations
CREATE OR REPLACE FUNCTION dms.StaffEducationOrganizationAuthorizationDeleteFunction()
RETURNS TRIGGER
AS $$
DECLARE
    old_staff_id text;
    old_ed_org_id bigint;
BEGIN
    old_staff_id := OLD.EdfiDoc->'staffReference'->>'staffUniqueId';
    old_ed_org_id := (OLD.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT;

    -- Remove the specific educationOrganizationId from the array
    PERFORM dms.RemoveStaffEducationOrganizationAuthorizationEdOrgIds(old_staff_id, jsonb_build_array(old_ed_org_id));

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for UPDATE operations
CREATE OR REPLACE FUNCTION dms.StaffEducationOrganizationAuthorizationUpdateFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    new_staff_id text;
    old_staff_id text;
    new_ed_org_id bigint;
    old_ed_org_id bigint;
BEGIN
    -- Extract values from NEW and OLD records
    new_staff_id := NEW.EdfiDoc->'staffReference'->>'staffUniqueId';
    old_staff_id := OLD.EdfiDoc->'staffReference'->>'staffUniqueId';
    new_ed_org_id := (NEW.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT;
    old_ed_org_id := (OLD.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT;

    -- Skip if neither StaffUniqueId nor EducationOrganizationId has changed
    IF new_staff_id = old_staff_id AND new_ed_org_id = old_ed_org_id THEN
        RETURN NULL;
    END IF;

    -- DELETE logic
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors(old_ed_org_id)
    INTO ed_org_ids;

    PERFORM dms.RemoveStaffEducationOrganizationAuthorizationEdOrgIds(old_staff_id, ed_org_ids);

    -- INSERT logic
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors(new_ed_org_id)
    INTO ed_org_ids;

    UPDATE dms.StaffEducationOrganizationAuthorization
    SET StaffUniqueId = new_staff_id,
        HierarchyEdOrgId = new_ed_org_id,
        StaffEducationOrganizationAuthorizationEdOrgIds = ed_org_ids
    WHERE
        StaffEducationOrganizationId = new.Id AND
        StaffEducationOrganizationPartitionKey = new.DocumentPartitionKey;

    UPDATE dms.Document
    SET StaffEducationOrganizationAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    PERFORM dms.SetStaffEducationOrganizationAuthorizationEdOrgIds(new_staff_id, ed_org_ids);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Drop and recreate triggers
DROP TRIGGER IF EXISTS StaffEducationOrganizationAuthorizationTrigger_Insert ON dms.Document;
CREATE TRIGGER StaffEducationOrganizationAuthorizationTrigger_Insert
AFTER INSERT ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND (NEW.ResourceName = 'StaffEducationOrganizationEmploymentAssociation' OR NEW.ResourceName = 'StaffEducationOrganizationAssignmentAssociation'))
    EXECUTE PROCEDURE dms.StaffEducationOrganizationAuthorizationInsertFunction();

DROP TRIGGER IF EXISTS StaffEducationOrganizationAuthorizationTrigger_Update ON dms.Document;
CREATE TRIGGER StaffEducationOrganizationAuthorizationTrigger_Update
AFTER UPDATE OF EdfiDoc ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND (NEW.ResourceName = 'StaffEducationOrganizationEmploymentAssociation' OR NEW.ResourceName = 'StaffEducationOrganizationAssignmentAssociation'))
    EXECUTE PROCEDURE dms.StaffEducationOrganizationAuthorizationUpdateFunction();

DROP TRIGGER IF EXISTS StaffEducationOrganizationAuthorizationTrigger_Delete ON dms.Document;
CREATE TRIGGER StaffEducationOrganizationAuthorizationTrigger_Delete
AFTER DELETE ON dms.Document
    FOR EACH ROW
    WHEN (OLD.ProjectName = 'Ed-Fi' AND (OLD.ResourceName = 'StaffEducationOrganizationEmploymentAssociation' OR OLD.ResourceName = 'StaffEducationOrganizationAssignmentAssociation'))
    EXECUTE PROCEDURE dms.StaffEducationOrganizationAuthorizationDeleteFunction();

