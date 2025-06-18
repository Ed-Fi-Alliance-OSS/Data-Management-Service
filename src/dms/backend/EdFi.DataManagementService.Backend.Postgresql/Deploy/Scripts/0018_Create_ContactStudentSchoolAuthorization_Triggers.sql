-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- When StudentContactAssociation documents are inserted or updated,
-- this trigger synchronizes the ContactStudentSchoolAuthorization table.
-- Deletes are simply cascaded from the Document table.

-- Sets the EducationOrganizationIds to all the contact-securable documents
-- of a given contact
CREATE OR REPLACE FUNCTION dms.SetEdOrgIdsToContactSecurables(
    ed_org_ids jsonb,
    contact_id text
)
RETURNS VOID
AS $$
BEGIN
    UPDATE dms.Document doc
    SET ContactStudentSchoolAuthorizationEdOrgIds = ed_org_ids
    FROM dms.ContactSecurableDocument csd
    WHERE
        csd.ContactUniqueId = contact_id AND
        doc.Id = csd.ContactSecurableDocumentId AND
        doc.DocumentPartitionKey = csd.ContactSecurableDocumentPartitionKey;
END;
$$ LANGUAGE plpgsql;

-- Returns the combined EducationOrganizationIds of all the ContactStudentSchoolAuthorization
-- of a given contact
CREATE OR REPLACE FUNCTION dms.GetContactEdOrgIds(
    contact_id text
)
RETURNS jsonb AS $$
BEGIN
  RETURN (
    SELECT jsonb_agg(DISTINCT edOrgIds)
        FROM (
            SELECT jsonb_array_elements(ContactStudentSchoolAuthorizationEducationOrganizationIds) AS edOrgIds
            FROM dms.ContactStudentSchoolAuthorization
            WHERE ContactUniqueId = contact_id
        )
  );
END;
$$ LANGUAGE plpgsql;

-- Function for INSERT operations
CREATE OR REPLACE FUNCTION dms.ContactStudentSchoolAuthorizationDocumentInsertFunction()
RETURNS TRIGGER
AS $$
DECLARE
    student_id text;
    contact_id text;
    ed_org_ids jsonb;
BEGIN
    student_id := NEW.EdfiDoc->'studentReference'->>'studentUniqueId';
    contact_id := NEW.EdfiDoc->'contactReference'->>'contactUniqueId';

    -- INSERT StudentContactRelation
    INSERT INTO dms.StudentContactRelation (
        StudentUniqueId,
        ContactUniqueId,
        StudentContactAssociationDocumentId,
        StudentContactAssociationDocumentPartitionKey
    )
    SELECT
        student_id,
        contact_id,
        NEW.Id,
        NEW.DocumentPartitionKey;

    -- Extract student school association document details
    INSERT INTO dms.ContactStudentSchoolAuthorization (
        ContactUniqueId,
        StudentUniqueId,
        ContactStudentSchoolAuthorizationEducationOrganizationIds,
        StudentContactAssociationId,
        StudentContactAssociationPartitionKey,
        StudentSchoolAssociationId,
        StudentSchoolAssociationPartitionKey
    )
    SELECT contact_id,
        student_id,
        StudentSchoolAuthorizationEducationOrganizationIds,
        NEW.Id,
        NEW.DocumentPartitionKey,
        StudentSchoolAssociationId,
        StudentSchoolAssociationPartitionKey
    FROM dms.StudentSchoolAssociationAuthorization
    WHERE StudentUniqueId = student_id;

    ed_org_ids := dms.GetContactEdOrgIds(contact_id);
    PERFORM dms.SetEdOrgIdsToContactSecurables(ed_org_ids, contact_id);

    -- Manually update the newly inserted ContactStudentSchoolAuthorization because it's not a
    -- contact-securable at this point
    UPDATE dms.Document
    SET ContactStudentSchoolAuthorizationEdOrgIds = ed_org_ids
    WHERE
        Id = NEW.Id AND
        DocumentPartitionKey = NEW.DocumentPartitionKey;

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
    old_contact_id := OLD.ContactUniqueId;

    -- Update edorg id list for the contact securable documents
    PERFORM dms.SetEdOrgIdsToContactSecurables(dms.GetContactEdOrgIds(old_contact_id), old_contact_id);

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- Drop and recreate triggers
DROP TRIGGER IF EXISTS ContactStudentSchoolAuthorization_Document_Insert_Trigger ON dms.Document;
CREATE TRIGGER ContactStudentSchoolAuthorization_Document_Insert_Trigger
AFTER INSERT ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentContactAssociation')
    EXECUTE PROCEDURE dms.ContactStudentSchoolAuthorizationDocumentInsertFunction();

DROP TRIGGER IF EXISTS ContactStudentSchoolAuthorization_Delete_Trigger ON dms.ContactStudentSchoolAuthorization;
CREATE TRIGGER ContactStudentSchoolAuthorization_Delete_Trigger
AFTER DELETE ON dms.ContactStudentSchoolAuthorization
    FOR EACH ROW
    EXECUTE PROCEDURE dms.ContactStudentSchoolAuthorizationDeleteFunction();
