-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When StudentSchoolAssociation documents are inserted or updated,
-- this trigger synchronizes the StudentSchoolAssociationAuthorization table.
-- Deletes are simply cascaded from the Document table.

-- Sets the EducationOrganizationIds to all the student-securable documents
-- of a given student
CREATE OR REPLACE FUNCTION dms.SetEdOrgIdsToStudentSecurables(
    ed_org_ids jsonb,
    student_id text
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

-- Returns the combined EducationOrganizationIds of all the StudentSchoolAssociationAuthorizations
-- of a given student
CREATE OR REPLACE FUNCTION dms.GetStudentEdOrgIds(
    student_id text
)
RETURNS jsonb AS $$
BEGIN
  RETURN (
    SELECT jsonb_agg(DISTINCT edOrgIds)
        FROM (
            SELECT jsonb_array_elements(ssa.StudentSchoolAuthorizationEducationOrganizationIds) AS edOrgIds
            FROM dms.StudentSchoolAssociationAuthorization ssa
            WHERE ssa.StudentUniqueId = student_id
        )
  );
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION dms.InsertContactStudentSchoolAuthorization(
    student_id text,
    ed_org_ids jsonb,
    ssa_id bigint,
    ssa_partition_key smallint,
    contact_id text DEFAULT NULL
)
RETURNS VOID
AS $$
DECLARE
    student_contact RECORD;
BEGIN

    FOR student_contact IN
        SELECT ContactUniqueId, StudentContactAssociationDocumentId, StudentContactAssociationDocumentPartitionKey
        FROM dms.StudentContactRelation WHERE StudentUniqueId = student_id
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
            COALESCE(contact_id, student_contact.ContactUniqueId),
            student_id,
            ed_org_ids,
            student_contact.StudentContactAssociationDocumentId,
            student_contact.StudentContactAssociationDocumentPartitionKey,
            ssa_id,
            ssa_partition_key
        );

        PERFORM dms.SetEdOrgIdsToContactSecurables(dms.GetContactEdOrgIds(COALESCE(contact_id, student_contact.ContactUniqueId)), COALESCE(contact_id, student_contact.ContactUniqueId));
    END LOOP;
END;
$$ LANGUAGE plpgsql;

-- Function for INSERT operations
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationInsertFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ancestor_ed_org_ids jsonb;
    ed_org_ids jsonb;
    student_id text;
    school_id bigint;
BEGIN
    student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    school_id := NEW.EdfiDoc->'schoolReference'->>'schoolId';

    -- Calculate Ed Org IDs once and store in variable
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors(school_id)
    INTO ancestor_ed_org_ids;

    INSERT INTO dms.StudentSchoolAssociationAuthorization (
        StudentUniqueId,
        HierarchySchoolId,
        StudentSchoolAuthorizationEducationOrganizationIds,
        StudentSchoolAssociationId,
        StudentSchoolAssociationPartitionKey
    )
    VALUES (
        student_id,
        school_id,
        ancestor_ed_org_ids,
        NEW.Id,
        NEW.DocumentPartitionKey
    );

    -- Update all student-securable documents for this student
    ed_org_ids := dms.GetStudentEdOrgIds(student_id);
    PERFORM dms.SetEdOrgIdsToStudentSecurables(ed_org_ids, student_id);

    -- Manually update the newly inserted StudentSchoolAssociation because it's not a
    -- student-securable at this point
    UPDATE dms.Document
    SET StudentSchoolAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

    -- Insert student contact association records for this student school association
    PERFORM dms.InsertContactStudentSchoolAuthorization(student_id, ancestor_ed_org_ids, NEW.Id, NEW.DocumentPartitionKey);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for DELETE operations
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationDeleteFunction()
RETURNS TRIGGER
AS $$
DECLARE
    old_student_id text;
    contact_id text;
BEGIN
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';

    -- Update all documents for the old student
    PERFORM dms.SetEdOrgIdsToStudentSecurables(dms.GetStudentEdOrgIds(old_student_id), old_student_id);

    -- Remove access to the related Contacts
    UPDATE dms.ContactStudentSchoolAuthorization
    SET
        ContactStudentSchoolAuthorizationEducationOrganizationIds = '[]'
    WHERE
        StudentUniqueId = old_student_id AND
        StudentSchoolAssociationId IS NULL AND
        StudentSchoolAssociationPartitionKey IS NULL;

    FOR contact_id IN
        SELECT DISTINCT ContactUniqueId
        FROM dms.ContactStudentSchoolAuthorization
        WHERE
            StudentUniqueId = old_student_id AND
            StudentSchoolAssociationId IS NULL AND
            StudentSchoolAssociationPartitionKey IS NULL
    LOOP

        PERFORM dms.SetEdOrgIdsToContactSecurables(dms.GetContactEdOrgIds(contact_id), contact_id);
    END LOOP;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Function for UPDATE operations
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationUpdateFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ancestor_ed_org_ids jsonb;
    new_student_id text;
    old_student_id text;
    new_school_id bigint;
    old_school_id bigint;
    contact_id text;
BEGIN
    new_student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';
    new_school_id := (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT;
    old_school_id := (OLD.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT;

    -- Exit early if nothing has changed
    IF new_student_id = old_student_id AND new_school_id = old_school_id THEN
        RETURN NULL;
    END IF;

    -- Remove existing securables for old student
    PERFORM dms.SetEdOrgIdsToStudentSecurables(NULL, old_student_id);

    -- Get new ancestor education organization IDs for the school
    SELECT jsonb_agg(to_jsonb(EducationOrganizationId::text))
    FROM dms.GetEducationOrganizationAncestors(new_school_id)
    INTO ancestor_ed_org_ids;

    UPDATE dms.StudentSchoolAssociationAuthorization
    SET
        StudentUniqueId = new_student_id,
        HierarchySchoolId = new_school_id,
        StudentSchoolAuthorizationEducationOrganizationIds = ancestor_ed_org_ids
    WHERE
        StudentSchoolAssociationId = NEW.Id AND
        StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey;

    -- Update all documents for the new student
    PERFORM dms.SetEdOrgIdsToStudentSecurables(dms.GetStudentEdOrgIds(new_student_id), new_student_id);

    IF new_student_id = old_student_id THEN
        -- School changed but student stayed the same

        -- Update student contact association records for this student school association
        UPDATE dms.ContactStudentSchoolAuthorization
        SET
            ContactStudentSchoolAuthorizationEducationOrganizationIds = ancestor_ed_org_ids
        WHERE
            StudentUniqueId = new_student_id AND
            StudentSchoolAssociationId = NEW.Id AND
            StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey;

        FOR contact_id IN
            SELECT DISTINCT ContactUniqueId
            FROM dms.ContactStudentSchoolAuthorization
            WHERE
                StudentUniqueId = new_student_id AND
                StudentSchoolAssociationId = NEW.Id AND
                StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey
        LOOP
            PERFORM dms.SetEdOrgIdsToContactSecurables(dms.GetContactEdOrgIds(contact_id), contact_id);
        END LOOP;

    ELSE
        -- Student changed

        -- Remove authorization data from old student's contact associations
        UPDATE dms.ContactStudentSchoolAuthorization
        SET
            ContactStudentSchoolAuthorizationEducationOrganizationIds = '[]'
        WHERE
            StudentUniqueId = old_student_id  AND
            StudentSchoolAssociationId = NEW.Id AND
            StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey;

        -- Remove access to old student Contacts
        FOR contact_id IN
            SELECT DISTINCT ContactUniqueId
            FROM dms.ContactStudentSchoolAuthorization WHERE StudentUniqueId = old_student_id
        LOOP
            PERFORM dms.SetEdOrgIdsToContactSecurables(dms.GetContactEdOrgIds(contact_id), contact_id);
        END LOOP;

        -- Insert new authorizations and update securables for contacts of the new student
        FOR contact_id IN
            SELECT DISTINCT ContactUniqueId
            FROM dms.ContactStudentSchoolAuthorization WHERE StudentUniqueId = new_student_id
        LOOP
            PERFORM dms.InsertContactStudentSchoolAuthorization(new_student_id, ancestor_ed_org_ids, NEW.Id, NEW.DocumentPartitionKey, contact_id);
        END LOOP;

    END IF;

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
