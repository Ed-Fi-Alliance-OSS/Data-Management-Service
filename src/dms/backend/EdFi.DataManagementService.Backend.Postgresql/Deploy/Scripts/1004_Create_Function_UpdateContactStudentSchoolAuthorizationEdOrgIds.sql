-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Helper function to handle the aggregation of ContactStudentSchoolAuthorizationEducationOrganizationIds
-- and updating the dms.Document table
CREATE OR REPLACE FUNCTION dms.UpdateContactStudentSchoolAuthorizationEdOrgIds(
    contact_id text
)
RETURNS VOID
AS $$
DECLARE
    unified_ed_org_ids jsonb := '[]';
BEGIN
        -- Aggregate and merge distinct values into unified_ed_org_ids
        SELECT jsonb_agg(DISTINCT value)
        INTO unified_ed_org_ids
        FROM (
            SELECT DISTINCT jsonb_array_elements(ContactStudentSchoolAuthorizationEducationOrganizationIds) AS value
            FROM dms.ContactStudentSchoolAuthorization
            WHERE ContactUniqueId = contact_id
        ) subquery;

        -- Update the Document table with the unified_ed_org_ids
        UPDATE dms.Document doc
        SET ContactStudentSchoolAuthorizationEdOrgIds = unified_ed_org_ids
        FROM dms.ContactSecurableDocument csd
        WHERE
            csd.ContactUniqueId = contact_id AND
            doc.Id = csd.ContactSecurableDocumentId AND
            doc.DocumentPartitionKey = csd.ContactSecurableDocumentPartitionKey;
END;
$$ LANGUAGE plpgsql;
