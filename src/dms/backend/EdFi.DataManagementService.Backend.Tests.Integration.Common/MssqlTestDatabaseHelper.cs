// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public static class MssqlTestDatabaseHelper
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private const int GeneratedDdlDataFileSizeMb = 256;
    private const int GeneratedDdlDataFileGrowthMb = 256;
    private const int GeneratedDdlLogFileSizeMb = 128;
    private const int GeneratedDdlLogFileGrowthMb = 128;

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
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.ExecuteNonQuery();
    }

    public static void CreateGeneratedDdlDatabase(string databaseName, bool useExplicitFileSizing = false)
    {
        if (useExplicitFileSizing)
        {
            CreateDatabaseWithGeneratedDdlFileLayout(databaseName);
        }
        else
        {
            CreateDatabase(databaseName);
        }

        ApplyGeneratedDdlDatabaseOptions(databaseName);
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

    private static void CreateDatabaseWithGeneratedDdlFileLayout(string databaseName)
    {
        var filePaths = BuildGeneratedDdlDatabaseFilePaths(databaseName);
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        var escapedDataLogicalName = EscapeSqlLiteral(databaseName);
        var escapedLogLogicalName = EscapeSqlLiteral($"{databaseName}_log");
        var escapedDataFilePath = EscapeSqlLiteral(filePaths.DataFilePath);
        var escapedLogFilePath = EscapeSqlLiteral(filePaths.LogFilePath);

        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.CommandText = $"""
            CREATE DATABASE {quotedDatabaseName}
            ON PRIMARY
            (
                NAME = N'{escapedDataLogicalName}',
                FILENAME = N'{escapedDataFilePath}',
                SIZE = {GeneratedDdlDataFileSizeMb}MB,
                FILEGROWTH = {GeneratedDdlDataFileGrowthMb}MB
            )
            LOG ON
            (
                NAME = N'{escapedLogLogicalName}',
                FILENAME = N'{escapedLogFilePath}',
                SIZE = {GeneratedDdlLogFileSizeMb}MB,
                FILEGROWTH = {GeneratedDdlLogFileGrowthMb}MB
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void ApplyGeneratedDdlDatabaseOptions(string databaseName)
    {
        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.CommandText = $"""
            ALTER DATABASE {quotedDatabaseName} SET RECOVERY SIMPLE;
            ALTER DATABASE {quotedDatabaseName} SET AUTO_CLOSE OFF;
            ALTER DATABASE {quotedDatabaseName} SET AUTO_SHRINK OFF;
            """;
        command.ExecuteNonQuery();
    }

    private static MssqlGeneratedDdlDatabaseFilePaths BuildGeneratedDdlDatabaseFilePaths(string databaseName)
    {
        var masterFilePaths = ReadMasterDatabaseFilePaths();

        return new(
            BuildSiblingFilePath(masterFilePaths.DataFilePath, $"{databaseName}.mdf"),
            BuildSiblingFilePath(masterFilePaths.LogFilePath, $"{databaseName}_log.ldf")
        );
    }

    private static MssqlGeneratedDdlDatabaseFilePaths ReadMasterDatabaseFilePaths()
    {
        const string sql = """
            SELECT [type_desc], [physical_name]
            FROM sys.master_files
            WHERE [database_id] = DB_ID(N'master')
              AND [type_desc] IN (N'ROWS', N'LOG');
            """;

        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = DefaultCommandTimeoutSeconds;

        using var reader = command.ExecuteReader();
        string? dataFilePath = null;
        string? logFilePath = null;

        while (reader.Read())
        {
            var type = reader.GetString(0);
            var physicalName = reader.GetString(1);

            if (type.Equals("ROWS", StringComparison.OrdinalIgnoreCase))
            {
                dataFilePath = physicalName;
            }
            else if (type.Equals("LOG", StringComparison.OrdinalIgnoreCase))
            {
                logFilePath = physicalName;
            }
        }

        return dataFilePath is not null && logFilePath is not null
            ? new(dataFilePath, logFilePath)
            : throw new InvalidOperationException("Could not locate SQL Server master data and log files.");
    }

    private sealed record MssqlGeneratedDdlDatabaseFilePaths(string DataFilePath, string LogFilePath);
}
