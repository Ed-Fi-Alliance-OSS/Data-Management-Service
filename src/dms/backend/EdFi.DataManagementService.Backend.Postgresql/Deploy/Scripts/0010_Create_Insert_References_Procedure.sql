-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Replace the legacy delete-then-insert implementation with an ON CONFLICT upsert.
-- Returns false when any referentialId failed to resolve; callers can inspect
-- temp_reference_stage for details while the session remains open.

DROP FUNCTION IF EXISTS dms.InsertReferences(
    BIGINT[],
    SMALLINT[],
    UUID[],
    SMALLINT[]
);

CREATE OR REPLACE FUNCTION dms.InsertReferences(
    parentDocumentId BIGINT,
    parentDocumentPartitionKey SMALLINT,
    referentialIds UUID[],
    referentialPartitionKeys SMALLINT[]
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS
$$
DECLARE
    invalid BOOLEAN;
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS temp_reference_stage
    (
        parentdocumentid               BIGINT,
        parentdocumentpartitionkey     SMALLINT,
        referentialpartitionkey        SMALLINT,
        referentialid                  UUID,
        aliasid                        BIGINT,
        referenceddocumentid           BIGINT,
        referenceddocumentpartitionkey SMALLINT
    ) ON COMMIT PRESERVE ROWS;

    TRUNCATE temp_reference_stage;

    INSERT INTO temp_reference_stage
    SELECT
        parentDocumentId,
        parentDocumentPartitionKey,
        ids.referentialPartitionKey,
        ids.referentialId,
        a.Id,
        a.DocumentId,
        a.DocumentPartitionKey
    FROM unnest(referentialIds, referentialPartitionKeys)
        AS ids(referentialId, referentialPartitionKey)
    LEFT JOIN dms.Alias a
        ON a.ReferentialId = ids.referentialId
       AND a.ReferentialPartitionKey = ids.referentialPartitionKey;

    WITH upsert AS (
        INSERT INTO dms.Reference (
            ParentDocumentId,
            ParentDocumentPartitionKey,
            AliasId,
            ReferentialPartitionKey,
            ReferencedDocumentId,
            ReferencedDocumentPartitionKey
        )
        SELECT
            parentdocumentid,
            parentdocumentpartitionkey,
            aliasid,
            referentialpartitionkey,
            referenceddocumentid,
            referenceddocumentpartitionkey
        FROM temp_reference_stage
        WHERE aliasid IS NOT NULL
        ON CONFLICT ON CONSTRAINT reference_parent_alias_unique
        DO UPDATE
           SET ReferentialPartitionKey = EXCLUDED.ReferentialPartitionKey,
               ReferencedDocumentId = EXCLUDED.ReferencedDocumentId,
               ReferencedDocumentPartitionKey = EXCLUDED.ReferencedDocumentPartitionKey
        WHERE (
              dms.Reference.ReferentialPartitionKey,
              dms.Reference.ReferencedDocumentId,
              dms.Reference.ReferencedDocumentPartitionKey
        ) IS DISTINCT FROM (
              EXCLUDED.ReferentialPartitionKey,
              EXCLUDED.ReferencedDocumentId,
              EXCLUDED.ReferencedDocumentPartitionKey
        )
        RETURNING 1
    )
    DELETE FROM dms.Reference r
    WHERE r.ParentDocumentId = parentDocumentId
      AND r.ParentDocumentPartitionKey = parentDocumentPartitionKey
      AND NOT EXISTS (
          SELECT 1
          FROM temp_reference_stage s
          WHERE s.aliasid = r.aliasid
            AND s.referentialpartitionkey = r.referentialpartitionkey
      );

    invalid := EXISTS (
        SELECT 1
        FROM temp_reference_stage
        WHERE aliasid IS NULL
    );

    RETURN NOT invalid;
END;
$$;
