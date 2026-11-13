-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1439 step 1.3c. Deploy with all catalog writers and old CMS binaries drained.
-- Revalidate 0039 independently: it may already be journaled and will not run again.
BEGIN;
LOCK TABLE "dmscs"."DataStore" IN ACCESS EXCLUSIVE MODE;
DO $$
DECLARE
    constraint_definition text;
    collision_count bigint;
    collision_ids text;
    key_expression constant text := $expression$
        translate(
            btrim(
                "Name" COLLATE "C",
                chr(9) || chr(10) || chr(11) || chr(12) || chr(13) || chr(32)
                || chr(133) || chr(160) || chr(5760)
                || chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196)
                || chr(8197) || chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202)
                || chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288)
            ),
            'ABCDEFGHIJKLMNOPQRSTUVWXYZ',
            'abcdefghijklmnopqrstuvwxyz'
        )
    $expression$;
BEGIN
    IF current_setting('server_encoding') <> 'UTF8' THEN
        RAISE EXCEPTION 'DataStore name key upgrade requires UTF8 server encoding.';
    END IF;

    -- Bind built-ins and deparse both expressions under the same catalog-only path.
    SET LOCAL search_path TO pg_catalog;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute a
        JOIN pg_collation c ON c.oid = a.attcollation
        WHERE a.attrelid = '"dmscs"."DataStore"'::regclass
          AND a.attname = 'NameComparisonKey'
          AND a.attgenerated = 's'
          AND a.atttypid = 'text'::regtype
          AND c.collname = 'C' AND c.collnamespace = 'pg_catalog'::regnamespace
          AND c.collisdeterministic
    ) THEN
        RAISE EXCEPTION 'Existing DataStore NameComparisonKey must be stored generated text with deterministic C collation.';
    END IF;

    -- Let this server produce the expected catalog expression, including casts from
    -- the actual Name type (varchar in the approved schema). No stored rows are copied.
    -- Transaction rollback or commit removes the temporary definition; never replace
    -- an existing object. Equivalent custom expressions may conservatively be refused.
    CREATE TEMP TABLE dmscs_expected_name_key ON COMMIT DROP AS
        SELECT "Name" FROM "dmscs"."DataStore" WITH NO DATA;
    EXECUTE format(
        'ALTER TABLE pg_temp.dmscs_expected_name_key ADD COLUMN "NameComparisonKey" text COLLATE "C" GENERATED ALWAYS AS (%s) STORED',
        key_expression
    );

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute a
        JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        JOIN pg_attribute expected ON expected.attrelid = 'pg_temp.dmscs_expected_name_key'::regclass
            AND expected.attname = 'NameComparisonKey'
        JOIN pg_attrdef expected_definition ON expected_definition.adrelid = expected.attrelid
            AND expected_definition.adnum = expected.attnum
        WHERE a.attrelid = '"dmscs"."DataStore"'::regclass AND a.attname = 'NameComparisonKey'
          AND pg_get_expr(d.adbin, d.adrelid) COLLATE "C"
              = pg_get_expr(expected_definition.adbin, expected_definition.adrelid) COLLATE "C"
    ) THEN
        RAISE EXCEPTION 'Existing DataStore NameComparisonKey column definition does not match the approved migration definition.';
    END IF;

    -- Accept only the approved legacy constraint or an already committed 0040.
    -- A missing/custom constraint is not a request to repair catalog objects.
    SELECT pg_get_constraintdef(c.oid) INTO constraint_definition
    FROM pg_constraint c
    JOIN pg_index i ON i.indexrelid = c.conindid
    WHERE c.conrelid = '"dmscs"."DataStore"'::regclass
      AND c.conname = 'UX_DataStore_TenantId_Name'
      AND c.contype = 'u' AND c.convalidated AND NOT c.condeferrable
      AND i.indisunique AND i.indisvalid AND i.indisready AND i.indislive
      AND i.indnullsnotdistinct AND i.indnkeyatts = 2 AND i.indnatts = 2
      AND i.indpred IS NULL AND i.indexprs IS NULL;

    IF constraint_definition IS NULL OR constraint_definition NOT IN (
        'UNIQUE NULLS NOT DISTINCT ("TenantId", "Name")',
        'UNIQUE NULLS NOT DISTINCT ("TenantId", "NameComparisonKey")'
    ) THEN
        RAISE EXCEPTION 'Existing UX_DataStore_TenantId_Name must be the approved legacy or canonical uniqueness constraint.';
    END IF;

    SELECT count(*), string_agg("Id"::text, ', ' ORDER BY "Id") FILTER (WHERE row_position <= 20)
    INTO collision_count, collision_ids
    FROM (
        SELECT "Id", row_number() OVER (ORDER BY "Id") AS row_position
        FROM (
            SELECT "Id", count(*) OVER (PARTITION BY "TenantId", "NameComparisonKey") AS occurrences
            FROM "dmscs"."DataStore"
        ) candidates
        WHERE occurrences > 1
    ) collisions;

    IF collision_count > 0 THEN
        RAISE EXCEPTION '%', format(
            'Canonical DataStore uniqueness upgrade blocked: %s row(s), ids: %s%s Rename conflicting rows by ID with PUT /v3/dataStores/{id}, or explicitly remove them with DELETE, then retry the upgrade.',
            collision_count, collision_ids,
            CASE WHEN collision_count > 20 THEN format(', ... and %s more.', collision_count - 20) ELSE '.' END);
    END IF;

    IF constraint_definition = 'UNIQUE NULLS NOT DISTINCT ("TenantId", "Name")' THEN
        ALTER TABLE "dmscs"."DataStore" DROP CONSTRAINT "UX_DataStore_TenantId_Name";
        ALTER TABLE "dmscs"."DataStore" ADD CONSTRAINT "UX_DataStore_TenantId_Name"
            UNIQUE NULLS NOT DISTINCT ("TenantId", "NameComparisonKey");
    END IF;
END $$;
COMMIT;
