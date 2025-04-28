-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When StudentContactAssociation documents are inserted or updated,
-- this trigger synchronizes the ContactStudentSchoolAuthorization table.
-- Deletes are simply cascaded from the Document table.

-- Function for INSERT operations
CREATE OR REPLACE FUNCTION dms.ContactStudentSchoolAuthorizationInsertFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    student_id text;
    contact_id text;
    student_school_asso_id BIGINT;
    student_school_asso_key BIGINT;
    hierarchy_school_id BIGINT;
BEGIN
    -- Extract student unique ID
    student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';

    -- Extract contact unique ID
    contact_id := NEW.EdfiDoc->'contactReference'->>'contactUniqueId';

    -- Extract student school association document details
    SELECT jsonb_agg(StudentSchoolAuthorizationEducationOrganizationIds), HierarchySchoolId, StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey
    INTO ed_org_ids, hierarchy_school_id, student_school_asso_id, student_school_asso_key
    FROM dms.StudentSchoolAssociationAuthorization 
    WHERE StudentUniqueId = student_id


    INSERT INTO dms.ContactStudentSchoolAuthorization (
        ContactUniqueId,
        StudentUniqueId,
        HierarchySchoolId, 
        ContactStudentSchoolAuthorizationEducationOrganizationIds,
        StudentContactAssociationId,
        StudentContactAssociationPartitionKey,
        StudentSchoolAssociationId, 
        StudentSchoolAssociationPartitionKey
    )
    VALUES (
        contact_id,
        student_id,
        hierarchy_school_id,
        ed_org_ids,
        NEW.Id,
        NEW.DocumentPartitionKey,
        student_school_asso_id,
        hierarchy_school_id
    );

    UPDATE dms.Document
    SET ContactStudentSchoolAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for UPDATE operations
CREATE OR REPLACE FUNCTION dms.ContactStudentSchoolAuthorizationUpdateFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    new_student_id text;
    old_student_id text;
    new_contact_id bigint;
    old_contact_id bigint;
    student_school_asso_id BIGINT;
    student_school_asso_key BIGINT;
    hierarchy_school_id BIGINT;
BEGIN
    -- Extract values from NEW and OLD records
    new_student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';
    new_contact_id := (NEW.EdfiDoc->'contactReference'->>'contactUniqueId')::BIGINT;
    old_contact_id := (OLD.EdfiDoc->'contactReference'->>'contactUniqueId')::BIGINT;

    -- Skip if neither StudentUniqueId nor ContactUniqueId has changed
    IF new_student_id = old_student_id AND new_contact_id = old_contact_id THEN
        RETURN NULL;
    END IF;

    -- Extract student school association document details
    SELECT jsonb_agg(StudentSchoolAuthorizationEducationOrganizationIds), HierarchySchoolId, StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey
    INTO ed_org_ids, hierarchy_school_id, student_school_asso_id, student_school_asso_key
    FROM dms.StudentSchoolAssociationAuthorization 
    WHERE StudentUniqueId = student_id

    UPDATE dms.ContactStudentSchoolAuthorization
    SET
        ContactUniqueId = new_contact_id,
        StudentUniqueId = new_student_id,
        HierarchySchoolId = hierarchy_school_id,
        ContactStudentSchoolAuthorizationEducationOrganizationIds = ed_org_ids,
        StudentSchoolAssociationId = student_school_asso_id,
        StudentSchoolAssociationPartitionKey = student_school_asso_key
    WHERE
        StudentContactAssociationId = NEW.Id AND
        StudentContactAssociationPartitionKey = NEW.DocumentPartitionKey;

    UPDATE dms.Document
    SET ContactStudentSchoolAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Drop and recreate triggers
DROP TRIGGER IF EXISTS ContactStudentSchoolAuthorizationTrigger_Insert ON dms.Document;
CREATE TRIGGER ContactStudentSchoolAuthorizationTrigger_Insert
AFTER INSERT ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentContactAssociation')
    EXECUTE PROCEDURE dms.ContactStudentSchoolAuthorizationInsertFunction();

DROP TRIGGER IF EXISTS ContactStudentSchoolAuthorizationTrigger_Update ON dms.Document;
CREATE TRIGGER ContactStudentSchoolAuthorizationTrigger_Update
AFTER UPDATE OF EdfiDoc ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentContactAssociation')
    EXECUTE PROCEDURE dms.ContactStudentSchoolAuthorizationUpdateFunction();
