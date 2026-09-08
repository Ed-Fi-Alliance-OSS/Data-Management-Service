// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public static class PostgresqlTestDatabaseHelper
{
    public static string GenerateUniqueDatabaseName()
    {
        return $"dmsrr{Guid.NewGuid():N}"[..24];
    }

    public static string BuildConnectionString(string databaseName)
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseConnectionString)
        {
            Database = databaseName,
        };

        return builder.ConnectionString;
    }

    public static void CreateDatabase(string databaseName)
    {
        using NpgsqlConnection connection = new(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"""CREATE DATABASE "{databaseName}";""";
        command.ExecuteNonQuery();
    }

    public static void DropDatabaseIfExists(string databaseName)
    {
        NpgsqlConnection.ClearAllPools();

        using NpgsqlConnection connection = new(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using (NpgsqlCommand terminateCommand = connection.CreateCommand())
        {
            terminateCommand.CommandText = $$"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{{databaseName}}' AND pid <> pg_backend_pid();
                """;
            terminateCommand.ExecuteNonQuery();
        }

        using NpgsqlCommand dropCommand = connection.CreateCommand();
        dropCommand.CommandText = $"""DROP DATABASE IF EXISTS "{databaseName}";""";
        dropCommand.ExecuteNonQuery();
    }
}
