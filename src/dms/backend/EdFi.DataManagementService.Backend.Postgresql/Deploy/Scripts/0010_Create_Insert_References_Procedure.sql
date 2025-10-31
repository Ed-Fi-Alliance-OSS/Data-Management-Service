-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Returns false when any referentialId is found to be invalid
-- Inspect temp_reference_stage for details while the session remains open
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    p_parentDocumentId BIGINT,
    p_parentDocumentPartitionKey SMALLINT,
    p_referentialIds UUID[],
    p_referentialPartitionKeys SMALLINT[]
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS
$$
BEGIN
    -- Temp table caches the staged references for the current session scope.
    -- ON COMMIT PRESERVE ROWS keeps the data available until the caller finishes.
    CREATE TEMP TABLE IF NOT EXISTS temp_reference_stage
    (
        parentdocumentid               BIGINT,
        parentdocumentpartitionkey     SMALLINT,
        referentialpartitionkey        SMALLINT,
        referentialid                  UUID,
        aliasid                        BIGINT,
        referenceddocumentid           BIGINT,
        referenceddocumentpartitionkey SMALLINT
    ) ON COMMIT PRESERVE ROWS;  -- Available to get invalid referentialId information on failure

    -- Guarantee we start from a clean slate for each invocation.
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
        -- Expand the parallel referentialId/partition key arrays into individual rows for staging.
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
    -- Keeps unresolved alias rows while taking only the first resolved match per alias
    WHERE aliasid IS NULL OR alias_row_number = 1;

    -- If any staged rows failed to resolve aliases, do not mutate persistent tables.
    IF EXISTS (
        SELECT 1
        FROM temp_reference_stage
        WHERE aliasid IS NULL
    ) THEN
        RETURN FALSE;
    END IF;

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
        -- Use the unique constraint to detect existing reference rows. Deduplicated staging
        -- rows ensure ON CONFLICT only fires once per target row within a single statement.
        ON CONFLICT ON CONSTRAINT ux_reference_parent_alias
        -- Refresh the target when any downstream document metadata changed; skip the write when the
        -- triple matches to avoid unnecessary churn and triggering immutability checks.
        DO UPDATE
           SET ReferentialPartitionKey = EXCLUDED.ReferentialPartitionKey,
               ReferencedDocumentId = EXCLUDED.ReferencedDocumentId,
               ReferencedDocumentPartitionKey = EXCLUDED.ReferencedDocumentPartitionKey
        -- Only perform the update when the persisted targeting metadata actually differs.
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
    -- Remove references tied to the parent document that were not included in this upsert request,
    -- ensuring the database reflects exactly the references supplied in the current operation.
    DELETE FROM dms.Reference r
    WHERE r.ParentDocumentId = p_parentDocumentId
      AND r.ParentDocumentPartitionKey = p_parentDocumentPartitionKey
      AND NOT EXISTS (
          SELECT 1
          FROM temp_reference_stage s
          WHERE s.aliasid = r.aliasid
            AND s.referentialpartitionkey = r.referentialpartitionkey
      );

    -- All staged references were valid, so return true.
    RETURN TRUE;
END;
$$;
