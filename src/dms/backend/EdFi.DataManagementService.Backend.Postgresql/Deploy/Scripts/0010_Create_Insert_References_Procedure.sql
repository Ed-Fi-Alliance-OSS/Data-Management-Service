-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Replace the legacy delete-then-insert implementation with an ON CONFLICT upsert.
-- Returns false when any referentialId failed to resolve; callers can inspect
-- temp_reference_stage for details while the session remains open.

CREATE OR REPLACE FUNCTION dms.InsertReferences(
    p_parentDocumentId BIGINT,
    p_parentDocumentPartitionKey SMALLINT,
    p_referentialIds UUID[],
    p_referentialPartitionKeys SMALLINT[]
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

    WITH staged AS (
        -- Materialize the incoming references along with resolved alias/document metadata.
        -- ROW_NUMBER keeps only a single row per alias, preventing PostgreSQL from hitting
        -- the same ON CONFLICT target more than once in the same statement.
        SELECT
            p_parentDocumentId AS parentdocumentid,
            p_parentDocumentPartitionKey AS parentdocumentpartitionkey,
            ids.referentialPartitionKey,
            ids.referentialId,
            a.Id AS aliasid,
            a.DocumentId AS referenceddocumentid,
            a.DocumentPartitionKey AS referenceddocumentpartitionkey,
            ROW_NUMBER() OVER (PARTITION BY a.Id ORDER BY ids.referentialId) AS alias_row_number
        FROM unnest(p_referentialIds, p_referentialPartitionKeys)
            AS ids(referentialId, referentialPartitionKey)
        LEFT JOIN dms.Alias a
            ON a.ReferentialId = ids.referentialId
           AND a.ReferentialPartitionKey = ids.referentialPartitionKey
    )
    INSERT INTO temp_reference_stage
    SELECT
        parentdocumentid,
        parentdocumentpartitionkey,
        referentialpartitionkey,
        referentialid,
        aliasid,
        referenceddocumentid,
        referenceddocumentpartitionkey
    FROM staged
    WHERE aliasid IS NULL OR alias_row_number = 1;

    WITH upsert AS (
        -- Perform the reference upsert, relying on the deduplicated staging rows above.
        INSERT INTO dms.Reference AS target (
            ParentDocumentId,
            ParentDocumentPartitionKey,
            AliasId,
            ReferentialPartitionKey,
            ReferencedDocumentId,
            ReferencedDocumentPartitionKey
        )
        SELECT
            s.parentdocumentid,
            s.parentdocumentpartitionkey,
            s.aliasid,
            s.referentialpartitionkey,
            s.referenceddocumentid,
            s.referenceddocumentpartitionkey
        FROM temp_reference_stage s
        WHERE s.aliasid IS NOT NULL
        ON CONFLICT ON CONSTRAINT reference_parent_alias_unique
        DO UPDATE
           SET ReferentialPartitionKey = EXCLUDED.ReferentialPartitionKey,
               ReferencedDocumentId = EXCLUDED.ReferencedDocumentId,
               ReferencedDocumentPartitionKey = EXCLUDED.ReferencedDocumentPartitionKey
        WHERE (
              target.ReferentialPartitionKey,
              target.ReferencedDocumentId,
              target.ReferencedDocumentPartitionKey
        ) IS DISTINCT FROM (
              EXCLUDED.ReferentialPartitionKey,
              EXCLUDED.ReferencedDocumentId,
              EXCLUDED.ReferencedDocumentPartitionKey
        )
        RETURNING 1
    )
    DELETE FROM dms.Reference r
    WHERE r.ParentDocumentId = p_parentDocumentId
      AND r.ParentDocumentPartitionKey = p_parentDocumentPartitionKey
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
