// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

public static class PostgresTestDatabaseHelper
{
    public static string GenerateUniqueDatabaseName()
    {
        return $"dms_test_{Guid.NewGuid():N}"[..24];
    }

    public static string BuildConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.PostgresAdminConnectionString)
        {
            Database = databaseName,
        };
        return builder.ConnectionString;
    }

    public static void CreateDatabase(string databaseName)
    {
        using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        // Database names are generated internally (GUID-based), safe to interpolate
        command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
        command.ExecuteNonQuery();
    }

    public static void DropDatabaseIfExists(string databaseName)
    {
        // Clear all Npgsql connection pools to release any held connections
        NpgsqlConnection.ClearAllPools();

        using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
        connection.Open();

        // Terminate active connections to the target database
        // Database names are generated internally (GUID-based), safe to interpolate
        using var terminateCommand = connection.CreateCommand();
        terminateCommand.CommandText = $"""
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{databaseName}' AND pid <> pg_backend_pid();
            """;
        terminateCommand.ExecuteNonQuery();

        using var dropCommand = connection.CreateCommand();
        dropCommand.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\";";
        dropCommand.ExecuteNonQuery();
    }
}
