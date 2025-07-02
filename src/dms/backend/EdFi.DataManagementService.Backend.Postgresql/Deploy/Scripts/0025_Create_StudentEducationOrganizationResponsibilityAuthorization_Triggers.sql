-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When StudentEducationOrganizationResponsibilityAssociation documents are inserted or updated,
-- this trigger synchronizes the StudentEducationOrganizationResponsibilityAuthorization table.
-- Deletes are simply cascaded from the Document table.

-- Sets the EducationOrganizationIds to all the student-securable documents
-- of a given student (existing function, no need to recreate)

-- New function for StudentEducationOrganizationResponsibility authorization
CREATE OR REPLACE FUNCTION dms.GetStudentEdOrgResponsibilityIds(
    student_id text
)
RETURNS jsonb AS $$
BEGIN
  RETURN (
    SELECT jsonb_agg(DISTINCT edOrgIds)
        FROM (
            SELECT jsonb_array_elements(seora.StudentEdOrgResponsibilityAuthorizationEdOrgIds) AS edOrgIds
            FROM dms.StudentEducationOrganizationResponsibilityAuthorization seora
            WHERE seora.StudentUniqueId = student_id
        )
  );
END;
$$ LANGUAGE plpgsql;

-- Function to set EdOrg IDs to student securable documents for responsibility authorization
CREATE OR REPLACE FUNCTION dms.SetStudentEdOrgResponsibilityIdsToStudentSecurables(
    ed_org_ids jsonb,
    student_id text
)
RETURNS VOID
AS $$
BEGIN
    UPDATE dms.Document d
    SET StudentEdOrgResponsibilityAuthorizationIds = ed_org_ids
    FROM dms.StudentSecurableDocument ssd
    WHERE
        ssd.StudentUniqueId = student_id AND
        d.Id = ssd.StudentSecurableDocumentId AND
        d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;
END;
$$ LANGUAGE plpgsql;

-- Function for INSERT operations
CREATE OR REPLACE FUNCTION dms.StudentEducationOrganizationResponsibilityAuthorizationInsertFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ancestor_ed_org_ids jsonb;
    ed_org_ids jsonb;
    student_id text;
    education_organization_id bigint;
BEGIN
    student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    education_organization_id := NEW.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId';

    -- Calculate Ed Org IDs once and store in variable
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors(education_organization_id)
    INTO ancestor_ed_org_ids;

    INSERT INTO dms.StudentEducationOrganizationResponsibilityAuthorization (
        StudentUniqueId,
        HierarchyEdOrgId,
        StudentEdOrgResponsibilityAuthorizationEdOrgIds,
        StudentEdOrgResponsibilityAssociationId,
        StudentEdOrgResponsibilityAssociationPartitionKey
    )
    VALUES (
        student_id,
        education_organization_id,
        ancestor_ed_org_ids,
        NEW.Id,
        NEW.DocumentPartitionKey
    );

    -- Update all student-securable documents for this student
    ed_org_ids := dms.GetStudentEdOrgResponsibilityIds(student_id);
    PERFORM dms.SetStudentEdOrgResponsibilityIdsToStudentSecurables(ed_org_ids, student_id);

    -- Manually update the newly inserted StudentEducationOrganizationResponsibilityAssociation because it's not a
    -- student-securable at this point
    UPDATE dms.Document
    SET StudentEdOrgResponsibilityAuthorizationIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for DELETE operations
CREATE OR REPLACE FUNCTION dms.StudentEducationOrganizationResponsibilityAuthorizationDeleteFunction()
RETURNS TRIGGER
AS $$
DECLARE
    old_student_id text;
BEGIN
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';

    -- Update all documents for the old student
    PERFORM dms.SetStudentEdOrgResponsibilityIdsToStudentSecurables(dms.GetStudentEdOrgResponsibilityIds(old_student_id), old_student_id);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for UPDATE operations
CREATE OR REPLACE FUNCTION dms.StudentEducationOrganizationResponsibilityAuthorizationUpdateFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ancestor_ed_org_ids jsonb;
    new_student_id text;
    old_student_id text;
    new_education_organization_id bigint;
    old_education_organization_id bigint;
BEGIN
    new_student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';
    new_education_organization_id := (NEW.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT;
    old_education_organization_id := (OLD.EdfiDoc->'educationOrganizationReference'->>'educationOrganizationId')::BIGINT;

    -- Exit early if nothing has changed
    IF new_student_id = old_student_id AND new_education_organization_id = old_education_organization_id THEN
        RETURN NULL;
    END IF;

    -- Remove existing securables for old student
    PERFORM dms.SetStudentEdOrgResponsibilityIdsToStudentSecurables(NULL, old_student_id);

    -- Get new ancestor education organization IDs for the education organization
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors(new_education_organization_id)
    INTO ancestor_ed_org_ids;

    UPDATE dms.StudentEducationOrganizationResponsibilityAuthorization
    SET
        StudentUniqueId = new_student_id,
        HierarchyEdOrgId = new_education_organization_id,
        StudentEdOrgResponsibilityAuthorizationEdOrgIds = ancestor_ed_org_ids
    WHERE
        StudentEdOrgResponsibilityAssociationId = NEW.Id AND
        StudentEdOrgResponsibilityAssociationPartitionKey = NEW.DocumentPartitionKey;

    -- Update all documents for the new student
    PERFORM dms.SetStudentEdOrgResponsibilityIdsToStudentSecurables(dms.GetStudentEdOrgResponsibilityIds(new_student_id), new_student_id);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Drop and recreate triggers
DROP TRIGGER IF EXISTS StudentEdOrgResponsibilityAssociation_Trigger_Insert ON dms.Document;
CREATE TRIGGER StudentEdOrgResponsibilityAssociation_Trigger_Insert
AFTER INSERT ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentEducationOrganizationResponsibilityAssociation')
    EXECUTE PROCEDURE dms.StudentEducationOrganizationResponsibilityAuthorizationInsertFunction();

DROP TRIGGER IF EXISTS StudentEdOrgResponsibilityAssociation_Trigger_Update ON dms.Document;
CREATE TRIGGER StudentEdOrgResponsibilityAssociation_Trigger_Update
AFTER UPDATE OF EdfiDoc ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentEducationOrganizationResponsibilityAssociation')
    EXECUTE PROCEDURE dms.StudentEducationOrganizationResponsibilityAuthorizationUpdateFunction();

DROP TRIGGER IF EXISTS StudentEdOrgResponsibilityAssociation_Trigger_Delete ON dms.Document;
CREATE TRIGGER StudentEdOrgResponsibilityAssociation_Trigger_Delete
AFTER DELETE ON dms.Document
    FOR EACH ROW
    WHEN (OLD.ProjectName = 'Ed-Fi' AND OLD.ResourceName = 'StudentEducationOrganizationResponsibilityAssociation')
    EXECUTE PROCEDURE dms.StudentEducationOrganizationResponsibilityAuthorizationDeleteFunction();
