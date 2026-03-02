// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.SchemaGenerator.Pgsql.Tests.Integration;

/// <summary>
/// Helper methods for managing test databases and querying schema metadata.
/// </summary>
public static class DatabaseHelper
{
    private static readonly string _maintenanceConnectionString =
        Configuration.MaintenanceConnectionString
        ?? throw new InvalidOperationException("MaintenanceConnection string is not configured.");

    /// <summary>
    /// Creates a fresh test database.
    /// </summary>
    public static async Task CreateDatabaseAsync(string databaseName)
    {
        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Terminates active connections and drops the test database.
    /// </summary>
    public static async Task DropDatabaseAsync(string databaseName)
    {
        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync();

        // Terminate active connections
        await using (var terminateCmd = conn.CreateCommand())
        {
            terminateCmd.CommandText =
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{databaseName}' AND pid <> pg_backend_pid()";
            await terminateCmd.ExecuteNonQueryAsync();
        }

        await using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
        await dropCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Derives a connection string for a specific test database from the maintenance connection string.
    /// </summary>
    public static string GetTestDatabaseConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_maintenanceConnectionString)
        {
            Database = databaseName,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Executes a SQL statement against a database.
    /// </summary>
    public static async Task ExecuteSqlAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns DDL for a minimal dms.Document stub table satisfying FK references.
    /// The generated DDL creates FKs referencing dms.Document(Id, DocumentPartitionKey).
    /// </summary>
    public static string GetDocumentTableStubSql()
    {
        return """
            CREATE SCHEMA IF NOT EXISTS dms;
            CREATE TABLE IF NOT EXISTS dms.Document (
                Id BIGINT NOT NULL,
                DocumentPartitionKey SMALLINT NOT NULL,
                PRIMARY KEY (Id, DocumentPartitionKey)
            );
            """;
    }

    /// <summary>
    /// Gets all table names in the specified schema.
    /// </summary>
    public static async Task<List<string>> GetTablesInSchemaAsync(string connectionString, string schemaName)
    {
        var tables = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE' ORDER BY table_name";
        cmd.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    /// <summary>
    /// Gets all view names in the specified schema.
    /// </summary>
    public static async Task<List<string>> GetViewsInSchemaAsync(string connectionString, string schemaName)
    {
        var views = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT table_name FROM information_schema.views WHERE table_schema = @schema ORDER BY table_name";
        cmd.Parameters.AddWithValue("schema", schemaName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            views.Add(reader.GetString(0));
        }
        return views;
    }

    /// <summary>
    /// Gets constraints for a table. Type codes: u=unique, f=foreign key, p=primary key.
    /// </summary>
    public static async Task<List<(string Name, string Type)>> GetConstraintsAsync(
        string connectionString,
        string schemaName,
        string tableName
    )
    {
        var constraints = new List<(string Name, string Type)>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT con.conname, con.contype::text
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
            WHERE nsp.nspname = @schema AND rel.relname = @table
            ORDER BY con.conname
            """;
        cmd.Parameters.AddWithValue("schema", schemaName);
        cmd.Parameters.AddWithValue("table", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            constraints.Add((reader.GetString(0), reader.GetString(1)));
        }
        return constraints;
    }

    /// <summary>
    /// Gets column information for a table.
    /// </summary>
    public static async Task<List<(string Name, string DataType, bool IsNullable)>> GetColumnsAsync(
        string connectionString,
        string schemaName,
        string tableName
    )
    {
        var columns = new List<(string Name, string DataType, bool IsNullable)>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        cmd.Parameters.AddWithValue("schema", schemaName);
        cmd.Parameters.AddWithValue("table", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2) == "YES"));
        }
        return columns;
    }

    /// <summary>
    /// Gets index names for a table.
    /// </summary>
    public static async Task<List<string>> GetIndexesAsync(
        string connectionString,
        string schemaName,
        string tableName
    )
    {
        var indexes = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = @schema AND tablename = @table
            ORDER BY indexname
            """;
        cmd.Parameters.AddWithValue("schema", schemaName);
        cmd.Parameters.AddWithValue("table", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }
        return indexes;
    }

    /// <summary>
    /// Gets all schema names in the database (excluding system schemas).
    /// </summary>
    public static async Task<List<string>> GetSchemasAsync(string connectionString)
    {
        var schemas = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_name
            FROM information_schema.schemata
            WHERE schema_name NOT IN ('information_schema', 'pg_catalog', 'pg_toast')
            ORDER BY schema_name
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }
        return schemas;
    }
}
