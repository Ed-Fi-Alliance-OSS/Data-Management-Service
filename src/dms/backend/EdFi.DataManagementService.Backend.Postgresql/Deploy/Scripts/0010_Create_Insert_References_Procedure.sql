-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Upserts document references for a parent document.
-- The provided referential id / partition key pairs MUST be unique; callers must deduplicate them
-- before invoking this function. Returns a success flag plus any referentialIds that are invalid.
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    p_parentDocumentId BIGINT,
    p_parentDocumentPartitionKey SMALLINT,
    p_referentialIds UUID[],
    p_referentialPartitionKeys SMALLINT[]
) RETURNS TABLE (
    success BOOLEAN,
    invalid_ids UUID[]
)
LANGUAGE plpgsql
AS
$$
DECLARE
    reference_partition TEXT;
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

    -- Guarantee we start from a clean slate
    DELETE FROM temp_reference_stage
    WHERE parentdocumentid = p_parentDocumentId
      AND parentdocumentpartitionkey = p_parentDocumentPartitionKey;

    WITH staged AS (
        -- Materialize the incoming references along with resolved alias/document metadata.
        SELECT
            p_parentDocumentId AS parentdocumentid,
            p_parentDocumentPartitionKey AS parentdocumentpartitionkey,
            ids.referentialPartitionKey,
            ids.referentialId,
            a.Id AS aliasid,
            a.DocumentId AS referenceddocumentid,
            a.DocumentPartitionKey AS referenceddocumentpartitionkey
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
    FROM staged;

    SELECT
        COALESCE(array_agg(DISTINCT referentialid), ARRAY[]::uuid[])
    INTO invalid_ids
    FROM temp_reference_stage
    WHERE parentdocumentid = p_parentDocumentId
      AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
      AND aliasid IS NULL;

    IF cardinality(invalid_ids) = 0 THEN
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
        );

        reference_partition := format('reference_%s', lpad(p_parentDocumentPartitionKey::text, 2, '0'));

        -- Remove references tied to the parent document that were not included in this upsert request,
        -- targeting only the specific partition that stores the parent's rows to avoid cross-partition scans.
        EXECUTE format(
            $sql$
            DELETE FROM %I.%I AS r
            WHERE r.ParentDocumentId = $1
              AND NOT EXISTS (
                  SELECT 1
                  FROM temp_reference_stage s
                  WHERE s.aliasid = r.aliasid
                    AND s.referentialpartitionkey = r.referentialpartitionkey
                    AND s.parentdocumentpartitionkey = $2
                    AND r.parentdocumentpartitionKey = $2
              )
            $sql$,
            'dms',
            reference_partition
        )
        USING p_parentDocumentId, p_parentDocumentPartitionKey;
    END IF;

    -- Clear the staged rows for the current document so the session can be reused safely.
    DELETE FROM temp_reference_stage
    WHERE parentdocumentid = p_parentDocumentId
      AND parentdocumentpartitionkey = p_parentDocumentPartitionKey;

    RETURN QUERY
    SELECT
        cardinality(invalid_ids) = 0,
        invalid_ids;
END;
$$;
