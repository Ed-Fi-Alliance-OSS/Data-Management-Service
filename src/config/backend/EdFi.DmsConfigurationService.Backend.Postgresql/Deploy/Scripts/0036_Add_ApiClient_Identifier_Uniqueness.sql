-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Makes the API client key (ClientId) and the identity-provider client identifier (ClientUuid)
-- unique across every application and tenant.
--
-- Both identifiers are global by construction: the identity provider already keeps the key unique
-- for the whole realm (UX_OpenIddictApplication_ClientId on the self-contained provider), and the
-- UUID names exactly one provider object. ApiClient has no tenant column, so neither constraint
-- carries one. Both columns are NOT NULL, so plain UNIQUE is sufficient.
--
-- Upgrade blocker. Earlier versions allowed duplicates, for example from externally seeded rows or
-- a repeated setup. If any exist, the preflight below stops the upgrade before either constraint is
-- added and lists a sample of the offending row ids. It never deletes, merges or chooses among
-- duplicates. The operator procedure in docs/OPERATIONS.md ("Configuration Service database
-- upgrades") enumerates every affected row and resolves ownership; DELETE /v3/apiClients/{id} must
-- not be used for these rows, because it also deletes the identity-provider client the rows share.
--
-- Duplicate detection uses the equality each constraint applies: plain column equality on ClientId
-- under the column's collation and on the UUID. The message lists at most 20 ids per column; the
-- counts are complete, the id lists are a sample.
--
-- The preflight and both constraint additions run in one DO block, which executes as a single
-- statement and therefore as one transaction: a failure of the second addition rolls back the
-- first. The ACCESS EXCLUSIVE lock taken first is held until the block ends, so no row can be
-- inserted between the preflight and the constraints. The existence guards make a retry converge
-- and leave a constraint that already exists untouched.
DO $$
DECLARE
    id_limit CONSTANT INT := 20;
    remediation CONSTANT TEXT :=
        'Resolve the duplicates with the operator procedure in docs/OPERATIONS.md ("Configuration Service database upgrades"), then retry the upgrade. Do not use DELETE /v3/apiClients/{id} for these rows.';
    client_id_total INT;
    client_uuid_total INT;
    client_id_ids TEXT;
    client_uuid_ids TEXT;
    message_text TEXT := 'API client identifier uniqueness upgrade blocked: ';
BEGIN
    LOCK TABLE "dmscs"."ApiClient" IN ACCESS EXCLUSIVE MODE;

    SELECT count(*), string_agg("Id"::TEXT, ', ' ORDER BY "Id") FILTER (WHERE row_position <= id_limit)
    INTO client_id_total, client_id_ids
    FROM (
        SELECT candidate."Id", row_number() OVER (ORDER BY candidate."Id") AS row_position
        FROM "dmscs"."ApiClient" candidate
        WHERE EXISTS (
            SELECT 1
            FROM "dmscs"."ApiClient" other
            WHERE other."ClientId" = candidate."ClientId"
              AND other."Id" <> candidate."Id"
        )
    ) duplicate_rows;

    SELECT count(*), string_agg("Id"::TEXT, ', ' ORDER BY "Id") FILTER (WHERE row_position <= id_limit)
    INTO client_uuid_total, client_uuid_ids
    FROM (
        SELECT candidate."Id", row_number() OVER (ORDER BY candidate."Id") AS row_position
        FROM "dmscs"."ApiClient" candidate
        WHERE EXISTS (
            SELECT 1
            FROM "dmscs"."ApiClient" other
            WHERE other."ClientUuid" = candidate."ClientUuid"
              AND other."Id" <> candidate."Id"
        )
    ) duplicate_rows;

    IF client_id_total > 0 OR client_uuid_total > 0 THEN
        IF client_id_total > 0 THEN
            message_text := message_text
                || format('%s ApiClient row(s) share a ClientId with another row, ids: %s', client_id_total, client_id_ids)
                || (CASE WHEN client_id_total > id_limit THEN format(', ... and %s more. ', client_id_total - id_limit) ELSE '. ' END);
        END IF;

        IF client_uuid_total > 0 THEN
            message_text := message_text
                || format('%s ApiClient row(s) share a ClientUuid with another row, ids: %s', client_uuid_total, client_uuid_ids)
                || (CASE WHEN client_uuid_total > id_limit THEN format(', ... and %s more. ', client_uuid_total - id_limit) ELSE '. ' END);
        END IF;

        RAISE EXCEPTION '%', message_text || remediation;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_ApiClient_ClientId'
          AND conrelid = '"dmscs"."ApiClient"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClient" ADD CONSTRAINT "UX_ApiClient_ClientId" UNIQUE ("ClientId");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_ApiClient_ClientUuid'
          AND conrelid = '"dmscs"."ApiClient"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClient" ADD CONSTRAINT "UX_ApiClient_ClientUuid" UNIQUE ("ClientUuid");
    END IF;
END$$;

COMMENT ON CONSTRAINT "UX_ApiClient_ClientId" ON "dmscs"."ApiClient" IS
    'API client keys are unique across every application and tenant';

COMMENT ON CONSTRAINT "UX_ApiClient_ClientUuid" ON "dmscs"."ApiClient" IS
    'Identity-provider client identifiers are unique across every application and tenant';
