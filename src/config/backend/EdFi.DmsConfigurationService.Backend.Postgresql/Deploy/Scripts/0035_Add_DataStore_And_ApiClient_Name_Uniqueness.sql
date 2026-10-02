-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Makes data store names unique within a tenant and API client names unique within an application.
--
-- Natural keys: DataStore (TenantId, Name) and ApiClient (ApplicationId, Name). The same data store
-- name may exist in different tenants. NULLS NOT DISTINCT keeps single-tenant deployments
-- (TenantId IS NULL) rejecting duplicates too; PostgreSQL would otherwise treat every NULL tenant as
-- distinct. A NULL-tenant data store and a tenant's data store may share a name.
--
-- Upgrade blocker. Earlier versions allowed duplicates, for example from repeated setup-script runs.
-- If any exist, the preflight below stops the upgrade before either constraint is added and lists
-- the offending row ids. It never renames, deletes or chooses among duplicates. Rename a duplicate
-- with PUT /v3/dataStores/{id} or PUT /v3/apiClients/{id}, or remove it with DELETE, on the current
-- version, then retry the upgrade.

-- Duplicate detection uses the equality each constraint applies: plain column equality on Name and
-- ApplicationId, and NULL-equal tenants, matching NULLS NOT DISTINCT. The message lists at most 20
-- ids per table, so it stays the same bounded shape the SQL Server script can THROW.
DO $$
DECLARE
    id_limit CONSTANT INT := 20;
    remediation CONSTANT TEXT :=
        'Rename a duplicate with PUT /v3/dataStores/{id} or PUT /v3/apiClients/{id}, or remove it with DELETE, then retry the upgrade.';
    data_store_total INT;
    api_client_total INT;
    data_store_ids TEXT;
    api_client_ids TEXT;
    message_text TEXT := 'Name uniqueness upgrade blocked: ';
BEGIN
    SELECT count(*), string_agg("Id"::TEXT, ', ' ORDER BY "Id") FILTER (WHERE row_position <= id_limit)
    INTO data_store_total, data_store_ids
    FROM (
        SELECT candidate."Id", row_number() OVER (ORDER BY candidate."Id") AS row_position
        FROM "dmscs"."DataStore" candidate
        WHERE EXISTS (
            SELECT 1
            FROM "dmscs"."DataStore" other
            WHERE other."TenantId" IS NOT DISTINCT FROM candidate."TenantId"
              AND other."Name" = candidate."Name"
              AND other."Id" <> candidate."Id"
        )
    ) duplicate_rows;

    SELECT count(*), string_agg("Id"::TEXT, ', ' ORDER BY "Id") FILTER (WHERE row_position <= id_limit)
    INTO api_client_total, api_client_ids
    FROM (
        SELECT candidate."Id", row_number() OVER (ORDER BY candidate."Id") AS row_position
        FROM "dmscs"."ApiClient" candidate
        WHERE EXISTS (
            SELECT 1
            FROM "dmscs"."ApiClient" other
            WHERE other."ApplicationId" = candidate."ApplicationId"
              AND other."Name" = candidate."Name"
              AND other."Id" <> candidate."Id"
        )
    ) duplicate_rows;

    IF data_store_total = 0 AND api_client_total = 0 THEN
        RETURN;
    END IF;

    IF data_store_total > 0 THEN
        message_text := message_text
            || format('%s DataStore row(s) share a (TenantId, Name) with another row, ids: %s', data_store_total, data_store_ids)
            || (CASE WHEN data_store_total > id_limit THEN format(', ... and %s more. ', data_store_total - id_limit) ELSE '. ' END);
    END IF;

    IF api_client_total > 0 THEN
        message_text := message_text
            || format('%s ApiClient row(s) share an (ApplicationId, Name) with another row, ids: %s', api_client_total, api_client_ids)
            || (CASE WHEN api_client_total > id_limit THEN format(', ... and %s more. ', api_client_total - id_limit) ELSE '. ' END);
    END IF;

    RAISE EXCEPTION '%', message_text || remediation;
END$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_DataStore_TenantId_Name'
          AND conrelid = '"dmscs"."DataStore"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."DataStore" ADD CONSTRAINT "UX_DataStore_TenantId_Name" UNIQUE NULLS NOT DISTINCT ("TenantId", "Name");
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'UX_ApiClient_ApplicationId_Name'
          AND conrelid = '"dmscs"."ApiClient"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."ApiClient" ADD CONSTRAINT "UX_ApiClient_ApplicationId_Name" UNIQUE ("ApplicationId", "Name");
    END IF;
END$$;

COMMENT ON CONSTRAINT "UX_DataStore_TenantId_Name" ON "dmscs"."DataStore" IS
    'Data store names are unique within a tenant, and among data stores with no tenant';

COMMENT ON CONSTRAINT "UX_ApiClient_ApplicationId_Name" ON "dmscs"."ApiClient" IS
    'API client names are unique within an application';
