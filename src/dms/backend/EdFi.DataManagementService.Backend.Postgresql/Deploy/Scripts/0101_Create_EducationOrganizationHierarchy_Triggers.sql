-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR REPLACE FUNCTION dms.EducationOrganizationHierarchyTriggerFunction()
RETURNS TRIGGER
AS $$
BEGIN
    INSERT INTO dms.EducationOrganizationHierarchyTermsLookup(Id, Hierarchy)
    VALUES (NEW.EducationOrganizationId, jsonb_build_array(NEW.EducationOrganizationId));

    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = jsonb_insert(
        Hierarchy,
        '{-1}',
        to_jsonb(NEW.EducationOrganizationId), true)
    WHERE Id = (SELECT EducationOrganizationId FROM dms.EducationOrganizationHierarchy WHERE Id = NEW.ParentId);

RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER EducationOrganizationHierarchyTrigger
AFTER INSERT ON dms.EducationOrganizationHierarchy
    FOR EACH ROW
    EXECUTE PROCEDURE dms.EducationOrganizationHierarchyTriggerFunction()
