-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

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
                FROM dms.GetEducationOrganizationAncestors(NEW.DocumentUuid, NEW.DocumentPartitionKey)
            ),
            NEW.Id,
            NEW.DocumentPartitionKey
        );
    ELSIF (TG_OP = 'UPDATE') THEN
        UPDATE dms.StudentSchoolAssociationAuthorization
        SET
            StudentUniqueId = NEW.EdfiDoc->'studentReference'->>'studentUniqueId',
            HierarchySchoolId = (NEW.EdfiDoc->'schoolReference'->>'schoolId')::BIGINT,
            StudentSchoolAuthorizationEducationOrganizationIds = (
                SELECT jsonb_agg(EducationOrganizationId)
                FROM dms.GetEducationOrganizationAncestors(NEW.DocumentUuid, NEW.DocumentPartitionKey)
            )
        WHERE
            StudentSchoolAssociationId = NEW.Id AND
            StudentSchoolAssociationPartitionKey = NEW.DocumentPartitionKey;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER StudentSchoolAssociationAuthorizationTrigger
AFTER INSERT OR UPDATE ON dms.Document
    FOR EACH ROW
    WHEN (NEW.ProjectName = 'Ed-Fi' AND NEW.ResourceName = 'StudentSchoolAssociation')
    EXECUTE PROCEDURE dms.StudentSchoolAssociationAuthorizationTriggerFunction();
