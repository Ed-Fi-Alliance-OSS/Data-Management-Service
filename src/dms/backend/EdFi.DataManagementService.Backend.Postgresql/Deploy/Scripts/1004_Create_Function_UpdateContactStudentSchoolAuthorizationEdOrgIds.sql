-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Helper function to handle the aggregation of ContactStudentSchoolAuthorizationEducationOrganizationIds
-- and updating the dms.Document table
CREATE OR REPLACE FUNCTION dms.UpdateContactStudentSchoolAuthorizationEdOrgIds(
    student_id text
)
RETURNS VOID
AS $$
DECLARE
    contact_id text;
    unified_ed_org_ids jsonb := '[]';
    contact_secureable_doc_id BIGINT;
    contact_secureable_doc_key SMALLINT;
BEGIN
    -- Loop through all contact IDs associated with the given student ID
    FOR contact_id IN
        SELECT ContactUniqueId
        FROM dms.ContactStudentSchoolAuthorization
        WHERE StudentUniqueId = student_id
    LOOP
        -- Aggregate and merge distinct values into unified_ed_org_ids
        SELECT jsonb_agg(DISTINCT value)
        INTO unified_ed_org_ids
        FROM (
            SELECT DISTINCT jsonb_array_elements(ContactStudentSchoolAuthorizationEducationOrganizationIds) AS value
            FROM dms.ContactStudentSchoolAuthorization
            WHERE ContactUniqueId = contact_id
        ) subquery;

        -- Retrieve ContactSecurableDocumentId and ContactSecurableDocumentPartitionKey
        SELECT ContactSecurableDocumentId, ContactSecurableDocumentPartitionKey
        INTO contact_secureable_doc_id, contact_secureable_doc_key
        FROM dms.ContactSecurableDocument
        WHERE ContactUniqueId = contact_id;

        -- Update the Document table with the unified_ed_org_ids
        UPDATE dms.Document
        SET ContactStudentSchoolAuthorizationEdOrgIds = unified_ed_org_ids
        WHERE 
            Id = contact_secureable_doc_id AND
            DocumentPartitionKey = contact_secureable_doc_key;
    END LOOP;
END;
$$ LANGUAGE plpgsql;
