\set ON_ERROR_STOP on
\echo >>> Test 3: duplicate referentials in one call deduplicate correctly
BEGIN;
INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (10, uuid_generate_v4(), 'ParentResource', '1', false, 'Integration', '{}'::jsonb, 'test-3')
RETURNING id AS parent_id;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (4, uuid_generate_v4(), 'ReferencedResource', '1', false, 'Integration', '{}'::jsonb, 'test-3')
RETURNING id AS ref_id;\gset

INSERT INTO dms.Alias (referentialpartitionkey, referentialid, documentid, documentpartitionkey)
VALUES (4, uuid_generate_v4(), :ref_id, 4)
RETURNING referentialid AS ref_uuid;\gset

SELECT dms.InsertReferences((:parent_id)::bigint, 10::smallint, ARRAY[:'ref_uuid', :'ref_uuid']::uuid[], ARRAY[4,4]::smallint[]) AS result;

SELECT COUNT(*) AS reference_count
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 10;
ROLLBACK;
