-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Rendered owners come only from the reviewed implementation manifest.
DO $authoritative_timestamp$
DECLARE
    owners jsonb := '__EXPECTED_OWNERS__'::jsonb;
    owner jsonb;
    relation oid;
BEGIN
    relation := to_regclass('dms."Document"');
    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = relation AND relkind IN ('r', 'p')) THEN
        RAISE EXCEPTION 'Timestamp baseline: missing table dms.Document';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = relation AND attname = 'ContentLastModifiedAt' AND NOT attisdropped) THEN
        RAISE EXCEPTION 'Timestamp baseline: forbidden column dms.Document.ContentLastModifiedAt';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = relation AND attname = 'ContentVersion'
        AND NOT attisdropped AND atttypid = 'int8'::regtype AND attnotnull AND attgenerated = '') THEN
        RAISE EXCEPTION 'Timestamp baseline: missing/invalid stored column dms.Document.ContentVersion';
    END IF;
    FOR owner IN SELECT value FROM jsonb_array_elements(owners) LOOP
        relation := to_regclass(format('%I.%I', owner->>'schema', owner->>'table'));
        IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = relation AND relkind IN ('r', 'p')) THEN
            RAISE EXCEPTION 'Timestamp baseline: missing owner table %.%', owner->>'schema', owner->>'table';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = relation AND attname = 'ContentLastModifiedAt'
            AND NOT attisdropped AND atttypid = 'timestamptz'::regtype AND attnotnull AND attgenerated = '') THEN
            RAISE EXCEPTION 'Timestamp baseline: missing/invalid authoritative column %.%.ContentLastModifiedAt', owner->>'schema', owner->>'table';
        END IF;
    END LOOP;
END
$authoritative_timestamp$;
