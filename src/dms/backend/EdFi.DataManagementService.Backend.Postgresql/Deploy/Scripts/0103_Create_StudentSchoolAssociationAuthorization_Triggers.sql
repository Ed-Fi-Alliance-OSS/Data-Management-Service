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
BEGIN
    IF (TG_OP = 'INSERT') THEN
        INSERT INTO dms.StudentSchoolAssociationAuthorization (
            StudentUniqueId,
            HierarchySchoolId,
            StudentSchoolAuthorizationEducationOrganizationIds,
            StudentSchoolAssociationId,
            StudentSchoolAssociationPartitionKey
        )
        VALUES (
             NEW.EdfiDoc->'studentReference'->>'studentUniqueId',
            (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT,
            (
                SELECT jsonb_agg(EducationOrganizationId)
                FROM dms.GetEducationOrganizationAncestors((NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT)
            ),
            NEW.Id,
            NEW.DocumentPartitionKey
        );

        UPDATE dms.Document
        SET StudentSchoolAuthorizationEdOrgIds = (
            SELECT jsonb_agg(EducationOrganizationId)
            FROM dms.GetEducationOrganizationAncestors((NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT)
        )
        WHERE
            Id = NEW.Id AND
            DocumentPartitionKey = NEW.DocumentPartitionKey;
    ELSIF (TG_OP = 'UPDATE') THEN
        UPDATE dms.StudentSchoolAssociationAuthorization
        SET
            StudentUniqueId = NEW.EdfiDoc->'studentReference'->>'studentUniqueId',
            HierarchySchoolId = (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT,
            StudentSchoolAuthorizationEducationOrganizationIds = (
                SELECT jsonb_agg(EducationOrganizationId)
                FROM dms.GetEducationOrganizationAncestors((NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT)
            )
        WHERE
            StudentSchoolAssociationId = NEW.Id AND
            StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey;

        UPDATE dms.Document
        SET StudentSchoolAuthorizationEdOrgIds = (
            SELECT jsonb_agg(EducationOrganizationId)
            FROM dms.GetEducationOrganizationAncestors((NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT)
        )
        WHERE
            Id = NEW.Id AND
            DocumentPartitionKey = NEW.DocumentPartitionKey;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS StudentSchoolAssociationAuthorizationTrigger ON dms.Document;

CREATE TRIGGER StudentSchoolAssociationAuthorizationTrigger
AFTER INSERT OR UPDATE OF EdFiDoc ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationTriggerFunction();
