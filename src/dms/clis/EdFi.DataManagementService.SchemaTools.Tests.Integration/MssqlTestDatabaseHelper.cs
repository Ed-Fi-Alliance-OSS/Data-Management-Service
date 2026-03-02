// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

public static class MssqlTestDatabaseHelper
{
    /// <summary>
    /// Returns true if a MssqlAdmin connection string is configured in appsettings.
    /// MSSQL integration tests are opt-in: configure MssqlAdmin in appsettings.Test.json.
    /// </summary>
    public static bool IsConfigured() => DatabaseConfiguration.MssqlAdminConnectionString is not null;

    public static string GenerateUniqueDatabaseName()
    {
        return $"dms_test_{Guid.NewGuid():N}"[..24];
    }

    public static string BuildConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(DatabaseConfiguration.MssqlAdminConnectionString!)
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }

    public static void CreateDatabase(string databaseName)
    {
        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        // Database names are generated internally (GUID-based), safe to interpolate
        using var command = connection.CreateCommand();
        var quotedName = $"[{databaseName.Replace("]", "]]")}]";
        command.CommandText = $"CREATE DATABASE {quotedName}";
        command.ExecuteNonQuery();
    }

    public static void DropDatabaseIfExists(string databaseName)
    {
        // Clear all SqlClient connection pools to release any held connections
        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        // Database names are generated internally (GUID-based), safe to interpolate
        var quotedName = $"[{databaseName.Replace("]", "]]")}]";

        // Force-close active connections then drop the database
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE name = '{databaseName}')
            BEGIN
                ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quotedName};
            END
            """;
        command.ExecuteNonQuery();
    }
}
