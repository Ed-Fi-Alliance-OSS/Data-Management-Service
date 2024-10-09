-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Attempts to insert references into Reference table
-- If any referentialId is invalid, rolls back and returns an array of invalid referentialIds
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    parentDocumentIds BIGINT[],
    parentDocumentPartitionKeys SMALLINT[],
    referentialIds UUID[],
    referentialPartitionKeys SMALLINT[]
) RETURNS TABLE (ReferentialId UUID)
LANGUAGE plpgsql AS
$$
DECLARE
    constraintErrorName text;
BEGIN
    INSERT INTO dms.Reference (
        ParentDocumentId,
        ParentDocumentPartitionKey,
        ReferentialId,
        ReferentialPartitionKey,
        ReferencedDocumentId,
        ReferencedDocumentPartitionKey
    )
    SELECT
        ids.documentId,
        ids.documentPartitionKey,
        ids.referentialId,
        ids.referentialPartitionKey,
        a.documentId,
        a.documentPartitionKey
    FROM unnest(parentDocumentIds, parentDocumentPartitionKeys, referentialIds, referentialPartitionKeys) AS
        ids(documentId, documentPartitionKey, referentialId, referentialPartitionKey)
    LEFT JOIN dms.Alias a ON
        ids.referentialId = a.referentialId and ids.referentialPartitionKey = a.referentialPartitionKey;
    RETURN;

EXCEPTION
    -- Check if there were bad referentialIds: FK violation involving referentialIds
    WHEN foreign_key_violation then
    	GET STACKED DIAGNOSTICS constraintErrorName = CONSTRAINT_NAME;
    	IF constraintErrorName = 'fk_reference_referencedalias' THEN
	        RETURN QUERY SELECT r.ReferentialId
	            FROM ROWS FROM
	                (unnest(referentialIds), unnest(referentialPartitionKeys))
	                AS r (ReferentialId, ReferentialPartitionKey)
	            WHERE NOT EXISTS (
	                SELECT 1
	                FROM dms.Alias a
	                WHERE r.ReferentialId = a.ReferentialId
	                AND r.ReferentialPartitionKey = a.ReferentialPartitionKey);
        -- Some other foreign key violation
        ELSE RAISE;
        END IF;
    -- Some other exception
    WHEN OTHERS THEN RAISE;
end;
$$;
