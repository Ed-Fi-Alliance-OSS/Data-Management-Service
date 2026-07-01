// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public sealed record MssqlDatabaseResetPlan(string Sql)
{
    public static MssqlDatabaseResetPlan Dynamic(params (string Schema, string Table)[] excludedTables)
    {
        return new(MssqlDatabaseResetSql.Build(excludedTables));
    }
}

public static class MssqlDatabaseResetSql
{
    public static async Task<MssqlDatabaseResetPlan> BuildPrecomputedAsync(
        string connectionString,
        int commandTimeoutSeconds,
        params (string Schema, string Table)[] excludedTables
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var metadata = await ReadTargetMetadataAsync(connectionString, commandTimeoutSeconds, excludedTables);
        var sequences = await ReadTargetSequencesAsync(connectionString, commandTimeoutSeconds);

        return new(BuildPrecomputed(metadata.Tables, metadata.DeleteIdentities, sequences));
    }

    public static string Build(params (string Schema, string Table)[] excludedTables)
    {
        var excludedTableFilter = BuildExcludedTableFilter(excludedTables);

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
                [HasIdentity] bit NOT NULL,
                [IdentitySeed] decimal(38, 0) NULL,
                [IdentityIncrement] decimal(38, 0) NULL,
                [IdentityLastValue] decimal(38, 0) NULL
            );

            INSERT INTO @targetTables (
                [SchemaName],
                [TableName],
                [QualifiedName],
                [EscapedQualifiedName],
                [HasIdentity],
                [IdentitySeed],
                [IdentityIncrement],
                [IdentityLastValue]
            )
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
                END,
                identity_columns.[seed_value],
                identity_columns.[increment_value],
                identity_columns.[last_value]
            FROM sys.tables tables
            INNER JOIN sys.schemas schemas
                ON schemas.[schema_id] = tables.[schema_id]
            OUTER APPLY (
                SELECT TOP (1)
                    CONVERT(decimal(38, 0), identity_columns.[seed_value]) AS [seed_value],
                    CONVERT(decimal(38, 0), identity_columns.[increment_value]) AS [increment_value],
                    CONVERT(decimal(38, 0), identity_columns.[last_value]) AS [last_value]
                FROM sys.identity_columns identity_columns
                WHERE identity_columns.[object_id] = tables.[object_id]
                ORDER BY identity_columns.[column_id]
            ) identity_columns
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
                    + N''', RESEED, '
                    + CONVERT(
                        nvarchar(100),
                        CASE
                            WHEN [IdentityLastValue] IS NULL THEN [IdentitySeed]
                            ELSE [IdentitySeed] - [IdentityIncrement]
                        END
                    )
                    + N') WITH NO_INFOMSGS;'
                    AS nvarchar(max)
                ),
                @lineBreak
            )
            FROM @targetTables
            WHERE [HasIdentity] = 1
              AND [IdentitySeed] IS NOT NULL
              AND [IdentityIncrement] IS NOT NULL;

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

    private static async Task<MssqlDatabaseResetTargetMetadata> ReadTargetMetadataAsync(
        string connectionString,
        int commandTimeoutSeconds,
        (string Schema, string Table)[] excludedTables
    )
    {
        var excludedTableFilter = BuildExcludedTableFilter(excludedTables);
        var sql = $$"""
            SELECT
                QUOTENAME(schemas.[name]) + N'.' + QUOTENAME(tables.[name]) AS [QualifiedName],
                REPLACE(QUOTENAME(schemas.[name]) + N'.' + QUOTENAME(tables.[name]), N'''', N'''''') AS [EscapedQualifiedName],
                CASE
                    WHEN tables.[temporal_type] = 0
                     AND tables.[is_memory_optimized] = 0
                     AND NOT EXISTS (
                        SELECT 1
                        FROM sys.foreign_keys foreign_keys
                        WHERE foreign_keys.[referenced_object_id] = tables.[object_id]
                          AND foreign_keys.[parent_object_id] <> foreign_keys.[referenced_object_id]
                     )
                    THEN CAST(1 AS bit)
                    ELSE CAST(0 AS bit)
                END AS [CanTruncate],
                identity_columns.[name] AS [IdentityColumnName],
                CONVERT(nvarchar(100), identity_columns.[seed_value]) AS [IdentitySeed],
                CONVERT(nvarchar(100), identity_columns.[increment_value]) AS [IdentityIncrement]
            FROM sys.tables tables
            INNER JOIN sys.schemas schemas
                ON schemas.[schema_id] = tables.[schema_id]
            OUTER APPLY (
                SELECT TOP (1)
                    identity_columns.[name],
                    identity_columns.[seed_value],
                    identity_columns.[increment_value]
                FROM sys.identity_columns identity_columns
                WHERE identity_columns.[object_id] = tables.[object_id]
                ORDER BY identity_columns.[column_id]
            ) identity_columns
            WHERE tables.[is_ms_shipped] = 0
              AND schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys'){{excludedTableFilter}}
            ORDER BY schemas.[name], tables.[name];
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;

        List<MssqlDatabaseResetTargetTable> tables = [];
        List<MssqlDatabaseResetTargetIdentity> deleteIdentities = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var qualifiedName = reader.GetString(0);
            var escapedQualifiedName = reader.GetString(1);
            var canTruncate = reader.GetBoolean(2);

            tables.Add(new(qualifiedName, canTruncate));

            if (!canTruncate && !await reader.IsDBNullAsync(3))
            {
                deleteIdentities.Add(
                    new(
                        qualifiedName,
                        escapedQualifiedName,
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5)
                    )
                );
            }
        }

        return new(tables, deleteIdentities);
    }

    private static async Task<IReadOnlyList<MssqlDatabaseResetTargetSequence>> ReadTargetSequencesAsync(
        string connectionString,
        int commandTimeoutSeconds
    )
    {
        const string sql = """
            SELECT
                QUOTENAME(schemas.[name]) + N'.' + QUOTENAME(sequences.[name]),
                CONVERT(nvarchar(100), sequences.[start_value])
            FROM sys.sequences sequences
            INNER JOIN sys.schemas schemas
                ON schemas.[schema_id] = sequences.[schema_id]
            WHERE schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
            ORDER BY schemas.[name], sequences.[name];
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;

        List<MssqlDatabaseResetTargetSequence> sequences = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            sequences.Add(new(reader.GetString(0), reader.GetString(1)));
        }

        return sequences;
    }

    private static string BuildPrecomputed(
        IReadOnlyList<MssqlDatabaseResetTargetTable> tables,
        IReadOnlyList<MssqlDatabaseResetTargetIdentity> deleteIdentities,
        IReadOnlyList<MssqlDatabaseResetTargetSequence> sequences
    )
    {
        List<string> enableConstraintStatements =
        [
            .. tables.Select(table => $"ALTER TABLE {table.QualifiedName} WITH CHECK CHECK CONSTRAINT ALL;"),
        ];
        List<string> enableTriggerStatements =
        [
            .. tables.Select(table => $"ALTER TABLE {table.QualifiedName} ENABLE TRIGGER ALL;"),
        ];

        StringBuilder builder = new();
        builder.AppendLine("SET NOCOUNT ON;");
        builder.AppendLine();
        builder.AppendLine("BEGIN TRY");
        AppendStatements(
            builder,
            tables.Select(table => $"ALTER TABLE {table.QualifiedName} DISABLE TRIGGER ALL;")
        );
        AppendStatements(
            builder,
            tables.Select(table => $"ALTER TABLE {table.QualifiedName} NOCHECK CONSTRAINT ALL;")
        );
        AppendStatements(
            builder,
            tables.Where(table => table.CanTruncate).Select(table => $"TRUNCATE TABLE {table.QualifiedName};")
        );
        AppendStatements(
            builder,
            tables.Where(table => !table.CanTruncate).Select(table => $"DELETE FROM {table.QualifiedName};")
        );
        AppendStatements(builder, BuildIdentityReseedStatements(deleteIdentities));
        AppendStatements(
            builder,
            sequences.Select(sequence =>
                $"ALTER SEQUENCE {sequence.QualifiedName} RESTART WITH {sequence.StartValue};"
            )
        );
        AppendStatements(builder, enableConstraintStatements);
        AppendStatements(builder, enableTriggerStatements);
        builder.AppendLine("END TRY");
        builder.AppendLine("BEGIN CATCH");
        builder.AppendLine("    BEGIN TRY");
        AppendStatements(builder, enableConstraintStatements, "        ");
        builder.AppendLine("    END TRY");
        builder.AppendLine("    BEGIN CATCH");
        builder.AppendLine("    END CATCH;");
        builder.AppendLine();
        builder.AppendLine("    BEGIN TRY");
        AppendStatements(builder, enableTriggerStatements, "        ");
        builder.AppendLine("    END TRY");
        builder.AppendLine("    BEGIN CATCH");
        builder.AppendLine("    END CATCH;");
        builder.AppendLine();
        builder.AppendLine("    THROW;");
        builder.AppendLine("END CATCH;");

        return builder.ToString();
    }

    private static IEnumerable<string> BuildIdentityReseedStatements(
        IReadOnlyList<MssqlDatabaseResetTargetIdentity> identities
    )
    {
        for (var identityIndex = 0; identityIndex < identities.Count; identityIndex++)
        {
            var identity = identities[identityIndex];
            var suffix = (identityIndex + 1).ToString("D4", CultureInfo.InvariantCulture);
            var escapedIdentityColumnName = EscapeSqlLiteral(identity.IdentityColumnName);

            yield return $$"""
                DECLARE @identityLastValue{{suffix}} decimal(38, 0);
                SELECT @identityLastValue{{suffix}} = CONVERT(decimal(38, 0), identity_columns.[last_value])
                FROM sys.identity_columns identity_columns
                WHERE identity_columns.[object_id] = OBJECT_ID(N'{{identity.EscapedQualifiedName}}')
                  AND identity_columns.[name] = N'{{escapedIdentityColumnName}}';

                DECLARE @identityReseedSql{{suffix}} nvarchar(max) =
                    N'DBCC CHECKIDENT (''{{identity.EscapedQualifiedName}}'', RESEED, '
                    + CONVERT(
                        nvarchar(100),
                        CASE
                            WHEN @identityLastValue{{suffix}} IS NULL THEN {{identity.SeedValue}}
                            ELSE {{identity.SeedValue}} - {{identity.IncrementValue}}
                        END
                    )
                    + N') WITH NO_INFOMSGS;';
                EXEC sys.sp_executesql @identityReseedSql{{suffix}};
                """;
        }
    }

    private static void AppendStatements(
        StringBuilder builder,
        IEnumerable<string> statements,
        string indent = "    "
    )
    {
        foreach (var statement in statements)
        {
            var normalizedStatement = statement.Replace("\r\n", "\n", StringComparison.Ordinal);

            foreach (var line in normalizedStatement.Split('\n'))
            {
                builder.Append(indent);
                builder.AppendLine(line);
            }

            builder.AppendLine();
        }
    }

    private static string BuildExcludedTableFilter((string Schema, string Table)[] excludedTables)
    {
        return excludedTables.Length == 0
            ? string.Empty
            : $$"""

                  AND NOT (
                      {{string.Join(
                $"{Environment.NewLine}                      OR ",
                excludedTables.Select(excludedTable =>
                    $"(schemas.[name] = N'{EscapeSqlLiteral(excludedTable.Schema)}' AND tables.[name] = N'{EscapeSqlLiteral(excludedTable.Table)}')"
                )
            )}}
                  )
            """;
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private sealed record MssqlDatabaseResetTargetMetadata(
        IReadOnlyList<MssqlDatabaseResetTargetTable> Tables,
        IReadOnlyList<MssqlDatabaseResetTargetIdentity> DeleteIdentities
    );

    private sealed record MssqlDatabaseResetTargetTable(string QualifiedName, bool CanTruncate);

    private sealed record MssqlDatabaseResetTargetIdentity(
        string QualifiedName,
        string EscapedQualifiedName,
        string IdentityColumnName,
        string SeedValue,
        string IncrementValue
    );

    private sealed record MssqlDatabaseResetTargetSequence(string QualifiedName, string StartValue);
}
