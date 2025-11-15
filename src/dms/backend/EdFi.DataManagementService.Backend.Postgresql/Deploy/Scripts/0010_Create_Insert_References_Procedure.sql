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
    needs_upsert BOOLEAN := TRUE;
    stage_has_difference BOOLEAN := FALSE;
    reference_has_orphans BOOLEAN := FALSE;
BEGIN
    reference_partition := format('reference_%s', lpad(p_parentDocumentPartitionKey::text, 2, '0'));

    -- Ensure each session has a private staging table, create it on first use.
    IF to_regclass('pg_temp.reference_stage') IS NULL THEN
        EXECUTE '
            CREATE TEMP TABLE reference_stage (
                parentdocumentid BIGINT NOT NULL,
                parentdocumentpartitionkey SMALLINT NOT NULL,
                referentialpartitionkey SMALLINT NOT NULL,
                referentialid UUID NOT NULL,
                aliasid BIGINT,
                referenceddocumentid BIGINT,
                referenceddocumentpartitionkey SMALLINT,
                PRIMARY KEY (referentialpartitionkey, referentialid)
            )
            ON COMMIT DELETE ROWS
        ';

        EXECUTE '
            CREATE INDEX reference_stage_alias_idx
                ON reference_stage (aliasid)
        ';

        EXECUTE '
            CREATE INDEX reference_stage_parent_idx
                ON reference_stage (parentdocumentpartitionkey, parentdocumentid)
        ';
    END IF;

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
    INSERT INTO reference_stage (
        parentdocumentid,
        parentdocumentpartitionkey,
        referentialpartitionkey,
        referentialid,
        aliasid,
        referenceddocumentid,
        referenceddocumentpartitionkey
    )
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
    FROM reference_stage
    WHERE parentdocumentid = p_parentDocumentId
      AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
      AND aliasid IS NULL;

    IF cardinality(invalid_ids) = 0 THEN
        -- Optimization: Detect when ReferenceStage mirrors References, meaning no reference changes
        -- so we skip the write path entirely.

        IF NOT p_isPureInsert THEN
            stage_has_difference := FALSE;
            reference_has_orphans := FALSE;

            EXECUTE format(
                $sql$
                SELECT EXISTS (
                    SELECT 1
                    FROM reference_stage s
                    LEFT JOIN dms.%I r
                      ON r.ParentDocumentPartitionKey = s.parentdocumentpartitionkey
                     AND r.ParentDocumentId = s.parentdocumentid
                     AND r.AliasId = s.aliasid
                    WHERE s.aliasid IS NOT NULL
                      AND (
                          r.AliasId IS NULL
                       OR r.ReferentialPartitionKey IS DISTINCT FROM s.referentialpartitionkey
                       OR r.ReferencedDocumentId IS DISTINCT FROM s.referenceddocumentid
                       OR r.ReferencedDocumentPartitionKey IS DISTINCT FROM s.referenceddocumentpartitionkey
                    )
                )
                $sql$,
                reference_partition
            )
            INTO stage_has_difference;

            IF NOT stage_has_difference THEN
                EXECUTE format(
                    $sql$
                    SELECT EXISTS (
                        SELECT 1
                        FROM dms.%I r
                        WHERE r.ParentDocumentPartitionKey = $2
                          AND r.ParentDocumentId = $1
                          AND NOT EXISTS (
                              SELECT 1
                              FROM reference_stage s
                              WHERE s.parentdocumentpartitionkey = r.ParentDocumentPartitionKey
                                AND s.parentdocumentid = r.ParentDocumentId
                                AND s.aliasid = r.AliasId
                                AND s.referentialpartitionkey = r.ReferentialPartitionKey
                                AND s.referenceddocumentid = r.ReferencedDocumentId
                                AND s.referenceddocumentpartitionkey = r.ReferencedDocumentPartitionKey
                          )
                    )
                    $sql$,
                    reference_partition
                )
                INTO reference_has_orphans
                USING p_parentDocumentId, p_parentDocumentPartitionKey;

                IF NOT reference_has_orphans THEN
                    needs_upsert := FALSE;
                END IF;
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
            FROM reference_stage s
            WHERE s.aliasid IS NOT NULL
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
                -- Remove obsolete parent document references
                -- Targeting the specific partition table prevents this from being a cross-partition index scan
                EXECUTE format(
                    $sql$
                    DELETE FROM %I.%I AS r
                    WHERE r.ParentDocumentPartitionKey = $2
                      AND r.ParentDocumentId = $1
                      AND NOT EXISTS (
                          SELECT 1
                          FROM reference_stage s
                          WHERE s.parentdocumentpartitionkey = $2
                            AND s.parentdocumentid = $1
                            AND s.aliasid = r.aliasid
                            AND s.referentialpartitionkey = r.referentialpartitionkey
                            AND s.referenceddocumentid = r.referenceddocumentid
                            AND s.referenceddocumentpartitionkey = r.ReferencedDocumentPartitionKey
                      )
                    $sql$,
                    'dms',
                    reference_partition
                )
                USING p_parentDocumentId, p_parentDocumentPartitionKey;
            END IF;
        END IF;
    END IF;

    RETURN QUERY
    SELECT
        cardinality(invalid_ids) = 0,
        invalid_ids;
END;
$$;
