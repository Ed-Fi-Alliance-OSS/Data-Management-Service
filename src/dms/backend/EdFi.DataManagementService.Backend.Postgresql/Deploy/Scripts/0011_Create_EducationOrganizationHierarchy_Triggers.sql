-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Maintains the hierarchy of the EducationOrganizationHierarchyTermsLookup table
-- when the EducationOrganizationHierarchy table is updated.
CREATE OR REPLACE FUNCTION dms.EducationOrganizationHierarchyTriggerFunction()
RETURNS TRIGGER
AS $$
BEGIN
 IF (TG_OP = 'INSERT') THEN
    -- Add the new record, hierarchies always start with themselves as the single element in the array
    INSERT INTO dms.EducationOrganizationHierarchyTermsLookup(Id, Hierarchy)
    VALUES (NEW.EducationOrganizationId, jsonb_build_array(NEW.EducationOrganizationId::TEXT));

    -- Find all ancestors of the new education organization using recursive CTE
    WITH RECURSIVE ancestors AS (
        -- Start with the direct parent
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        WHERE h.Id = NEW.ParentId

        UNION ALL

        -- Recursively get all parents up the hierarchy
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        JOIN ancestors a ON h.Id = a.ParentId
    )
    -- Update the hierarchy for all ancestors
    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = jsonb_insert(
        Hierarchy,
        '{-1}',  -- Insert at the end
        to_jsonb(NEW.EducationOrganizationId::TEXT),
        true
    )
    WHERE Id IN (SELECT EducationOrganizationId FROM ancestors);

 ELSIF (TG_OP = 'UPDATE' AND NEW.ParentId <> OLD.ParentId) THEN
    -- Find all ancestors of the old parent
    WITH RECURSIVE old_ancestors AS (
        -- Start with the direct old parent
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        WHERE h.Id = OLD.ParentId

        UNION ALL

        -- Recursively get all parents up the hierarchy
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        JOIN old_ancestors a ON h.Id = a.ParentId
    )
    -- Remove from all old ancestors' hierarchies
    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = (
        SELECT jsonb_agg(elem)
        FROM jsonb_array_elements(Hierarchy) elem
        WHERE elem <> to_jsonb(OLD.EducationOrganizationId::TEXT)
    )
    WHERE Id IN (SELECT EducationOrganizationId FROM old_ancestors);

    -- Find all ancestors of the new parent
    WITH RECURSIVE new_ancestors AS (
        -- Start with the direct new parent
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        WHERE h.Id = NEW.ParentId

        UNION ALL

        -- Recursively get all parents up the hierarchy
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        JOIN new_ancestors a ON h.Id = a.ParentId
    )
    -- Add to all new ancestors' hierarchies
    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = jsonb_insert(
        Hierarchy,
        '{-1}',  -- Insert at the end
        to_jsonb(NEW.EducationOrganizationId::TEXT),
        true
    )
    WHERE Id IN (SELECT EducationOrganizationId FROM new_ancestors);

 ELSIF (TG_OP = 'DELETE') THEN
    -- Delete this record
    DELETE FROM dms.EducationOrganizationHierarchyTermsLookup
    WHERE Id = OLD.EducationOrganizationId;

    -- Find all ancestors of the deleted organization
    WITH RECURSIVE ancestors AS (
        -- Start with the direct parent
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        WHERE h.Id = OLD.ParentId

        UNION ALL

        -- Recursively get all parents up the hierarchy
        SELECT h.Id, h.EducationOrganizationId, h.ParentId
        FROM dms.EducationOrganizationHierarchy h
        JOIN ancestors a ON h.Id = a.ParentId
    )
    -- Remove the deleted item from all ancestors' hierarchy arrays
    UPDATE dms.EducationOrganizationHierarchyTermsLookup
    SET Hierarchy = (
        SELECT jsonb_agg(elem)
        FROM jsonb_array_elements(Hierarchy) elem
        WHERE elem <> to_jsonb(OLD.EducationOrganizationId::TEXT)
    )
    WHERE Id IN (SELECT EducationOrganizationId FROM ancestors);
 END IF;

RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS EducationOrganizationHierarchyTrigger ON dms.educationorganizationhierarchy;
CREATE TRIGGER EducationOrganizationHierarchyTrigger
AFTER INSERT OR UPDATE OR DELETE ON dms.EducationOrganizationHierarchy
    FOR EACH ROW
    EXECUTE PROCEDURE dms.EducationOrganizationHierarchyTriggerFunction();
