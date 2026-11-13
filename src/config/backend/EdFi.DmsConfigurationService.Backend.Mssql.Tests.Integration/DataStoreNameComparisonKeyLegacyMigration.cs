// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

// Frozen 0039 from 1d6988be0925c669243f8aa53e4cf6ff9c4708ae. Do not derive this
// replay fixture from the current migration: it must qualify already deployed definitions.
internal static class DataStoreNameComparisonKeyLegacyMigration
{
    public const string Script = """
        -- SPDX-License-Identifier: Apache-2.0
        -- Licensed to the Ed-Fi Alliance under one or more agreements.
        -- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
        -- See the LICENSE and NOTICES files in the project root for more information.

        -- DMS-1439 step 1.3a: add the canonical equality key only. Display names and
        -- UX_DataStore_TenantId_Name retain their existing values and behavior.
        -- Qualify the future query-only UTF8 sort conversion before any schema mutation.
        IF COALESCE(TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), 0) < 15
            THROW 50039, 'DataStore name key upgrade requires SQL Server 2019 or later.', 1;

        IF NOT EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = 'Latin1_General_100_BIN2_UTF8')
            THROW 50039, 'DataStore name key upgrade requires Latin1_General_100_BIN2_UTF8.', 1;

        DECLARE @Utf8Probe varbinary(1024);
        -- Dynamic SQL defers binding the collation until its availability has been checked.
        -- UTF16 bytes encode cafe-acute, U+E000 and U+10000 without a database code-page conversion.
        EXEC sys.sp_executesql
            N'SELECT @Bytes = CONVERT(varbinary(1024), CONVERT(varchar(1024),
                CONVERT(nvarchar(20), 0x630061006600E90000E000D800DC) COLLATE Latin1_General_100_BIN2_UTF8));',
            N'@Bytes varbinary(1024) OUTPUT', @Bytes = @Utf8Probe OUTPUT;

        IF @Utf8Probe IS NULL OR @Utf8Probe <> 0x636166C3A9EE8080F0908080
            THROW 50039, 'DataStore name key upgrade requires lossless UTF8 query conversion.', 1;

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

            -- CREATE FUNCTION must be the first statement in its batch. A local transaction
            -- makes helper creation and column addition atomic under the existing DbUp policy.
            -- Existence guards permit recovery after commit if DbUp did not persist its journal row.
            IF OBJECT_ID(N'dmscs.DataStoreNameComparisonKey', N'FN') IS NULL
                EXEC(N'CREATE FUNCTION dmscs.DataStoreNameComparisonKey (@Name nvarchar(256))
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
                END;');

            IF COL_LENGTH(N'dmscs.DataStore', N'NameComparisonKey') IS NULL
                EXEC(N'ALTER TABLE dmscs.DataStore
                    ADD NameComparisonKey AS dmscs.DataStoreNameComparisonKey(Name) PERSISTED;');

            IF NOT EXISTS (
                SELECT 1 FROM sys.computed_columns
                WHERE object_id = OBJECT_ID(N'dmscs.DataStore') AND name = N'NameComparisonKey'
                  AND is_persisted = 1 AND system_type_id = TYPE_ID(N'varbinary') AND max_length = 516
                  AND COLUMNPROPERTY(object_id, name, 'IsDeterministic') = 1
                  AND COLUMNPROPERTY(object_id, name, 'IsPrecise') = 1
            )
                THROW 50039, 'Existing DataStore NameComparisonKey must be a deterministic persisted varbinary(516) computed column.', 1;

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH;
        """;
}
