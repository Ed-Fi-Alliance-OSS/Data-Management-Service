-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Applies a JSON Patch (RFC 6902-style) to a JSONB target.
-- Supports 'add', 'remove', and 'replace' operations.

CREATE OR REPLACE FUNCTION dms.jsonb_patch(
    target JSONB,
    patch  JSONB
)
RETURNS JSONB
LANGUAGE plpgsql
AS $$
DECLARE
    patch_element  JSONB;
    op             TEXT;
    path_text      TEXT;
    path_parts     TEXT[];
    parent_path    TEXT[];
    parent_element JSONB;
    value          JSONB;
    last_part      TEXT;
    append_index   INT;
BEGIN
    IF jsonb_typeof(patch) != 'array' THEN
        RAISE EXCEPTION 'Patch must be a JSON array';
    END IF;

    FOR patch_element IN SELECT * FROM jsonb_array_elements(patch)
    LOOP
        op := patch_element->>'op';
        path_text := patch_element->>'path';

        -- Convert RFC 6901 JSON Pointer string into text[] path:
        --   ""           -> {}              (root)
        --   "/a/b"       -> {"a","b"}
        --   "/tags/1"    -> {"tags","1"}
        --   "/a~1b"      -> {"a/b"}
        --   "/a~0b"      -> {"a~b"}
        IF path_text IS NULL THEN
            RAISE EXCEPTION 'Patch operation is missing path';
        END IF;

        IF path_text = '' THEN
            -- Root path (special case).
            path_parts := ARRAY[]::TEXT[];
        ELSE
            -- Drop leading '/' and split on remaining '/'; decode ~0 and ~1 sequences.
            SELECT array_agg(
                       replace(
                         replace(part, '~1', '/'),
                         '~0', '~'
                       )
                   )
            INTO path_parts
            FROM regexp_split_to_table(ltrim(path_text, '/'), '/') AS part;
        END IF;

        IF patch_element ? 'value' THEN
            value := patch_element->'value';
        END IF;

        CASE op
            WHEN 'replace' THEN
                IF array_length(path_parts, 1) IS NULL OR array_length(path_parts, 1) = 0 THEN
                    -- Replace whole document
                    target := value;
                ELSE
                    target := jsonb_set(target, path_parts, value, TRUE);
                END IF;

            WHEN 'remove' THEN
                IF array_length(path_parts, 1) IS NULL OR array_length(path_parts, 1) = 0 THEN
                    -- Remove whole document; set to null JSON.
                    target := 'null'::jsonb;
                ELSE
                    target := target #- path_parts;
                END IF;

            WHEN 'add' THEN
                -- If path is root (""), treat as full replacement.
                IF array_length(path_parts, 1) IS NULL OR array_length(path_parts, 1) = 0 THEN
                    target := value;
                ELSE
                    parent_path := path_parts[1:array_length(path_parts, 1) - 1];

                    IF array_length(parent_path, 1) = 0 THEN
                        parent_element := target; -- Parent is the root
                    ELSE
                        parent_element := target #> parent_path;
                    END IF;

                    last_part := path_parts[array_length(path_parts, 1)];

                    -- If parent is an array, we MUST use jsonb_insert
                    IF jsonb_typeof(parent_element) = 'array' THEN
                        -- Check for the special 'append' syntax (e.g., /tags/-)
                        IF last_part = '-' THEN
                            append_index := jsonb_array_length(parent_element);
                            path_parts[array_length(path_parts, 1)] := append_index::text;
                        END IF;

                        target := jsonb_insert(target, path_parts, value);
                    ELSE
                        -- Parent is an object. For objects, 'add' acts like 'replace'.
                        target := jsonb_set(target, path_parts, value, TRUE);
                    END IF;
                END IF;

            ELSE
                RAISE EXCEPTION 'Unsupported patch operation: %', op;
        END CASE;
    END LOOP;

    RETURN target;
END;
$$;

