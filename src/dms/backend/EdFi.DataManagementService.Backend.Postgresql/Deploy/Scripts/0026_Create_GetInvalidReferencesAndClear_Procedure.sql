-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR REPLACE FUNCTION dms.GetInvalidReferencesAndClear(
    p_parentDocumentId BIGINT,
    p_parentDocumentPartitionKey SMALLINT
) RETURNS TABLE (referentialid UUID)
LANGUAGE plpgsql
AS
$$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_class
        WHERE relnamespace = pg_my_temp_schema()
          AND relname = 'temp_reference_stage'
    ) THEN
        RAISE EXCEPTION 'temp_reference_stage is not initialized for this session';
    END IF;

    RETURN QUERY
    WITH invalid AS (
        SELECT s.referentialid
        FROM pg_temp.temp_reference_stage s
        WHERE s.aliasid IS NULL
          AND s.parentdocumentid = p_parentDocumentId
          AND s.parentdocumentpartitionkey = p_parentDocumentPartitionKey
    ), cleanup AS (
        DELETE FROM pg_temp.temp_reference_stage
        WHERE parentdocumentid = p_parentDocumentId
          AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
    )
    SELECT invalid.referentialid
    FROM invalid;

    PERFORM 1
    FROM pg_temp.temp_reference_stage
    WHERE parentdocumentid = p_parentDocumentId
      AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
    LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION 'temp_reference_stage still holds rows for %, %', p_parentDocumentId, p_parentDocumentPartitionKey;
    END IF;
END;
$$;
