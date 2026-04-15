// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record PostgresqlForeignKeyMetadata(
    string ConstraintName,
    string[] Columns,
    string ReferencedSchema,
    string ReferencedTable,
    string[] ReferencedColumns,
    string DeleteAction,
    string UpdateAction
);

internal sealed class PostgresqlGeneratedDdlTestDatabase : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private static readonly string[] _generatedDdlBaselineTables =
    [
        "EffectiveSchema",
        "ResourceKey",
        "SchemaComponent",
    ];
    private static readonly string _resetSql = $$"""
        DO $$
        DECLARE
            truncate_sql text;
            sequence_sql text;
        BEGIN
            SELECT
                CASE
                    WHEN COUNT(*) = 0 THEN NULL
                    ELSE
                        'TRUNCATE TABLE '
                        || string_agg(
                            format('%I.%I', schemaname, tablename),
                            ', '
                            ORDER BY schemaname, tablename
                        )
                        || ' RESTART IDENTITY CASCADE;'
                END
            INTO truncate_sql
            FROM pg_tables
            WHERE schemaname <> 'information_schema'
              AND schemaname !~ '^pg_'
              AND NOT (
                  schemaname = 'dms'
                  AND tablename = ANY (ARRAY[{{string.Join(
            ", ",
            _generatedDdlBaselineTables.Select(tableName => $"'{tableName}'")
        )}}])
              );

            IF truncate_sql IS NOT NULL THEN
                EXECUTE truncate_sql;
            END IF;

            FOR sequence_sql IN
                SELECT format(
                    'ALTER SEQUENCE %I.%I RESTART WITH %s',
                    schemaname,
                    sequencename,
                    start_value
                )
                FROM pg_sequences
                WHERE schemaname <> 'information_schema'
                  AND schemaname !~ '^pg_'
                ORDER BY schemaname, sequencename
            LOOP
                EXECUTE sequence_sql;
            END LOOP;
        END
        $$;
        """;

    private PostgresqlGeneratedDdlTestDatabase(
        string databaseName,
        string connectionString,
        NpgsqlDataSource dataSource
    )
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        _dataSource = dataSource;
    }

    private readonly NpgsqlDataSource _dataSource;
    private bool _disposed;
    private bool _dropDatabaseOnDispose = true;

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<PostgresqlGeneratedDdlTestDatabase> CreateEmptyAsync()
    {
        var databaseName = GenerateUniqueDatabaseName();
        var connectionString = BuildConnectionString(databaseName);

        await CreateDatabaseAsync(databaseName);

        return new(databaseName, connectionString, NpgsqlDataSource.Create(connectionString));
    }

    public static async Task<PostgresqlGeneratedDdlTestDatabase> CreateFromTemplateAsync(
        string templateDatabaseName,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateDatabaseName);

        var databaseName = GenerateUniqueDatabaseName();
        var connectionString = BuildConnectionString(databaseName);

        await CreateDatabaseAsync(databaseName, templateDatabaseName, commandTimeoutSeconds);

        return new(databaseName, connectionString, NpgsqlDataSource.Create(connectionString));
    }

    public static async Task<PostgresqlGeneratedDdlTestDatabase> CreateProvisionedAsync(
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

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = generatedDdl;
            command.CommandTimeout = commandTimeoutSeconds;
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task ResetAsync(int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = _resetSql;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> SequenceExistsAsync(string schema, string sequenceName)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class c
                INNER JOIN pg_namespace n
                    ON n.oid = c.relnamespace
                WHERE c.relkind = 'S'
                  AND n.nspname = @schema
                  AND c.relname = @sequenceName
            );
            """;

        return await ExecuteScalarAsync<bool>(
            sql,
            new NpgsqlParameter("schema", schema),
            new NpgsqlParameter("sequenceName", sequenceName)
        );
    }

    public async Task<string?> GetColumnDefaultAsync(string schema, string tableName, string columnName)
    {
        const string sql = """
            SELECT pg_get_expr(defaults.adbin, defaults.adrelid)
            FROM pg_attrdef defaults
            INNER JOIN pg_class tables
                ON tables.oid = defaults.adrelid
            INNER JOIN pg_namespace schemas
                ON schemas.oid = tables.relnamespace
            INNER JOIN pg_attribute columns
                ON columns.attrelid = tables.oid
               AND columns.attnum = defaults.adnum
            WHERE schemas.nspname = @schema
              AND tables.relname = @tableName
              AND columns.attname = @columnName;
            """;

        return await ExecuteScalarOrDefaultAsync<string>(
            sql,
            new NpgsqlParameter("schema", schema),
            new NpgsqlParameter("tableName", tableName),
            new NpgsqlParameter("columnName", columnName)
        );
    }

    public async Task<IReadOnlyList<PostgresqlForeignKeyMetadata>> GetForeignKeyMetadataAsync(
        string schema,
        string tableName
    )
    {
        const string sql = """
            SELECT
                constraints.conname,
                array_agg(source_columns.attname ORDER BY key_columns.ordinality) AS column_names,
                referenced_schemas.nspname AS referenced_schema,
                referenced_tables.relname AS referenced_table,
                array_agg(referenced_columns.attname ORDER BY key_columns.ordinality) AS referenced_column_names,
                CASE constraints.confdeltype
                    WHEN 'a' THEN 'NO ACTION'
                    WHEN 'r' THEN 'RESTRICT'
                    WHEN 'c' THEN 'CASCADE'
                    WHEN 'n' THEN 'SET NULL'
                    WHEN 'd' THEN 'SET DEFAULT'
                END AS delete_action,
                CASE constraints.confupdtype
                    WHEN 'a' THEN 'NO ACTION'
                    WHEN 'r' THEN 'RESTRICT'
                    WHEN 'c' THEN 'CASCADE'
                    WHEN 'n' THEN 'SET NULL'
                    WHEN 'd' THEN 'SET DEFAULT'
                END AS update_action
            FROM pg_constraint constraints
            INNER JOIN pg_class tables
                ON tables.oid = constraints.conrelid
            INNER JOIN pg_namespace schemas
                ON schemas.oid = tables.relnamespace
            INNER JOIN pg_class referenced_tables
                ON referenced_tables.oid = constraints.confrelid
            INNER JOIN pg_namespace referenced_schemas
                ON referenced_schemas.oid = referenced_tables.relnamespace
            INNER JOIN LATERAL unnest(constraints.conkey) WITH ORDINALITY AS key_columns(attnum, ordinality)
                ON true
            INNER JOIN pg_attribute source_columns
                ON source_columns.attrelid = tables.oid
               AND source_columns.attnum = key_columns.attnum
            INNER JOIN LATERAL unnest(constraints.confkey) WITH ORDINALITY AS referenced_key_columns(attnum, ordinality)
                ON referenced_key_columns.ordinality = key_columns.ordinality
            INNER JOIN pg_attribute referenced_columns
                ON referenced_columns.attrelid = referenced_tables.oid
               AND referenced_columns.attnum = referenced_key_columns.attnum
            WHERE constraints.contype = 'f'
              AND schemas.nspname = @schema
              AND tables.relname = @tableName
            GROUP BY
                constraints.conname,
                referenced_schemas.nspname,
                referenced_tables.relname,
                constraints.confdeltype,
                constraints.confupdtype
            ORDER BY constraints.conname;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("schema", schema));
        command.Parameters.Add(new NpgsqlParameter("tableName", tableName));

        var results = new List<PostgresqlForeignKeyMetadata>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(
                new(
                    ConstraintName: reader.GetString(0),
                    Columns: await reader.GetFieldValueAsync<string[]>(1),
                    ReferencedSchema: reader.GetString(2),
                    ReferencedTable: reader.GetString(3),
                    ReferencedColumns: await reader.GetFieldValueAsync<string[]>(4),
                    DeleteAction: reader.GetString(5),
                    UpdateAction: reader.GetString(6)
                )
            );
        }

        return results;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
        string sql,
        params NpgsqlParameter[] parameters
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
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

    public async Task<int> ExecuteNonQueryAsync(string sql, params NpgsqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        var result = await ExecuteScalarOrDefaultAsync<T>(sql, parameters);
        return result is not null
            ? result
            : throw new InvalidOperationException(
                $"Expected scalar result for SQL but received null.\n{sql}"
            );
    }

    public async Task<T?> ExecuteScalarOrDefaultAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _dataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        if (_dropDatabaseOnDispose)
        {
            await DropDatabaseIfExistsAsync(DatabaseName);
        }

        _disposed = true;
    }

    internal async Task DetachAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _dataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        _dropDatabaseOnDispose = false;
        _disposed = true;
    }

    internal static Task DropDatabaseIfExistsAsync(string databaseName)
    {
        return DropDatabaseIfExistsAsync(BuildAdminConnectionString(), databaseName);
    }

    private static async Task CreateDatabaseAsync(
        string databaseName,
        string? templateDatabaseName = null,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        await using var connection = new NpgsqlConnection(BuildAdminConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = templateDatabaseName is null
            ? $"CREATE DATABASE {QuoteIdentifier(databaseName)}"
            : $"""
                CREATE DATABASE {QuoteIdentifier(databaseName)}
                TEMPLATE {QuoteIdentifier(templateDatabaseName)}
                """;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseIfExistsAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using var terminateCommand = connection.CreateCommand();
        terminateCommand.CommandText = """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @databaseName
              AND pid <> pg_backend_pid();
            """;
        terminateCommand.Parameters.Add(new NpgsqlParameter("databaseName", databaseName));
        await terminateCommand.ExecuteNonQueryAsync();

        await using var dropCommand = connection.CreateCommand();
        dropCommand.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)}";
        await dropCommand.ExecuteNonQueryAsync();
    }

    private static string BuildAdminConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(Configuration.DatabaseConnectionString)
        {
            Database = "postgres",
        };

        return builder.ConnectionString;
    }

    private static string BuildConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Configuration.DatabaseConnectionString)
        {
            Database = databaseName,
        };

        return builder.ConnectionString;
    }

    private static string GenerateUniqueDatabaseName()
    {
        return $"dmsddl{Guid.NewGuid():N}"[..24];
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}
