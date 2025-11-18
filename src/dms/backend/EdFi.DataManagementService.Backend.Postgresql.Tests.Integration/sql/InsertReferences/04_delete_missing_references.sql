\set ON_ERROR_STOP on
\echo >>> Test 4: references removed when omitted from subsequent call
BEGIN;
INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (13, uuid_generate_v4(), 'ParentResource', '1', false, 'Integration', '{}'::jsonb, 'test-4')
RETURNING id AS parent_id;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (7, uuid_generate_v4(), 'ReferencedResourceA', '1', false, 'Integration', '{}'::jsonb, 'test-4')
RETURNING id AS ref_a_id;\gset

INSERT INTO dms.Alias (referentialpartitionkey, referentialid, documentid, documentpartitionkey)
VALUES (7, uuid_generate_v4(), :ref_a_id, 7)
RETURNING id AS alias_a_id, referentialid AS ref_a_uuid;\gset

INSERT INTO dms.Document (documentpartitionkey, documentuuid, resourcename, resourceversion, isdescriptor, projectname, edfidoc, lastmodifiedtraceid)
VALUES (8, uuid_generate_v4(), 'ReferencedResourceB', '1', false, 'Integration', '{}'::jsonb, 'test-4')
RETURNING id AS ref_b_id;\gset

INSERT INTO dms.Alias (referentialpartitionkey, referentialid, documentid, documentpartitionkey)
VALUES (8, uuid_generate_v4(), :ref_b_id, 8)
RETURNING id AS alias_b_id, referentialid AS ref_b_uuid;\gset

SELECT success, invalid_ids
FROM dms.InsertReferences(
    (:parent_id)::bigint,
    13::smallint,
    ARRAY[:'ref_a_uuid', :'ref_b_uuid']::uuid[],
    ARRAY[7, 8]::smallint[]
);

SELECT COUNT(*) AS initial_ref_count
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 13;\gset

SELECT success, invalid_ids
FROM dms.InsertReferences(
    (:parent_id)::bigint,
    13::smallint,
    ARRAY[:'ref_a_uuid']::uuid[],
    ARRAY[7]::smallint[]
);

SELECT aliasid, referencedDocumentPartitionKey
FROM dms.Reference
WHERE parentdocumentid = (:parent_id)::bigint AND parentdocumentpartitionkey = 13;
ROLLBACK;
