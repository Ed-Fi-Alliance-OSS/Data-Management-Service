-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When StudentSchoolAssociation documents are inserted or updated,
-- this trigger synchronizes the StudentSchoolAssociationAuthorization table.
-- Deletes are simply cascaded from the Document table.

-- Helper function to clear StudentSchoolAuthorizationEdOrgIds for a student
CREATE OR REPLACE FUNCTION dms.ClearStudentSchoolAuthorizationEdOrgIds(
    student_id text
)
RETURNS VOID
AS $$
BEGIN
    UPDATE dms.Document d
    SET StudentSchoolAuthorizationEdOrgIds = NULL
    FROM dms.StudentSecurableDocument ssd
    WHERE
        ssd.StudentUniqueId = student_id AND
        d.Id = ssd.StudentSecurableDocumentId AND
        d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;
END;
$$ LANGUAGE plpgsql;

-- Helper function to set StudentSchoolAuthorizationEdOrgIds for a student
CREATE OR REPLACE FUNCTION dms.SetStudentSchoolAuthorizationEdOrgIds(
    student_id text,
    ed_org_ids jsonb
)
RETURNS VOID
AS $$
BEGIN
    UPDATE dms.Document d
    SET StudentSchoolAuthorizationEdOrgIds = ed_org_ids
    FROM dms.StudentSecurableDocument ssd
    WHERE
        ssd.StudentUniqueId = student_id AND
        d.Id = ssd.StudentSecurableDocumentId AND
        d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;
END;
$$ LANGUAGE plpgsql;

-- Function for INSERT operations
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationInsertFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    student_id text;
    existing_contact RECORD;

BEGIN
    -- Extract student unique ID
    student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';

    -- Calculate Ed Org IDs once and store in variable
    SELECT jsonb_agg(EducationOrganizationId)
    FROM dms.GetEducationOrganizationAncestors((NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT)
    INTO ed_org_ids;

    INSERT INTO dms.StudentSchoolAssociationAuthorization (
        StudentUniqueId,
        HierarchySchoolId,
        StudentSchoolAuthorizationEducationOrganizationIds,
        StudentSchoolAssociationId,
        StudentSchoolAssociationPartitionKey
    )
    VALUES (
        student_id,
        (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT,
        ed_org_ids,
        NEW.Id,
        NEW.DocumentPartitionKey
    );

    UPDATE dms.Document
    SET StudentSchoolAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    -- Update all student-securable documents for this student
    PERFORM dms.SetStudentSchoolAuthorizationEdOrgIds(student_id, ed_org_ids);

    -- Insert student contact association records for this student school association
    FOR existing_contact IN
        SELECT ContactUniqueId, StudentContactAssociationId, StudentContactAssociationPartitionKey
        FROM dms.ContactStudentSchoolAuthorization WHERE StudentUniqueId = student_id
    LOOP
        -- Insert into ContactStudentSchoolAuthorization table
        INSERT INTO dms.ContactStudentSchoolAuthorization (
        ContactUniqueId,
        StudentUniqueId,
        ContactStudentSchoolAuthorizationEducationOrganizationIds,
        StudentContactAssociationId,
        StudentContactAssociationPartitionKey,
        StudentSchoolAssociationId, 
        StudentSchoolAssociationPartitionKey
    )
    VALUES (
        existing_contact.ContactUniqueId,
        student_id,
        ed_org_ids,
        existing_contact.StudentContactAssociationId,
        existing_contact.StudentContactAssociationPartitionKey,
        NEW.Id,
        NEW.DocumentPartitionKey
    );

    PERFORM dms.UpdateContactStudentSchoolAuthorizationEdOrgIds(existing_contact.ContactUniqueId);

    END LOOP;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for DELETE operations
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationDeleteFunction()
RETURNS TRIGGER
AS $$
DECLARE
    old_student_id text;

BEGIN
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';

    -- Update all documents for the old student
    PERFORM dms.ClearStudentSchoolAuthorizationEdOrgIds(old_student_id);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for UPDATE operations
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationUpdateFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    new_student_id text;
    old_student_id text;
    new_school_id bigint;
    old_school_id bigint;
BEGIN
    -- Extract values from NEW and OLD records
    new_student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';
    new_school_id := (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT;
    old_school_id := (OLD.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT;

    -- Skip if neither StudentUniqueId nor SchoolId has changed
    IF new_student_id = old_student_id AND new_school_id = old_school_id THEN
        RETURN NULL;
    END IF;

    -- DELETE logic
    PERFORM dms.ClearStudentSchoolAuthorizationEdOrgIds(old_student_id);

    -- INSERT logic
    SELECT jsonb_agg(EducationOrganizationId)
    FROM dms.GetEducationOrganizationAncestors(new_school_id)
    INTO ed_org_ids;

    UPDATE dms.StudentSchoolAssociationAuthorization
    SET
        StudentUniqueId = new_student_id,
        HierarchySchoolId = new_school_id,
        StudentSchoolAuthorizationEducationOrganizationIds = ed_org_ids
    WHERE
        StudentSchoolAssociationId = NEW.Id AND
        StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey;

    UPDATE dms.Document
    SET StudentSchoolAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    -- Update all documents for the new student
    PERFORM dms.SetStudentSchoolAuthorizationEdOrgIds(new_student_id, ed_org_ids);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Drop and recreate triggers
DROP TRIGGER IF EXISTS StudentSchoolAssociationAuthorizationTrigger_Insert ON dms.Document;
CREATE TRIGGER StudentSchoolAssociationAuthorizationTrigger_Insert
AFTER INSERT ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationInsertFunction();

DROP TRIGGER IF EXISTS StudentSchoolAssociationAuthorizationTrigger_Update ON dms.Document;
CREATE TRIGGER StudentSchoolAssociationAuthorizationTrigger_Update
AFTER UPDATE OF EdfiDoc ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationUpdateFunction();

DROP TRIGGER IF EXISTS StudentSchoolAssociationAuthorizationTrigger_Delete ON dms.Document;
CREATE TRIGGER StudentSchoolAssociationAuthorizationTrigger_Delete
AFTER DELETE ON dms.Document
    FOR EACH ROW
    WHEN (OLD.ProjectName = 'Ed-Fi' AND OLD.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationDeleteFunction();
