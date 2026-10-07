-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Makes data store names unique within a tenant and API client names unique within an application.
--
-- Natural keys: DataStore (TenantId, Name) and ApiClient (ApplicationId, Name). The same data store
-- name may exist in different tenants. SQL Server unique constraints treat NULLs as equal, so
-- single-tenant deployments (TenantId IS NULL) reject duplicates too. A NULL-tenant data store and a
-- tenant's data store may share a name. Names compare under the column collation, which by default
-- ignores case and trailing spaces.
--
-- Upgrade blocker. Earlier versions allowed duplicates, for example from repeated setup-script runs.
-- If any exist, the preflight below stops the upgrade before either constraint is added and lists
-- the offending row ids. It never renames, deletes or chooses among duplicates. Rename a duplicate
-- with PUT /v3/dataStores/{id} or PUT /v3/apiClients/{id}, or remove it with DELETE, on the current
-- version, then retry the upgrade.
--
-- The whole script is a single batch with no GO, so the raised error aborts everything after it.
-- The new constraint names do not contain, and are not contained in, any existing constraint name,
-- because the repositories match unique-violation messages by substring.

-- Duplicate detection uses the equality each constraint applies: plain column equality on Name and
-- ApplicationId, including the column collation, and NULL-equal tenants. The message lists at most
-- 20 ids per table, so it stays far below THROW's 2047-character limit.
DECLARE @idLimit INT = 20;
DECLARE @remediation NVARCHAR(200) =
    N'Rename a duplicate with PUT /v3/dataStores/{id} or PUT /v3/apiClients/{id}, or remove it with DELETE, then retry the upgrade.';
DECLARE @dataStoreTotal INT;
DECLARE @apiClientTotal INT;
DECLARE @dataStoreIds NVARCHAR(MAX);
DECLARE @apiClientIds NVARCHAR(MAX);
DECLARE @message NVARCHAR(2047) = N'Name uniqueness upgrade blocked: ';

WITH duplicate_rows AS (
    SELECT candidate.Id, ROW_NUMBER() OVER (ORDER BY candidate.Id) AS RowPosition
    FROM dmscs.DataStore candidate
    WHERE EXISTS (
        SELECT 1
        FROM dmscs.DataStore other
        WHERE (other.TenantId = candidate.TenantId OR (other.TenantId IS NULL AND candidate.TenantId IS NULL))
          AND other.Name = candidate.Name
          AND other.Id <> candidate.Id
    )
)
SELECT
    @dataStoreTotal = COUNT(*),
    @dataStoreIds = STRING_AGG(CASE WHEN RowPosition <= @idLimit THEN CAST(Id AS NVARCHAR(MAX)) END, N', ')
        WITHIN GROUP (ORDER BY Id)
FROM duplicate_rows;

WITH duplicate_rows AS (
    SELECT candidate.Id, ROW_NUMBER() OVER (ORDER BY candidate.Id) AS RowPosition
    FROM dmscs.ApiClient candidate
    WHERE EXISTS (
        SELECT 1
        FROM dmscs.ApiClient other
        WHERE other.ApplicationId = candidate.ApplicationId
          AND other.Name = candidate.Name
          AND other.Id <> candidate.Id
    )
)
SELECT
    @apiClientTotal = COUNT(*),
    @apiClientIds = STRING_AGG(CASE WHEN RowPosition <= @idLimit THEN CAST(Id AS NVARCHAR(MAX)) END, N', ')
        WITHIN GROUP (ORDER BY Id)
FROM duplicate_rows;

IF @dataStoreTotal > 0 OR @apiClientTotal > 0
BEGIN
    IF @dataStoreTotal > 0
        SET @message = CONCAT(
            @message,
            @dataStoreTotal,
            N' DataStore row(s) share a (TenantId, Name) with another row, ids: ',
            @dataStoreIds,
            CASE
                WHEN @dataStoreTotal > @idLimit THEN CONCAT(N', ... and ', @dataStoreTotal - @idLimit, N' more. ')
                ELSE N'. '
            END
        );

    IF @apiClientTotal > 0
        SET @message = CONCAT(
            @message,
            @apiClientTotal,
            N' ApiClient row(s) share an (ApplicationId, Name) with another row, ids: ',
            @apiClientIds,
            CASE
                WHEN @apiClientTotal > @idLimit THEN CONCAT(N', ... and ', @apiClientTotal - @idLimit, N' more. ')
                ELSE N'. '
            END
        );

    SET @message = CONCAT(@message, @remediation);

    THROW 50000, @message, 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = 'UX_DataStore_TenantId_Name'
      AND parent_object_id = OBJECT_ID('dmscs.DataStore')
)
    ALTER TABLE dmscs.DataStore ADD CONSTRAINT UX_DataStore_TenantId_Name UNIQUE (TenantId, Name);

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = 'UX_ApiClient_ApplicationId_Name'
      AND parent_object_id = OBJECT_ID('dmscs.ApiClient')
)
    ALTER TABLE dmscs.ApiClient ADD CONSTRAINT UX_ApiClient_ApplicationId_Name UNIQUE (ApplicationId, Name);
