\set ON_ERROR_STOP on
\echo >>> Test 1: successful insert with valid referentials
BEGIN;
INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (15, uuid_generate_v4(), 'ParentResource', '1', false, 'Integration', '{}'::jsonb, 'test-1')
RETURNING id AS parent_id;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (3, uuid_generate_v4(), 'ReferencedResource', '1', false, 'Integration', '{}'::jsonb, 'test-1')
RETURNING id AS ref_id;\gset

INSERT INTO dms.Alias (referentialpartitionkey, referentialid, documentid, documentpartitionkey)
VALUES (3, uuid_generate_v4(), :ref_id, 3)
RETURNING referentialid AS ref_uuid;\gset

SELECT dms.InsertReferences((:parent_id)::bigint, 15::smallint, ARRAY[:'ref_uuid']::uuid[], ARRAY[3]::smallint[]) AS insert_result;

SELECT parentdocumentid, parentdocumentpartitionkey, aliasid, referencedDocumentId, referencedDocumentPartitionKey
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 15;
ROLLBACK;
