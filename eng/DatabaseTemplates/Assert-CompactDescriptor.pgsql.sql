-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Render with Get-CompactDescriptorAssertionSql. The database supplies observations only.
DO $compact_descriptor$
DECLARE
    expected jsonb := '__EXPECTED_INVENTORY__'::jsonb;
    item jsonb;
    definition jsonb;
    relation oid;
    target oid;
    constraint_row pg_constraint;
    index_row pg_index;
    actual_columns jsonb;
    descriptor_sequence regclass;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dms."EffectiveSchema" WHERE "EffectiveSchemaHash" = expected->>'effective_schema_hash') THEN
        RAISE EXCEPTION 'Compact descriptor baseline: EffectiveSchemaHash/schema set mismatch';
    END IF;
    FOR item IN SELECT value FROM jsonb_array_elements(expected->'projects') LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = item->>'physical_schema')
           OR NOT EXISTS (SELECT 1 FROM dms."SchemaComponent" WHERE "EffectiveSchemaHash" = expected->>'effective_schema_hash'
               AND "ProjectEndpointName" = item->>'project_endpoint_name' AND "ProjectName" = item->>'project_name'
               AND "ProjectVersion" = item->>'project_version' AND "IsExtensionProject" = (item->>'is_extension')::boolean) THEN
            RAISE EXCEPTION 'Compact descriptor baseline: project/schema set mismatch %', item;
        END IF;
    END LOOP;
    IF (SELECT count(*) FROM dms."SchemaComponent" WHERE "EffectiveSchemaHash" = expected->>'effective_schema_hash') <> jsonb_array_length(expected->'projects') THEN
        RAISE EXCEPTION 'Compact descriptor baseline: manifest project/schema set mismatch';
    END IF;
    IF (SELECT count(*) FROM dms."ResourceKey") <> jsonb_array_length(expected->'resources')
       OR EXISTS (
           SELECT 1 FROM jsonb_array_elements(expected->'resources') r
           WHERE NOT EXISTS (SELECT 1 FROM dms."ResourceKey" k WHERE k."ProjectName" = r->>'project_name' AND k."ResourceName" = r->>'resource_name')
       ) THEN
        RAISE EXCEPTION 'Compact descriptor baseline: manifest resource/schema set mismatch';
    END IF;
    FOR item IN SELECT value FROM jsonb_array_elements((expected->'columns') || (expected->'required_columns')) LOOP
        relation := to_regclass(format('%I.%I', item->>'schema', item->>'table'));
        IF NOT EXISTS (
            SELECT 1 FROM pg_attribute
            WHERE attrelid = relation AND attname = item->>'name' AND NOT attisdropped
              AND atttypid = CASE item->>'scalar_type' WHEN 'Int32' THEN 'int4'::regtype WHEN 'Int64' THEN 'int8'::regtype WHEN 'Int16' THEN 'int2'::regtype END
              AND attnotnull = NOT (item->>'is_nullable')::boolean AND attgenerated = ''
        ) THEN
            RAISE EXCEPTION 'Compact descriptor baseline: missing/noncompact stored column %', item;
        END IF;
    END LOOP;

    FOR item IN SELECT value FROM jsonb_array_elements(expected->'constraints') LOOP
        definition := item->'definition';
        relation := to_regclass(format('%I.%I', item->>'schema', item->>'table'));
        SELECT * INTO constraint_row FROM pg_constraint WHERE conrelid = relation AND conname = definition->>'name';
        IF NOT FOUND OR NOT constraint_row.convalidated THEN
            RAISE EXCEPTION 'Compact descriptor baseline: missing/unvalidated constraint %', item;
        END IF;
        SELECT jsonb_agg(a.attname ORDER BY k.ordinality) INTO actual_columns
        FROM unnest(constraint_row.conkey) WITH ORDINALITY k(attnum, ordinality)
        JOIN pg_attribute a ON a.attrelid = relation AND a.attnum = k.attnum;
        IF actual_columns IS DISTINCT FROM definition->'columns' THEN
            RAISE EXCEPTION 'Compact descriptor baseline: constraint columns mismatch %', item;
        END IF;
        IF definition->>'kind' = 'ForeignKey' THEN
            target := to_regclass(format('%I.%I', definition->'target_table'->>'schema', definition->'target_table'->>'name'));
            SELECT jsonb_agg(a.attname ORDER BY k.ordinality) INTO actual_columns
            FROM unnest(constraint_row.confkey) WITH ORDINALITY k(attnum, ordinality)
            JOIN pg_attribute a ON a.attrelid = target AND a.attnum = k.attnum;
            IF constraint_row.contype <> 'f' OR constraint_row.confrelid IS DISTINCT FROM target
               OR actual_columns IS DISTINCT FROM definition->'target_columns'
               OR constraint_row.confdeltype <> (CASE definition->>'on_delete' WHEN 'Cascade' THEN 'c' WHEN 'NoAction' THEN 'a' END)
               OR constraint_row.confupdtype <> (CASE definition->>'on_update' WHEN 'Cascade' THEN 'c' WHEN 'NoAction' THEN 'a' END) THEN
                RAISE EXCEPTION 'Compact descriptor baseline: FK target/actions mismatch %', item;
            END IF;
        ELSIF constraint_row.contype <> 'u' THEN
            RAISE EXCEPTION 'Compact descriptor baseline: expected unique constraint %', item;
        END IF;
    END LOOP;

    FOR item IN SELECT value FROM jsonb_array_elements(expected->'indexes') LOOP
        relation := to_regclass(format('%I.%I', item->'table'->>'schema', item->'table'->>'name'));
        SELECT i.* INTO index_row FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
        WHERE i.indrelid = relation AND c.relname = item->>'name';
        IF NOT FOUND OR NOT index_row.indisvalid OR index_row.indpred IS NOT NULL
           OR index_row.indisunique <> (item->>'is_unique')::boolean
           OR index_row.indisprimary <> (item->>'kind' = 'PrimaryKey') THEN
            RAISE EXCEPTION 'Compact descriptor baseline: missing/incorrect index %', item;
        END IF;
        SELECT jsonb_agg(a.attname ORDER BY k.ordinality) INTO actual_columns
        FROM unnest(index_row.indkey) WITH ORDINALITY k(attnum, ordinality)
        JOIN pg_attribute a ON a.attrelid = relation AND a.attnum = k.attnum
        WHERE k.ordinality <= index_row.indnkeyatts;
        IF actual_columns IS DISTINCT FROM item->'key_columns' THEN
            RAISE EXCEPTION 'Compact descriptor baseline: index columns mismatch %', item;
        END IF;
    END LOOP;

    relation := 'dms."Descriptor"'::regclass;
    descriptor_sequence := pg_get_serial_sequence('dms."Descriptor"', 'DescriptorId')::regclass;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = relation AND attname = 'DescriptorId' AND attidentity = 'a')
       OR descriptor_sequence IS NULL OR descriptor_sequence = pg_get_serial_sequence('dms."Document"', 'DocumentId')::regclass
       OR NOT EXISTS (SELECT 1 FROM pg_sequence WHERE seqrelid = descriptor_sequence AND seqtypid = 'int4'::regtype AND seqincrement = 1) THEN
        RAISE EXCEPTION 'Compact descriptor baseline: DescriptorId needs its independent int GENERATED ALWAYS identity';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid IN (relation, 'tracked_changes_edfi."Descriptor"'::regclass)
        AND NOT attisdropped AND attname IN ('Discriminator', 'UriLowered', 'NamespaceLowered', 'CodeValueLowered'))
       OR EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = relation AND NOT attisdropped AND attname = 'Uri')
       OR EXISTS (SELECT 1 FROM pg_class WHERE relnamespace = 'dms'::regnamespace
        AND relname IN ('UX_Descriptor_Uri_Discriminator', 'IX_Descriptor_Discriminator_ContentVersion')) THEN
        RAISE EXCEPTION 'Compact descriptor baseline: obsolete descriptor storage/index remains';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
        JOIN pg_attribute n ON n.attrelid = relation AND n.attname = 'Namespace'
        JOIN pg_attribute v ON v.attrelid = relation AND v.attname = 'CodeValue'
        JOIN pg_attribute r ON r.attrelid = relation AND r.attname = 'ResourceKeyId'
        WHERE i.indrelid = relation AND c.relname = 'UX_Descriptor_ResourceKeyId_Uri'
          AND i.indisunique AND i.indisvalid AND i.indpred IS NULL AND i.indnkeyatts = 2
          AND i.indkey[0] = r.attnum AND i.indkey[1] = 0
          AND i.indcollation[1] = n.attcollation AND n.attcollation = v.attcollation
          AND n.attcollation = (SELECT typcollation FROM pg_type WHERE oid = 'varchar'::regtype)
          AND regexp_replace(pg_get_expr(i.indexprs, i.indrelid), '\s+|::text|[()]', '', 'g') = '"Namespace"||''#''||"CodeValue"'
    ) THEN
        RAISE EXCEPTION 'Compact descriptor baseline: required unlowered whole-URI expression index/collation missing';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_index i JOIN pg_attribute a ON a.attrelid = relation AND a.attnum = ANY(i.indkey)
        WHERE i.indrelid = relation AND i.indisunique AND a.attname IN ('Namespace', 'CodeValue')
    ) THEN
        RAISE EXCEPTION 'Compact descriptor baseline: component uniqueness is forbidden';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'tracked_changes_edfi."Descriptor"'::regclass
        AND contype = 'f' AND confrelid IN ('dms."Descriptor"'::regclass, 'dms."Document"'::regclass)) THEN
        RAISE EXCEPTION 'Compact descriptor baseline: history must survive live descriptor/document deletion';
    END IF;
END $compact_descriptor$;
