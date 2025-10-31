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

    RETURN QUERY
    WITH invalid AS (
        SELECT referentialid
        FROM temp_reference_stage
        WHERE aliasid IS NULL
          AND parentdocumentid = p_parentDocumentId
          AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
    ), cleanup AS (
        DELETE FROM temp_reference_stage
        WHERE parentdocumentid = p_parentDocumentId
          AND parentdocumentpartitionkey = p_parentDocumentPartitionKey
    )
    SELECT referentialid
    FROM invalid;
END;
$$;
