// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlDatabaseResetSql
{
    public static string Build(params (string Schema, string Table)[] excludedTables)
    {
        var excludedTableFilter =
            excludedTables.Length == 0
                ? string.Empty
                : $$"""

                      AND NOT (
                          {{string.Join(
                $"{Environment.NewLine}                          OR ",
                excludedTables.Select(excludedTable =>
                    $"(schemas.[name] = N'{EscapeSqlLiteral(excludedTable.Schema)}' AND tables.[name] = N'{EscapeSqlLiteral(excludedTable.Table)}')"
                )
            )}}
                      )
                """;

        return $$"""
            SET NOCOUNT ON;

            DECLARE @lineBreak nchar(1) = NCHAR(10);
            DECLARE @disableTriggerSql nvarchar(max);
            DECLARE @disableConstraintSql nvarchar(max);
            DECLARE @deleteSql nvarchar(max);
            DECLARE @reseedIdentitySql nvarchar(max);
            DECLARE @restartSequenceSql nvarchar(max);
            DECLARE @enableConstraintSql nvarchar(max);
            DECLARE @enableTriggerSql nvarchar(max);

            DECLARE @targetTables TABLE
            (
                [SchemaName] sysname NOT NULL,
                [TableName] sysname NOT NULL,
                [QualifiedName] nvarchar(517) NOT NULL,
                [EscapedQualifiedName] nvarchar(517) NOT NULL,
                [HasIdentity] bit NOT NULL
            );

            INSERT INTO @targetTables ([SchemaName], [TableName], [QualifiedName], [EscapedQualifiedName], [HasIdentity])
            SELECT
                schemas.[name],
                tables.[name],
                QUOTENAME(schemas.[name]) + N'.' + QUOTENAME(tables.[name]),
                REPLACE(QUOTENAME(schemas.[name]) + N'.' + QUOTENAME(tables.[name]), N'''', N''''''),
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM sys.identity_columns identity_columns
                        WHERE identity_columns.[object_id] = tables.[object_id]
                    )
                    THEN CAST(1 AS bit)
                    ELSE CAST(0 AS bit)
                END
            FROM sys.tables tables
            INNER JOIN sys.schemas schemas
                ON schemas.[schema_id] = tables.[schema_id]
            WHERE tables.[is_ms_shipped] = 0
              AND schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys'){{excludedTableFilter}};

            DECLARE @targetSequences TABLE
            (
                [QualifiedName] nvarchar(517) NOT NULL,
                [StartValue] nvarchar(100) NOT NULL
            );

            INSERT INTO @targetSequences ([QualifiedName], [StartValue])
            SELECT
                QUOTENAME(schemas.[name]) + N'.' + QUOTENAME(sequences.[name]),
                CONVERT(nvarchar(100), sequences.[start_value])
            FROM sys.sequences sequences
            INNER JOIN sys.schemas schemas
                ON schemas.[schema_id] = sequences.[schema_id]
            WHERE schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys');

            SELECT @disableTriggerSql = STRING_AGG(
                CAST(N'ALTER TABLE ' + [QualifiedName] + N' DISABLE TRIGGER ALL;' AS nvarchar(max)),
                @lineBreak
            )
            FROM @targetTables;

            SELECT @disableConstraintSql = STRING_AGG(
                CAST(N'ALTER TABLE ' + [QualifiedName] + N' NOCHECK CONSTRAINT ALL;' AS nvarchar(max)),
                @lineBreak
            )
            FROM @targetTables;

            SELECT @deleteSql = STRING_AGG(
                CAST(N'DELETE FROM ' + [QualifiedName] + N';' AS nvarchar(max)),
                @lineBreak
            )
            FROM @targetTables;

            SELECT @reseedIdentitySql = STRING_AGG(
                CAST(
                    N'DBCC CHECKIDENT ('''
                    + [EscapedQualifiedName]
                    + N''', RESEED, 0) WITH NO_INFOMSGS;'
                    AS nvarchar(max)
                ),
                @lineBreak
            )
            FROM @targetTables
            WHERE [HasIdentity] = 1;

            SELECT @restartSequenceSql = STRING_AGG(
                CAST(
                    N'ALTER SEQUENCE ' + [QualifiedName] + N' RESTART WITH ' + [StartValue] + N';'
                    AS nvarchar(max)
                ),
                @lineBreak
            )
            FROM @targetSequences;

            SELECT @enableConstraintSql = STRING_AGG(
                CAST(
                    N'ALTER TABLE ' + [QualifiedName] + N' WITH CHECK CHECK CONSTRAINT ALL;'
                    AS nvarchar(max)
                ),
                @lineBreak
            )
            FROM @targetTables;

            SELECT @enableTriggerSql = STRING_AGG(
                CAST(N'ALTER TABLE ' + [QualifiedName] + N' ENABLE TRIGGER ALL;' AS nvarchar(max)),
                @lineBreak
            )
            FROM @targetTables;

            BEGIN TRY
                IF @disableTriggerSql IS NOT NULL
                    EXEC sys.sp_executesql @disableTriggerSql;

                IF @disableConstraintSql IS NOT NULL
                    EXEC sys.sp_executesql @disableConstraintSql;

                IF @deleteSql IS NOT NULL
                    EXEC sys.sp_executesql @deleteSql;

                IF @reseedIdentitySql IS NOT NULL
                    EXEC sys.sp_executesql @reseedIdentitySql;

                IF @restartSequenceSql IS NOT NULL
                    EXEC sys.sp_executesql @restartSequenceSql;

                IF @enableConstraintSql IS NOT NULL
                    EXEC sys.sp_executesql @enableConstraintSql;

                IF @enableTriggerSql IS NOT NULL
                    EXEC sys.sp_executesql @enableTriggerSql;
            END TRY
            BEGIN CATCH
                BEGIN TRY
                    IF @enableConstraintSql IS NOT NULL
                        EXEC sys.sp_executesql @enableConstraintSql;
                END TRY
                BEGIN CATCH
                END CATCH;

                BEGIN TRY
                    IF @enableTriggerSql IS NOT NULL
                        EXEC sys.sp_executesql @enableTriggerSql;
                END TRY
                BEGIN CATCH
                END CATCH;

                THROW;
            END CATCH;
            """;
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
