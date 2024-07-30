-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR REPLACE PROCEDURE UpsertDocument(
    _NewDocumentUuid,
    _NewDocumentPartitionKey,
    _ResourceName,
    _ResourceVersion,
    _ProjectName,
    _EdfiDoc,
    _ReferentialPartitionKey,
    _ReferentialId,
    _SuperclassReferentialPartitionKey,
    _SuperclassReferentialId
)
LANGUAGE plpgsql AS
$proc$
DECLARE
    found_document dms.Document%ROWTYPE
    new_document_id BIGINT
BEGIN
    -- Try to find the document from the given ReferentialId
    SELECT * INTO found_document
    FROM dms.Document d
    INNER JOIN dms.Alias a ON a.DocumentId = d.Id AND a.DocumentPartitionKey = d.DocumentPartitionKey
    WHERE a.ReferentialPartitionKey = _ReferentialPartitionKey AND a.ReferentialId = _ReferentialId FOR SHARE;

    -- If not found, this is an insert
    IF NOT FOUND THEN
        -- Insert new document
        INSERT INTO dms.Document (
            DocumentPartitionKey,
            DocumentUuid,
            ResourceName,
            ResourceVersion,
            ProjectName,
            EdfiDoc
        )
        VALUES (
            _DocumentPartitionKey,
            _DocumentUuid,
            _ResourceName,
            _ResourceVersion,
            _ProjectName,
            _EdfiDoc
        )
        RETURNING Id INTO new_document_id;

        -- now insert aliases
        INSERT INTO dms.Alias (
            ReferentialPartitionKey,
            ReferentialId,
            DocumentId,
            DocumentPartitionKey
        )
        VALUES (
            _ReferentialPartitionKey,
            _ReferentialId,
            new_document_id,
            _NewDocumentPartitionKey
        )

        -- now insert superclass alias table if exists -- or can we do as an array?????

        -- TODO: Catch Alias UniqueViolation conflict - Alias ReferentialId already exists

        -- delete any old references (update not upsert)

        --


    END IF;
END
$proc$;

-- Insert references, returning any referentialIds that are invalid
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    parentDocumentIds BIGINT[],
    parentDocumentPartitionKeys SMALLINT[],
    referentialIds UUID[],
    referentialPartitionKeys SMALLINT[]
) RETURNS TABLE (referentialId UUID)
LANGUAGE plpgsql AS
$func$
DECLARE

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

EXCEPTION
    -- Check if there were bad referentialIds
    WHEN foreign_key_violation THEN
        SELECT r.ReferentialId
            FROM ROWS FROM
                (unnest(referentialIds::uuid[]), unnest(referentialPartitionKeys::integer[]))
                AS r (ReferentialId, ReferentialPartitionKey)
            WHERE NOT EXISTS (
                SELECT 1
                FROM dms.Alias a
                WHERE r.ReferentialId = a.ReferentialId
                AND r.ReferentialPartitionKey = a.ReferentialPartitionKey);
END
$func$;
