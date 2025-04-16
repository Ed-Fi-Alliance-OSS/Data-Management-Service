-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When StudentSchoolAssociation documents are inserted or updated,
-- this trigger synchronizes the StudentSchoolAssociationAuthorization table.
-- Deletes are simply cascaded from the Document table.
CREATE OR REPLACE FUNCTION dms.StudentSchoolAssociationAuthorizationTriggerFunction()
RETURNS TRIGGER
AS $$
DECLARE
    ed_org_ids jsonb;
    student_id text;
    new_student_id text;
    old_student_id text;
    new_school_id bigint;
    old_school_id bigint;
BEGIN
    IF (TG_OP = 'INSERT') THEN
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
        UPDATE dms.Document d
        SET StudentSchoolAuthorizationEdOrgIds = ed_org_ids
        FROM dms.StudentSecurableDocument ssd
        WHERE
            ssd.StudentUniqueId = student_id AND
            d.Id = ssd.StudentSecurableDocumentId AND
            d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;

    ELSIF (TG_OP = 'UPDATE') THEN
        -- Extract values from NEW and OLD records
        new_student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
        old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';
        new_school_id := (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT;
        old_school_id := (OLD.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT;

        -- Skip if neither StudentUniqueId nor SchoolId has changed
        IF new_student_id = old_student_id AND new_school_id = old_school_id THEN
            RETURN NULL;
        END IF;

        -- Calculate Ed Org IDs once and store in variable
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
        UPDATE dms.Document d
        SET StudentSchoolAuthorizationEdOrgIds = ed_org_ids
        FROM dms.StudentSecurableDocument ssd
        WHERE
            ssd.StudentUniqueId = new_student_id AND
            d.Id = ssd.StudentSecurableDocumentId AND
            d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;

        -- If student ID changed, update related documents for both old and new students
        IF new_student_id != old_student_id THEN
            -- Update all documents for the old student
            UPDATE dms.Document d
            SET StudentSchoolAuthorizationEdOrgIds = (
                SELECT jsonb_agg(EducationOrganizationId)
                FROM dms.GetEducationOrganizationAncestors(old_school_id)
            )
            FROM dms.StudentSecurableDocument ssd
            WHERE
                ssd.StudentUniqueId = old_student_id AND
                d.Id = ssd.StudentSecurableDocumentId AND
                d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;
        END IF;
    ELSIF (TG_OP = 'DELETE') THEN
        old_student_id := OLD.EdfiDoc->'studentReference'->>'studentUniqueId';

        -- Update all documents for the old student
        UPDATE dms.Document d
        SET StudentSchoolAuthorizationEdOrgIds = NULL
        FROM dms.StudentSecurableDocument ssd
        WHERE
            ssd.StudentUniqueId = old_student_id AND
            d.Id = ssd.StudentSecurableDocumentId AND
            d.DocumentPartitionKey = ssd.StudentSecurableDocumentPartitionKey;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS StudentSchoolAssociationAuthorizationTrigger_Insert_Update ON dms.Document;

CREATE TRIGGER StudentSchoolAssociationAuthorizationTrigger_Insert_Update
AFTER INSERT OR UPDATE OF EdfiDoc ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationTriggerFunction();

DROP TRIGGER IF EXISTS StudentSchoolAssociationAuthorizationTrigger_Delete ON dms.Document;

CREATE TRIGGER StudentSchoolAssociationAuthorizationTrigger_Delete
AFTER DELETE ON dms.Document
    FOR EACH ROW
    WHEN (OLD.ProjectName = 'Ed-Fi' AND OLD.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationTriggerFunction();
