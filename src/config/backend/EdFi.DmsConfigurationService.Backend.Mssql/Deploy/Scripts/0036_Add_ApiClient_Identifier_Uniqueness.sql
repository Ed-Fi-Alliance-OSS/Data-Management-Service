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
-- under the column's collation (case sensitivity follows that collation; trailing spaces are
-- insignificant to = either way) and on the UUID. The message lists at most 20 ids per column; the
-- counts are complete, the id lists are a sample.
--
-- The preflight and both constraint additions run in one explicit transaction. The preflight reads
-- take an exclusive table lock that is held until COMMIT, so no row can be inserted between the
-- preflight and the constraints. XACT_ABORT dooms the transaction on any statement error and the
-- CATCH block rolls it back explicitly and rethrows, so a failure of the second addition undoes the
-- first. The existence guards make a retry converge and leave a constraint that already exists
-- untouched. The whole script is a single batch with no GO. The repositories identify a violated
-- constraint by reading the quoted name out of the SQL Server error message and comparing it whole,
-- so the new constraint names only have to differ from every existing constraint name.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @idLimit INT = 20;
    DECLARE @remediation NVARCHAR(300) =
        N'Resolve the duplicates with the operator procedure in docs/OPERATIONS.md ("Configuration Service database upgrades"), then retry the upgrade. Do not use DELETE /v3/apiClients/{id} for these rows.';
    DECLARE @clientIdTotal INT;
    DECLARE @clientUuidTotal INT;
    DECLARE @clientIdIds NVARCHAR(MAX);
    DECLARE @clientUuidIds NVARCHAR(MAX);
    DECLARE @message NVARCHAR(2047) = N'API client identifier uniqueness upgrade blocked: ';

    WITH duplicate_rows AS (
        SELECT candidate.Id, ROW_NUMBER() OVER (ORDER BY candidate.Id) AS RowPosition
        FROM dmscs.ApiClient candidate WITH (TABLOCKX, HOLDLOCK)
        WHERE EXISTS (
            SELECT 1
            FROM dmscs.ApiClient other
            WHERE other.ClientId = candidate.ClientId
              AND other.Id <> candidate.Id
        )
    )
    SELECT
        @clientIdTotal = COUNT(*),
        @clientIdIds = STRING_AGG(CASE WHEN RowPosition <= @idLimit THEN CAST(Id AS NVARCHAR(MAX)) END, N', ')
            WITHIN GROUP (ORDER BY Id)
    FROM duplicate_rows;

    WITH duplicate_rows AS (
        SELECT candidate.Id, ROW_NUMBER() OVER (ORDER BY candidate.Id) AS RowPosition
        FROM dmscs.ApiClient candidate WITH (TABLOCKX, HOLDLOCK)
        WHERE EXISTS (
            SELECT 1
            FROM dmscs.ApiClient other
            WHERE other.ClientUuid = candidate.ClientUuid
              AND other.Id <> candidate.Id
        )
    )
    SELECT
        @clientUuidTotal = COUNT(*),
        @clientUuidIds = STRING_AGG(CASE WHEN RowPosition <= @idLimit THEN CAST(Id AS NVARCHAR(MAX)) END, N', ')
            WITHIN GROUP (ORDER BY Id)
    FROM duplicate_rows;

    IF @clientIdTotal > 0 OR @clientUuidTotal > 0
    BEGIN
        IF @clientIdTotal > 0
            SET @message = CONCAT(
                @message,
                @clientIdTotal,
                N' ApiClient row(s) share a ClientId with another row, ids: ',
                @clientIdIds,
                CASE
                    WHEN @clientIdTotal > @idLimit THEN CONCAT(N', ... and ', @clientIdTotal - @idLimit, N' more. ')
                    ELSE N'. '
                END
            );

        IF @clientUuidTotal > 0
            SET @message = CONCAT(
                @message,
                @clientUuidTotal,
                N' ApiClient row(s) share a ClientUuid with another row, ids: ',
                @clientUuidIds,
                CASE
                    WHEN @clientUuidTotal > @idLimit THEN CONCAT(N', ... and ', @clientUuidTotal - @idLimit, N' more. ')
                    ELSE N'. '
                END
            );

        SET @message = CONCAT(@message, @remediation);

        THROW 50000, @message, 1;
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE name = 'UX_ApiClient_ClientId'
          AND parent_object_id = OBJECT_ID('dmscs.ApiClient')
    )
        ALTER TABLE dmscs.ApiClient ADD CONSTRAINT UX_ApiClient_ClientId UNIQUE (ClientId);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE name = 'UX_ApiClient_ClientUuid'
          AND parent_object_id = OBJECT_ID('dmscs.ApiClient')
    )
        ALTER TABLE dmscs.ApiClient ADD CONSTRAINT UX_ApiClient_ClientUuid UNIQUE (ClientUuid);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
SET XACT_ABORT OFF;
