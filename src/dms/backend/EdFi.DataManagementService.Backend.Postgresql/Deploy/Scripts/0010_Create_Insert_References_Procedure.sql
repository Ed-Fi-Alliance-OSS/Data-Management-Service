-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Upserts document references for a parent document.
-- The provided referentialIds MUST be unique
-- Returns a success flag plus the invalid referentialIds if unsuccessful.
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    p_parentDocumentId BIGINT,
    p_parentDocumentPartitionKey SMALLINT,
    p_referentialIds UUID[],
    p_referentialPartitionKeys SMALLINT[],
    p_isPureInsert BOOLEAN DEFAULT FALSE
) RETURNS TABLE (
    success BOOLEAN,
    invalid_ids UUID[]
)
LANGUAGE plpgsql
AS
$$
DECLARE
    reference_partition TEXT;
    current_session_id INTEGER := pg_backend_pid();
    needs_upsert BOOLEAN := TRUE;
BEGIN
    -- Reuse the unlogged staging table across calls, discard any leftovers from prior aborted executions.
    DELETE FROM dms.ReferenceStage
    WHERE SessionId = current_session_id;

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
    INSERT INTO dms.ReferenceStage (
        SessionId,
        parentdocumentid,
        parentdocumentpartitionkey,
        referentialpartitionkey,
        referentialid,
        aliasid,
        referenceddocumentid,
        referenceddocumentpartitionkey
    )
    SELECT
        current_session_id,
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
    FROM dms.ReferenceStage
    WHERE SessionId = current_session_id
      AND parentdocumentid = p_parentDocumentId
      AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
      AND aliasid IS NULL;

    IF cardinality(invalid_ids) = 0 THEN
        -- Optimization: Detect when ReferenceStage mirrors References, meaning no reference changes
        -- so we skip the write path entirely.

        IF NOT p_isPureInsert THEN
            -- Detects when a ReferenceStage row is missing in Reference table
            IF NOT EXISTS (
                   SELECT 1
                   FROM dms.ReferenceStage s
                   LEFT JOIN dms.Reference r
                     ON r.ParentDocumentPartitionKey = s.parentdocumentpartitionkey
                    AND r.ParentDocumentId = s.parentdocumentid
                    AND r.AliasId = s.aliasid
                   WHERE s.SessionId = current_session_id
                     AND s.aliasid IS NOT NULL
                     AND (
                         r.AliasId IS NULL
                      OR r.ReferentialPartitionKey IS DISTINCT FROM s.referentialpartitionkey
                      OR r.ReferencedDocumentId IS DISTINCT FROM s.referenceddocumentid
                      OR r.ReferencedDocumentPartitionKey IS DISTINCT FROM s.referenceddocumentpartitionkey
                   )
               )
               -- Detects when a Reference table row is missing in ReferenceStage
               AND NOT EXISTS (
                   SELECT 1
                   FROM dms.Reference r
                   WHERE r.ParentDocumentPartitionKey = p_parentDocumentPartitionKey
                     AND r.ParentDocumentId = p_parentDocumentId
                     AND NOT EXISTS (
                         SELECT 1
                         FROM dms.ReferenceStage s
                         WHERE s.SessionId = current_session_id
                           AND s.parentdocumentpartitionkey = r.ParentDocumentPartitionKey
                           AND s.parentdocumentid = r.ParentDocumentId
                           AND s.aliasid = r.AliasId
                           AND s.referentialpartitionkey = r.ReferentialPartitionKey
                           AND s.referenceddocumentid = r.ReferencedDocumentId
                           AND s.referenceddocumentpartitionkey = r.ReferencedDocumentPartitionKey
                     )
               )
            THEN
                needs_upsert := FALSE;
            END IF;
        END IF;

        IF needs_upsert THEN
            -- Perform the reference upsert
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
            FROM dms.ReferenceStage s
            WHERE s.SessionId = current_session_id
              AND s.aliasid IS NOT NULL
            -- Use the unique constraint to detect existing reference rows. Deduplicated staging
            -- rows ensure ON CONFLICT only fires once per target row within a single statement.
            ON CONFLICT ON CONSTRAINT ux_reference_parent_alias
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
            );

            -- If we know this is a pure insert, there is nothing to delete
            IF NOT p_isPureInsert THEN
                -- We already know the partition table we want
                reference_partition := format('reference_%s', lpad(p_parentDocumentPartitionKey::text, 2, '0'));

                -- Remove obsolete parent document references
                -- Targeting the specific partition table prevents this from being a cross-partition index scan
                EXECUTE format(
                    $sql$
                    DELETE FROM %I.%I AS r
                    WHERE r.ParentDocumentId = $1
                      AND NOT EXISTS (
                          SELECT 1
                          FROM dms.ReferenceStage s
                          WHERE s.SessionId = $3
                            AND s.aliasid = r.aliasid
                            AND s.referentialpartitionkey = r.referentialpartitionkey
                            AND s.parentdocumentpartitionkey = $2
                            AND r.parentdocumentpartitionKey = $2
                      )
                    $sql$,
                    'dms',
                    reference_partition
                )
                USING p_parentDocumentId, p_parentDocumentPartitionKey, current_session_id;
            END IF;
        END IF;
        END IF;
    END IF;

    -- Ensure the session-specific staging rows are cleared before returning.
    DELETE FROM dms.ReferenceStage
    WHERE SessionId = current_session_id;

    RETURN QUERY
    SELECT
        cardinality(invalid_ids) = 0,
        invalid_ids;
END;
$$;
