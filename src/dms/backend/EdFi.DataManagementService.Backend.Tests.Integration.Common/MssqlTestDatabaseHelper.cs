// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public static class MssqlTestDatabaseHelper
{
    public static bool IsConfigured() => BaselineDatabaseConfiguration.MssqlAdminConnectionString is not null;

    public static string GenerateUniqueDatabaseName()
    {
        return $"dmsfp{Guid.NewGuid():N}"[..24];
    }

    public static string BuildConnectionString(string databaseName)
    {
        SqlConnectionStringBuilder builder = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!)
        {
            InitialCatalog = databaseName,
        };

        return builder.ConnectionString;
    }

    public static void CreateDatabase(string databaseName)
    {
        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        command.CommandText = $"CREATE DATABASE {quotedDatabaseName}";
        command.ExecuteNonQuery();
    }

    public static async Task ExecuteAdminNonQueryAsync(string sql, int commandTimeoutSeconds = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    public static void DropDatabaseIfExists(string databaseName)
    {
        SqlConnection.ClearAllPools();

        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        var escapedDatabaseName = EscapeSqlLiteral(databaseName);
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

    public static string QuoteIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static string EscapeSqlLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    public static string BuildSiblingFilePath(string physicalName, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var lastForwardSlashIndex = physicalName.LastIndexOf('/');
        var lastBackslashIndex = physicalName.LastIndexOf('\\');
        var lastSeparatorIndex = Math.Max(lastForwardSlashIndex, lastBackslashIndex);

        if (lastSeparatorIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not determine the SQL Server-visible file directory from '{physicalName}'."
            );
        }

        var separator = physicalName[lastSeparatorIndex];

        return lastSeparatorIndex == 0
            ? $"{separator}{fileName}"
            : $"{physicalName[..lastSeparatorIndex]}{separator}{fileName}";
    }

    public static string SanitizeFileNamePart(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new([
            .. value.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'
            ),
        ]);
    }
}
