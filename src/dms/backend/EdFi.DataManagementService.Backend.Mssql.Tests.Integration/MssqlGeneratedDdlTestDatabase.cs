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
