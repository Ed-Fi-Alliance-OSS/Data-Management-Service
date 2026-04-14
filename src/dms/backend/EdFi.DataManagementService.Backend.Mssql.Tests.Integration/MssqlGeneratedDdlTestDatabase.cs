// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlForeignKeyMetadata(
    string ConstraintName,
    string[] Columns,
    string ReferencedSchema,
    string ReferencedTable,
    string[] ReferencedColumns,
    string DeleteAction,
    string UpdateAction
);

internal sealed partial class MssqlGeneratedDdlTestDatabase : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private static readonly string[] _generatedDdlBaselineTables =
    [
        "EffectiveSchema",
        "ResourceKey",
        "SchemaComponent",
    ];
    private static readonly string _resetSql = $$"""
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
          AND schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
          AND NOT (
              schemas.[name] = N'dms'
              AND tables.[name] IN ({{string.Join(
            ", ",
            _generatedDdlBaselineTables.Select(tableName => $"N'{tableName}'")
        )}})
          );

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

    private MssqlGeneratedDdlTestDatabase(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static Task<MssqlGeneratedDdlTestDatabase> CreateEmptyAsync()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            throw new InvalidOperationException(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(databaseName);

        MssqlTestDatabaseHelper.CreateDatabase(databaseName);

        return Task.FromResult(new MssqlGeneratedDdlTestDatabase(databaseName, connectionString));
    }

    public static async Task<MssqlGeneratedDdlTestDatabase> CreateProvisionedAsync(
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        var database = await CreateEmptyAsync();

        try
        {
            await database.ApplyGeneratedDdlAsync(generatedDdl, commandTimeoutSeconds);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task ApplyGeneratedDdlAsync(
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            foreach (var batch in SplitOnGoBatchSeparator(generatedDdl))
            {
                await using SqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batch;
                command.CommandTimeout = commandTimeoutSeconds;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            SqlConnection.ClearPool(connection);
        }
    }

    public async Task ResetAsync(int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = _resetSql;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> SequenceExistsAsync(string schema, string sequenceName)
    {
        const string sql = """
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM sys.sequences sequences
                    INNER JOIN sys.schemas schemas
                        ON schemas.schema_id = sequences.schema_id
                    WHERE schemas.name = @schema
                      AND sequences.name = @sequenceName
                )
                THEN CAST(1 AS bit)
                ELSE CAST(0 AS bit)
            END;
            """;

        return await ExecuteScalarAsync<bool>(
            sql,
            new SqlParameter("@schema", schema),
            new SqlParameter("@sequenceName", sequenceName)
        );
    }

    public async Task<string?> GetColumnDefaultAsync(string schema, string tableName, string columnName)
    {
        const string sql = """
            SELECT default_constraints.definition
            FROM sys.default_constraints default_constraints
            INNER JOIN sys.columns columns
                ON columns.default_object_id = default_constraints.object_id
            INNER JOIN sys.tables tables
                ON tables.object_id = columns.object_id
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = tables.schema_id
            WHERE schemas.name = @schema
              AND tables.name = @tableName
              AND columns.name = @columnName;
            """;

        return await ExecuteScalarOrDefaultAsync<string>(
            sql,
            new SqlParameter("@schema", schema),
            new SqlParameter("@tableName", tableName),
            new SqlParameter("@columnName", columnName)
        );
    }

    public async Task<IReadOnlyList<MssqlForeignKeyMetadata>> GetForeignKeyMetadataAsync(
        string schema,
        string tableName
    )
    {
        const string sql = """
            SELECT
                foreign_keys.name AS ConstraintName,
                foreign_key_columns.constraint_column_id AS ColumnOrdinal,
                source_columns.name AS ColumnName,
                referenced_schemas.name AS ReferencedSchema,
                referenced_tables.name AS ReferencedTable,
                referenced_columns.name AS ReferencedColumnName,
                foreign_keys.delete_referential_action_desc AS DeleteAction,
                foreign_keys.update_referential_action_desc AS UpdateAction
            FROM sys.foreign_keys foreign_keys
            INNER JOIN sys.foreign_key_columns foreign_key_columns
                ON foreign_key_columns.constraint_object_id = foreign_keys.object_id
            INNER JOIN sys.tables tables
                ON tables.object_id = foreign_keys.parent_object_id
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = tables.schema_id
            INNER JOIN sys.columns source_columns
                ON source_columns.object_id = tables.object_id
               AND source_columns.column_id = foreign_key_columns.parent_column_id
            INNER JOIN sys.tables referenced_tables
                ON referenced_tables.object_id = foreign_keys.referenced_object_id
            INNER JOIN sys.schemas referenced_schemas
                ON referenced_schemas.schema_id = referenced_tables.schema_id
            INNER JOIN sys.columns referenced_columns
                ON referenced_columns.object_id = referenced_tables.object_id
               AND referenced_columns.column_id = foreign_key_columns.referenced_column_id
            WHERE schemas.name = @schema
              AND tables.name = @tableName
            ORDER BY foreign_keys.name, foreign_key_columns.constraint_column_id;
            """;

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange([
            new SqlParameter("@schema", schema),
            new SqlParameter("@tableName", tableName),
        ]);

        await using var reader = await command.ExecuteReaderAsync();
        Dictionary<string, ForeignKeyMetadataBuilder> builders = new(StringComparer.Ordinal);
        List<string> orderedConstraintNames = [];

        while (await reader.ReadAsync())
        {
            var constraintName = reader.GetString(0);

            if (!builders.TryGetValue(constraintName, out var builder))
            {
                builder = new ForeignKeyMetadataBuilder(
                    constraintName,
                    reader.GetString(3),
                    reader.GetString(4),
                    NormalizeReferentialAction(reader.GetString(6)),
                    NormalizeReferentialAction(reader.GetString(7))
                );
                builders[constraintName] = builder;
                orderedConstraintNames.Add(constraintName);
            }

            builder.Columns.Add(reader.GetString(2));
            builder.ReferencedColumns.Add(reader.GetString(5));
        }

        return orderedConstraintNames.Select(constraintName => builders[constraintName].Build()).ToArray();
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
        string sql,
        params SqlParameter[] parameters
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        List<IReadOnlyDictionary<string, object?>> rows = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Dictionary<string, object?> row = new(StringComparer.Ordinal);

            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row[reader.GetName(ordinal)] = await reader.IsDBNullAsync(ordinal)
                    ? null
                    : reader.GetValue(ordinal);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<int> ExecuteNonQueryAsync(string sql, params SqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        var result = await ExecuteScalarOrDefaultAsync<T>(sql, parameters);
        return result is not null
            ? result
            : throw new InvalidOperationException(
                $"Expected scalar result for SQL but received null.\n{sql}"
            );
    }

    public async Task<T?> ExecuteScalarOrDefaultAsync<T>(string sql, params SqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        var result = await command.ExecuteScalarAsync();

        if (result is null || result is DBNull)
        {
            return default;
        }

        if (result is T typedResult)
        {
            return typedResult;
        }

        return (T?)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        MssqlTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> SplitOnGoBatchSeparator(string sql) =>
        GoBatchSeparatorPattern().Split(sql).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    private static string NormalizeReferentialAction(string value)
    {
        return value.Replace('_', ' ');
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparatorPattern();

    private sealed record ForeignKeyMetadataBuilder(
        string ConstraintName,
        string ReferencedSchema,
        string ReferencedTable,
        string DeleteAction,
        string UpdateAction
    )
    {
        public List<string> Columns { get; } = [];

        public List<string> ReferencedColumns { get; } = [];

        public MssqlForeignKeyMetadata Build()
        {
            return new(
                ConstraintName,
                [.. Columns],
                ReferencedSchema,
                ReferencedTable,
                [.. ReferencedColumns],
                DeleteAction,
                UpdateAction
            );
        }
    }
}
