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
    unified_ed_org_ids jsonb := '[]';
    student_id text;
    contact_id text;
    student_school_asso RECORD;
BEGIN
    -- Extract student unique ID
    student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';

    -- Extract contact unique ID
    contact_id := NEW.EdfiDoc->'contactReference'->>'contactUniqueId';

    -- Extract student school association document details
    FOR student_school_asso IN
        SELECT jsonb_agg(DISTINCT value) AS aggregated_ed_org_ids,
            StudentSchoolAssociationId, 
            StudentSchoolAssociationPartitionKey
        FROM (
            SELECT DISTINCT jsonb_array_elements(StudentSchoolAuthorizationEducationOrganizationIds) AS value,
            StudentSchoolAssociationId,
            StudentSchoolAssociationPartitionKey
            FROM dms.StudentSchoolAssociationAuthorization 
            WHERE StudentUniqueId = student_id
        ) subquery
        GROUP BY StudentSchoolAssociationId, StudentSchoolAssociationPartitionKey

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
        contact_id,
        student_id,
        student_school_asso.aggregated_ed_org_ids,
        NEW.Id,
        NEW.DocumentPartitionKey,
        student_school_asso.StudentSchoolAssociationId,
        student_school_asso.StudentSchoolAssociationPartitionKey
    );
    END LOOP;

    PERFORM dms.UpdateContactStudentSchoolAuthorizationEdOrgIds(contact_id);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for DELETE operations
CREATE OR REPLACE FUNCTION dms.ContactStudentSchoolAuthorizationDeleteFunction()
RETURNS TRIGGER
AS $$
DECLARE
    old_contact_id text;
BEGIN
    old_contact_id := OLD.EdfiDoc->'contactReference'->>'contactUniqueId';

    -- Update edorg id list for the contact securable documents
    PERFORM dms.UpdateContactStudentSchoolAuthorizationEdOrgIds(old_contact_id);

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

DROP TRIGGER IF EXISTS ContactStudentSchoolAuthorizationTrigger_Delete ON dms.Document;
CREATE TRIGGER ContactStudentSchoolAuthorizationTrigger_Delete
AFTER DELETE ON dms.Document
    FOR EACH ROW
    WHEN (OLD.ProjectName = 'Ed-Fi' AND OLD.ResourceName = 'StudentContactAssociation')
    EXECUTE PROCEDURE dms.ContactStudentSchoolAuthorizationDeleteFunction();
