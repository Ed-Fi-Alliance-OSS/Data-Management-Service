-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Attempts to insert references into Reference table.
-- If any referentialId is invalid, those rows are omitted and the function
-- returns the invalid referentialIds; the caller is responsible for transaction
-- semantics (e.g., rolling back the broader operation if desired).
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    parentDocumentIds BIGINT[],
    parentDocumentPartitionKeys SMALLINT[],
    referentialIds UUID[],
    referentialPartitionKeys SMALLINT[]
) RETURNS TABLE (ReferentialId UUID)
LANGUAGE plpgsql AS
$$
BEGIN

    -- First clear out all the existing references, as they may have changed
    DELETE from dms.Reference r
    USING unnest(parentDocumentIds, parentDocumentPartitionKeys) as d (Id, DocumentPartitionKey)
    WHERE d.Id = r.ParentDocumentId AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey;

    RETURN QUERY
    WITH payload AS (
        SELECT
            ids.documentId,
            ids.documentPartitionKey,
            ids.referentialId,
            ids.referentialPartitionKey,
            a.Id AS aliasId
        FROM unnest(parentDocumentIds, parentDocumentPartitionKeys, referentialIds, referentialPartitionKeys) AS
            ids(documentId, documentPartitionKey, referentialId, referentialPartitionKey)
        LEFT JOIN dms.Alias a ON
            a.ReferentialId = ids.referentialId
            AND a.ReferentialPartitionKey = ids.referentialPartitionKey
    ),
    inserted AS (
        INSERT INTO dms.Reference (
            ParentDocumentId,
            ParentDocumentPartitionKey,
            AliasId,
            ReferentialPartitionKey
        )
        SELECT
            documentId,
            documentPartitionKey,
            aliasId,
            referentialPartitionKey
        FROM payload
        WHERE aliasId IS NOT NULL
        RETURNING 1
    )
    SELECT payload.referentialId
    FROM payload
    WHERE payload.aliasId IS NULL;
END;
$$;
