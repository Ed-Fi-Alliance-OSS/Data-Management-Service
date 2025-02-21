-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR REPLACE FUNCTION dms.EducationOrganizationHierarchyTriggerFunction()
RETURNS TRIGGER
AS $$
BEGIN
 IF (TG_OP = 'INSERT') THEN
    INSERT INTO dms.EducationOrganizationHierarchyTermsLookup(Id, Hierarchy)
    VALUES (NEW.EducationOrganizationId, jsonb_build_array(NEW.EducationOrganizationId));

    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = jsonb_insert(
        Hierarchy,
        '{-1}',
        to_jsonb(NEW.EducationOrganizationId), true)
    WHERE Id = (SELECT EducationOrganizationId FROM dms.EducationOrganizationHierarchy WHERE Id = NEW.ParentId);
 ELSIF( TG_OP = 'UPDATE' AND NEW.ParentId <> OLD.ParentId ) THEN
    -- Remove from the old parent
    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = (
            SELECT jsonb_agg(elem)
            FROM jsonb_array_elements(Hierarchy) elem
            WHERE elem <> to_jsonb(OLD.EducationOrganizationId)
        )
    WHERE Id = (SELECT EducationOrganizationId FROM dms.EducationOrganizationHierarchy WHERE Id = OLD.ParentId);

    -- Add to the new parent
    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = jsonb_insert(
        Hierarchy,
        '{-1}',
        to_jsonb(NEW.EducationOrganizationId), true)
    WHERE Id = (SELECT EducationOrganizationId FROM dms.EducationOrganizationHierarchy WHERE Id = NEW.ParentId);
 ELSIF (TG_OP = 'DELETE') THEN
	DELETE FROM dms.EducationOrganizationHierarchyTermsLookup
	WHERE Id = OLD.EducationOrganizationId;

	UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = (
            SELECT jsonb_agg(elem)
            FROM jsonb_array_elements(Hierarchy) elem
            WHERE elem <> to_jsonb(OLD.EducationOrganizationId)
        )
    WHERE Id = (SELECT EducationOrganizationId FROM dms.EducationOrganizationHierarchy WHERE Id = OLD.ParentId);
 END IF;
RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER EducationOrganizationHierarchyTrigger
AFTER INSERT OR UPDATE OR DELETE ON dms.EducationOrganizationHierarchy
    FOR EACH ROW
    EXECUTE PROCEDURE dms.EducationOrganizationHierarchyTriggerFunction()
