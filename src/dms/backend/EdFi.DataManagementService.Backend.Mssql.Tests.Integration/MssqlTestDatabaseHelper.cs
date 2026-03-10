// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

public static class MssqlTestDatabaseHelper
{
    public static bool IsConfigured() => Configuration.MssqlAdminConnectionString is not null;

    public static string GenerateUniqueDatabaseName()
    {
        return $"dmsfp{Guid.NewGuid():N}"[..24];
    }

    public static string BuildConnectionString(string databaseName)
    {
        SqlConnectionStringBuilder builder = new(Configuration.MssqlAdminConnectionString!)
        {
            InitialCatalog = databaseName,
        };

        return builder.ConnectionString;
    }

    public static void CreateDatabase(string databaseName)
    {
        using SqlConnection connection = new(Configuration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        command.CommandText = $"CREATE DATABASE {quotedDatabaseName}";
        command.ExecuteNonQuery();
    }

    public static void DropDatabaseIfExists(string databaseName)
    {
        SqlConnection.ClearAllPools();

        using SqlConnection connection = new(Configuration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        var escapedDatabaseName = databaseName.Replace("'", "''");
        var quotedDatabaseName = QuoteIdentifier(databaseName);

        command.CommandText = $"""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{escapedDatabaseName}')
            BEGIN
                ALTER DATABASE {quotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quotedDatabaseName};
            END
            """;

        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string value)
    {
        return $"[{value.Replace("]", "]]")}]";
    }
}
