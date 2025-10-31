\set ON_ERROR_STOP on
\echo >>> Test 4: mix of resolved and unresolved referentials returns false and records invalid entries
BEGIN;
INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (9, uuid_generate_v4(), 'ParentResource', '1', false, 'Integration', '{}'::jsonb, 'test-4')
RETURNING id AS parent_id;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (2, uuid_generate_v4(), 'ReferencedResource', '1', false, 'Integration', '{}'::jsonb, 'test-4')
RETURNING id AS ref_id;\gset

INSERT INTO dms.Alias (referentialpartitionkey, referentialid, documentid, documentpartitionkey)
VALUES (2, uuid_generate_v4(), :ref_id, 2)
RETURNING referentialid AS resolved_uuid;\gset

SELECT uuid_generate_v4() AS missing_uuid;\gset

SELECT dms.InsertReferences((:parent_id)::bigint, 9::smallint, ARRAY[:'resolved_uuid', :'missing_uuid']::uuid[], ARRAY[2,2]::smallint[]) AS result;

SELECT parentdocumentid, aliasid, referencedDocumentId
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 9;

SELECT referentialid, aliasid
FROM temp_reference_stage
WHERE aliasid IS NULL;
ROLLBACK;
