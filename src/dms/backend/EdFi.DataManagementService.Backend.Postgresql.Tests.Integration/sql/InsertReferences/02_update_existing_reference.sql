\set ON_ERROR_STOP on
\echo >>> Test 2: update existing reference when alias points to different document
BEGIN;
INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (11, uuid_generate_v4(), 'ParentResource', '1', false, 'Integration', '{}'::jsonb, 'test-2')
RETURNING id AS parent_id;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (5, uuid_generate_v4(), 'ReferencedResourceA', '1', false, 'Integration', '{}'::jsonb, 'test-2')
RETURNING id AS ref_a_id;\gset

INSERT INTO dms.Alias (referentialpartitionkey, referentialid, documentid, documentpartitionkey)
VALUES (5, uuid_generate_v4(), :ref_a_id, 5)
RETURNING id AS alias_id, referentialid AS ref_uuid;\gset

SELECT dms.InsertReferences((:parent_id)::bigint, 11::smallint, ARRAY[:'ref_uuid']::uuid[], ARRAY[5]::smallint[]) AS initial_result;

SELECT referencedDocumentId AS initial_doc_id
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 11;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (6, uuid_generate_v4(), 'ReferencedResourceB', '1', false, 'Integration', '{}'::jsonb, 'test-2')
RETURNING id AS ref_b_id;\gset

UPDATE dms.Alias
SET documentid = :ref_b_id, documentpartitionkey = 6
WHERE id = :alias_id;

SELECT dms.InsertReferences((:parent_id)::bigint, 11::smallint, ARRAY[:'ref_uuid']::uuid[], ARRAY[5]::smallint[]) AS updated_result;

SELECT referencedDocumentId, referencedDocumentPartitionKey
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 11;
ROLLBACK;
