-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- DMS-1439 step 1.3c. Deploy with all catalog writers and old CMS binaries drained.
-- Revalidate 0039 independently: it may already be journaled and will not run again.
-- Qualify the future query-only UTF8 sort conversion before any schema mutation.
IF COALESCE(TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), 0) < 15
    THROW 50040, 'DataStore name key upgrade requires SQL Server 2019 or later.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = 'Latin1_General_100_BIN2_UTF8')
    THROW 50040, 'DataStore name key upgrade requires Latin1_General_100_BIN2_UTF8.', 1;

DECLARE @Utf8Probe varbinary(1024);
-- Dynamic SQL defers binding the collation until its availability has been checked.
-- UTF16 bytes encode cafe-acute, U+E000 and U+10000 without a database code-page conversion.
EXEC sys.sp_executesql
    N'SELECT @Bytes = CONVERT(varbinary(1024), CONVERT(varchar(1024),
        CONVERT(nvarchar(20), 0x630061006600E90000E000D800DC) COLLATE Latin1_General_100_BIN2_UTF8));',
    N'@Bytes varbinary(1024) OUTPUT', @Bytes = @Utf8Probe OUTPUT;

IF @Utf8Probe IS NULL OR @Utf8Probe <> 0x636166C3A9EE8080F0908080
    THROW 50040, 'DataStore name key upgrade requires lossless UTF8 query conversion.', 1;

-- Required when defining an indexable computed column and its schema-bound helper.
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    -- Read the table rather than TOP (0), which can optimize away the lock request.
    -- TABLOCKX/HOLDLOCK serializes writers and catalog DDL even when the table is empty.
    DECLARE @CatalogRows bigint;
    SELECT @CatalogRows = COUNT_BIG(*) FROM dmscs.DataStore WITH (TABLOCKX, HOLDLOCK);

    DECLARE @FunctionDefinition nvarchar(max) = N'CREATE FUNCTION dmscs.DataStoreNameComparisonKey (@Name nvarchar(256))
        RETURNS varbinary(516)
        WITH SCHEMABINDING
        AS
        BEGIN
            IF @Name IS NULL RETURN NULL;

            DECLARE @Whitespace nvarchar(25) =
                NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32)
                + NCHAR(133) + NCHAR(160) + NCHAR(5760)
                + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195) + NCHAR(8196)
                + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201) + NCHAR(8202)
                + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288);
            DECLARE @Normalized nvarchar(256) = TRANSLATE(
                TRIM(@Whitespace FROM @Name COLLATE Latin1_General_100_BIN2),
                N''ABCDEFGHIJKLMNOPQRSTUVWXYZ'', N''abcdefghijklmnopqrstuvwxyz'');

            -- The length prefix prevents binary padding from merging different lengths.
            RETURN CONVERT(binary(4), DATALENGTH(@Normalized)) + CONVERT(varbinary(512), @Normalized);
        END;';

    -- Reuse only the approved complete definition, including its signature and body.
    -- Keep the original 0039 source spelling/indentation for already deployed helpers.
    -- Only CRLF/LF differences are normalized; custom alternatives are refused, not rewritten.
    DECLARE @NormalizedDefinition nvarchar(max) =
        REPLACE(@FunctionDefinition, NCHAR(13) + NCHAR(10), NCHAR(10));
    IF NOT EXISTS (
        SELECT 1 FROM sys.sql_modules m
        JOIN sys.objects o ON o.object_id = m.object_id
        WHERE m.object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey') AND o.type = 'FN'
          AND m.is_schema_bound = 1 AND m.uses_ansi_nulls = 1 AND m.uses_quoted_identifier = 1
          AND DATALENGTH(REPLACE(m.definition, NCHAR(13) + NCHAR(10), NCHAR(10))) = DATALENGTH(@NormalizedDefinition)
          AND REPLACE(m.definition, NCHAR(13) + NCHAR(10), NCHAR(10)) COLLATE Latin1_General_100_BIN2
              = @NormalizedDefinition COLLATE Latin1_General_100_BIN2
    ) OR (SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey')) <> 2
      OR NOT EXISTS (
          SELECT 1 FROM sys.parameters
          WHERE object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey') AND parameter_id = 0
            AND system_type_id = TYPE_ID(N'varbinary') AND max_length = 516
      ) OR NOT EXISTS (
          SELECT 1 FROM sys.parameters
          WHERE object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey') AND parameter_id = 1
            AND name = N'@Name' AND system_type_id = TYPE_ID(N'nvarchar') AND max_length = 512
      )
        THROW 50040, 'Existing DataStoreNameComparisonKey helper definition does not match the approved migration definition.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.computed_columns
        WHERE object_id = OBJECT_ID(N'dmscs.DataStore') AND name = N'NameComparisonKey'
          AND is_persisted = 1 AND system_type_id = TYPE_ID(N'varbinary') AND max_length = 516
          AND COLUMNPROPERTY(object_id, name, 'IsDeterministic') = 1
          AND COLUMNPROPERTY(object_id, name, 'IsPrecise') = 1
    )
        THROW 50040, 'Existing DataStore NameComparisonKey must be a deterministic persisted varbinary(516) computed column.', 1;

    -- SQL Server stores this expression with quoted identifiers and outer parentheses.
    -- Metadata and a few matching output probes cannot certify the normalization contract.
    DECLARE @ColumnDefinition nvarchar(max) = N'([dmscs].[DataStoreNameComparisonKey]([Name]))';
    IF NOT EXISTS (
        SELECT 1 FROM sys.computed_columns
        WHERE object_id = OBJECT_ID(N'dmscs.DataStore') AND name = N'NameComparisonKey'
          AND DATALENGTH(definition) = DATALENGTH(@ColumnDefinition)
          AND definition COLLATE Latin1_General_100_BIN2 = @ColumnDefinition COLLATE Latin1_General_100_BIN2
    )
        THROW 50040, 'Existing DataStore NameComparisonKey column definition does not match the approved migration definition.', 1;


    DECLARE @SecondKey sysname;
    SELECT @SecondKey = second_column.name
    FROM sys.key_constraints k
    JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
    JOIN sys.index_columns first_key ON first_key.object_id = i.object_id
        AND first_key.index_id = i.index_id AND first_key.key_ordinal = 1
    JOIN sys.columns first_column ON first_column.object_id = first_key.object_id
        AND first_column.column_id = first_key.column_id
    JOIN sys.index_columns second_key ON second_key.object_id = i.object_id
        AND second_key.index_id = i.index_id AND second_key.key_ordinal = 2
    JOIN sys.columns second_column ON second_column.object_id = second_key.object_id
        AND second_column.column_id = second_key.column_id
    WHERE k.parent_object_id = OBJECT_ID(N'dmscs.DataStore')
      AND k.name = N'UX_DataStore_TenantId_Name' AND k.type = 'UQ'
      AND i.is_unique = 1 AND i.is_unique_constraint = 1 AND i.type = 2
      AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0 AND i.ignore_dup_key = 0
      AND first_column.name = N'TenantId' AND second_column.name IN (N'Name', N'NameComparisonKey')
      AND first_key.is_descending_key = 0 AND second_key.is_descending_key = 0
      AND (SELECT COUNT(*) FROM sys.index_columns all_keys
           WHERE all_keys.object_id = i.object_id AND all_keys.index_id = i.index_id) = 2;

    IF @SecondKey IS NULL
        THROW 50040, 'Existing UX_DataStore_TenantId_Name must be the approved legacy or canonical uniqueness constraint.', 1;

    DECLARE @CollisionCount bigint;
    DECLARE @CollisionIds nvarchar(max);
    WITH candidates AS (
        SELECT Id, COUNT_BIG(*) OVER (PARTITION BY TenantId, NameComparisonKey) AS Occurrences
        FROM dmscs.DataStore
    ), collisions AS (
        SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS RowPosition
        FROM candidates WHERE Occurrences > 1
    )
    SELECT @CollisionCount = COUNT_BIG(*),
        @CollisionIds = STRING_AGG(CASE WHEN RowPosition <= 20 THEN CONVERT(nvarchar(max), Id) END, N', ')
            WITHIN GROUP (ORDER BY Id)
    FROM collisions;

    IF @CollisionCount > 0
    BEGIN
        DECLARE @Message nvarchar(2047) = CONCAT(
            N'Canonical DataStore uniqueness upgrade blocked: ', @CollisionCount, N' row(s), ids: ', @CollisionIds,
            CASE WHEN @CollisionCount > 20 THEN CONCAT(N', ... and ', @CollisionCount - 20, N' more.') ELSE N'.' END,
            N' Rename conflicting rows by ID with PUT /v3/dataStores/{id}, or explicitly remove them with DELETE, then retry the upgrade.');
        THROW 50040, @Message, 1;
    END;

    IF @SecondKey = N'Name'
    BEGIN
        ALTER TABLE dmscs.DataStore DROP CONSTRAINT UX_DataStore_TenantId_Name;
        ALTER TABLE dmscs.DataStore ADD CONSTRAINT UX_DataStore_TenantId_Name UNIQUE (TenantId, NameComparisonKey);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
