// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public sealed record MssqlRunOwnedDatabase(string Name, string? SourceDatabaseName);

public static class MssqlTestDatabaseHelper
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private const int GeneratedDdlDataFileSizeMb = 256;
    private const int GeneratedDdlDataFileGrowthMb = 256;
    private const int GeneratedDdlLogFileSizeMb = 128;
    private const int GeneratedDdlLogFileGrowthMb = 128;
    private static readonly string _runOwnedDatabasePrefix = $"dmsfp{Guid.NewGuid():N}"[..13];

    public static bool IsConfigured() => BaselineDatabaseConfiguration.MssqlAdminConnectionString is not null;

    public static string RunOwnedDatabasePrefix => _runOwnedDatabasePrefix;

    public static string GenerateUniqueDatabaseName()
    {
        return $"{RunOwnedDatabasePrefix}{Guid.NewGuid():N}"[..24];
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
        CreateDatabaseUnderLifecycleGateAsync(databaseName).GetAwaiter().GetResult();
    }

    public static Task CreateDatabaseUnderLifecycleGateAsync(string databaseName)
    {
        return CreateDatabaseAsync(
            databaseName,
            useExplicitFileSizing: false,
            applyGeneratedDdlOptions: false
        );
    }

    public static void CreateGeneratedDdlDatabase(string databaseName, bool useExplicitFileSizing = false)
    {
        CreateGeneratedDdlDatabaseAsync(databaseName, useExplicitFileSizing).GetAwaiter().GetResult();
    }

    public static Task CreateGeneratedDdlDatabaseAsync(
        string databaseName,
        bool useExplicitFileSizing = false
    )
    {
        return CreateDatabaseAsync(databaseName, useExplicitFileSizing, applyGeneratedDdlOptions: true);
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
        DropDatabaseUnderLifecycleGateAsync(databaseName).GetAwaiter().GetResult();
    }

    public static async Task DropDatabaseUnderLifecycleGateAsync(
        string databaseName,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        SqlConnection.ClearAllPools();

        await MssqlDatabaseLifecycleCoordinator.ExecuteAsync(async connection =>
        {
            IReadOnlyList<string> snapshotNames = await ReadOwnedSnapshotNamesAsync(
                connection,
                databaseName,
                commandTimeoutSeconds
            );

            foreach (var snapshotName in snapshotNames)
            {
                await DropSnapshotIfExistsAsync(connection, snapshotName, commandTimeoutSeconds);
                await VerifyDatabaseDoesNotExistAsync(connection, snapshotName, commandTimeoutSeconds);
            }

            IReadOnlyList<string> remainingSnapshotNames = await ReadOwnedSnapshotNamesAsync(
                connection,
                databaseName,
                commandTimeoutSeconds
            );
            if (remainingSnapshotNames.Count != 0)
            {
                throw new InvalidOperationException(
                    $"SQL Server snapshots owned by database '{databaseName}' remain after teardown: {string.Join(", ", remainingSnapshotNames)}."
                );
            }

            await DropSourceDatabaseIfExistsAsync(connection, databaseName, commandTimeoutSeconds);
            await VerifyDatabaseDoesNotExistAsync(connection, databaseName, commandTimeoutSeconds);
        });
    }

    public static async Task<IReadOnlyList<MssqlRunOwnedDatabase>> ReadRunOwnedDatabasesAsync(
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        const string sql = """
            SELECT databases.[name], source_databases.[name]
            FROM sys.databases databases
            LEFT JOIN sys.databases source_databases
                ON source_databases.[database_id] = databases.[source_database_id]
            WHERE databases.[name] LIKE @databaseNamePrefix + N'%'
            ORDER BY databases.[name];
            """;

        await using SqlConnection connection = CreateMasterConnection();
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@databaseNamePrefix", RunOwnedDatabasePrefix));

        List<MssqlRunOwnedDatabase> databases = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            databases.Add(
                new(reader.GetString(0), await reader.IsDBNullAsync(1) ? null : reader.GetString(1))
            );
        }

        return databases;
    }

    public static async Task<IReadOnlyList<MssqlRunOwnedDatabase>> CleanupRunOwnedDatabasesAsync(
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        IReadOnlyList<MssqlRunOwnedDatabase> leakedDatabases = await ReadRunOwnedDatabasesAsync(
            commandTimeoutSeconds
        );
        List<Exception> cleanupExceptions = [];

        foreach (
            MssqlRunOwnedDatabase sourceDatabase in leakedDatabases.Where(database =>
                database.SourceDatabaseName is null
            )
        )
        {
            try
            {
                await DropDatabaseUnderLifecycleGateAsync(sourceDatabase.Name, commandTimeoutSeconds);
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        IReadOnlyList<MssqlRunOwnedDatabase> remainingDatabases = await ReadRunOwnedDatabasesAsync(
            commandTimeoutSeconds
        );
        if (remainingDatabases.Count != 0)
        {
            cleanupExceptions.Add(
                new InvalidOperationException(
                    $"The SQL Server run-owned database cleanup left databases or snapshots behind: {string.Join(", ", remainingDatabases.Select(database => database.Name))}."
                )
            );
        }

        if (cleanupExceptions.Count != 0)
        {
            MssqlLifecycleExceptionAggregator.Throw(cleanupExceptions);
        }

        return leakedDatabases;
    }

    internal static async Task DropSnapshotIfExistsAsync(
        SqlConnection connection,
        string snapshotName,
        int commandTimeoutSeconds
    )
    {
        var escapedSnapshotName = EscapeSqlLiteral(snapshotName);
        var quotedSnapshotName = QuoteIdentifier(snapshotName);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE [name] = N'{escapedSnapshotName}')
            BEGIN
                DROP DATABASE {quotedSnapshotName};
            END
            """;
        command.CommandTimeout = commandTimeoutSeconds;

        await command.ExecuteNonQueryAsync();
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

    private static async Task CreateDatabaseAsync(
        string databaseName,
        bool useExplicitFileSizing,
        bool applyGeneratedDdlOptions
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        await MssqlDatabaseLifecycleCoordinator.ExecuteAsync(async connection =>
        {
            MssqlGeneratedDdlDatabaseFilePaths? filePaths = useExplicitFileSizing
                ? await BuildGeneratedDdlDatabaseFilePathsAsync(connection, databaseName)
                : null;
            var createDatabaseSql = BuildCreateDatabaseSql(databaseName, filePaths);

            await using (SqlCommand command = connection.CreateCommand())
            {
                command.CommandTimeout = DefaultCommandTimeoutSeconds;
                command.CommandText = createDatabaseSql;
                await command.ExecuteNonQueryAsync();
            }

            if (applyGeneratedDdlOptions)
            {
                await ApplyGeneratedDdlDatabaseOptionsAsync(connection, databaseName);
            }
        });
    }

    private static string BuildCreateDatabaseSql(
        string databaseName,
        MssqlGeneratedDdlDatabaseFilePaths? filePaths
    )
    {
        var escapedDatabaseName = EscapeSqlLiteral(databaseName);
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        if (filePaths is null)
        {
            return $"""
                IF DB_ID(N'{escapedDatabaseName}') IS NULL
                BEGIN
                    CREATE DATABASE {quotedDatabaseName};
                END
                """;
        }

        var escapedDataLogicalName = EscapeSqlLiteral(databaseName);
        var escapedLogLogicalName = EscapeSqlLiteral($"{databaseName}_log");
        var escapedDataFilePath = EscapeSqlLiteral(filePaths.DataFilePath);
        var escapedLogFilePath = EscapeSqlLiteral(filePaths.LogFilePath);

        return $"""
            IF DB_ID(N'{escapedDatabaseName}') IS NULL
            BEGIN
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
            END
            """;
    }

    private static async Task ApplyGeneratedDdlDatabaseOptionsAsync(
        SqlConnection connection,
        string databaseName
    )
    {
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.CommandText = $"""
            ALTER DATABASE {quotedDatabaseName} SET RECOVERY SIMPLE;
            ALTER DATABASE {quotedDatabaseName} SET AUTO_CLOSE OFF;
            ALTER DATABASE {quotedDatabaseName} SET AUTO_SHRINK OFF;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<MssqlGeneratedDdlDatabaseFilePaths> BuildGeneratedDdlDatabaseFilePathsAsync(
        SqlConnection connection,
        string databaseName
    )
    {
        MssqlGeneratedDdlDatabaseFilePaths masterFilePaths = await ReadMasterDatabaseFilePathsAsync(
            connection
        );

        return new(
            BuildSiblingFilePath(masterFilePaths.DataFilePath, $"{databaseName}.mdf"),
            BuildSiblingFilePath(masterFilePaths.LogFilePath, $"{databaseName}_log.ldf")
        );
    }

    private static async Task<MssqlGeneratedDdlDatabaseFilePaths> ReadMasterDatabaseFilePathsAsync(
        SqlConnection connection
    )
    {
        const string sql = """
            SELECT [type_desc], [physical_name]
            FROM sys.master_files
            WHERE [database_id] = DB_ID(N'master')
              AND [type_desc] IN (N'ROWS', N'LOG');
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = DefaultCommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync();
        string? dataFilePath = null;
        string? logFilePath = null;

        while (await reader.ReadAsync())
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

    private static async Task<IReadOnlyList<string>> ReadOwnedSnapshotNamesAsync(
        SqlConnection connection,
        string sourceDatabaseName,
        int commandTimeoutSeconds
    )
    {
        const string sql = """
            SELECT snapshots.[name]
            FROM sys.databases snapshots
            INNER JOIN sys.databases source_databases
                ON source_databases.[database_id] = snapshots.[source_database_id]
            WHERE source_databases.[name] = @sourceDatabaseName
            ORDER BY snapshots.[name];
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@sourceDatabaseName", sourceDatabaseName));

        List<string> snapshotNames = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            snapshotNames.Add(reader.GetString(0));
        }

        return snapshotNames;
    }

    private static async Task DropSourceDatabaseIfExistsAsync(
        SqlConnection connection,
        string databaseName,
        int commandTimeoutSeconds
    )
    {
        var escapedDatabaseName = EscapeSqlLiteral(databaseName);
        var quotedDatabaseName = QuoteIdentifier(databaseName);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE [name] = N'{escapedDatabaseName}')
            BEGIN
                ALTER DATABASE {quotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quotedDatabaseName};
            END
            """;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task VerifyDatabaseDoesNotExistAsync(
        SqlConnection connection,
        string databaseName,
        int commandTimeoutSeconds
    )
    {
        const string sql = """
            SELECT CASE WHEN DB_ID(@databaseName) IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END;
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@databaseName", databaseName));

        if ((bool)(await command.ExecuteScalarAsync() ?? false))
        {
            throw new InvalidOperationException(
                $"SQL Server database or snapshot '{databaseName}' still exists after teardown."
            );
        }
    }

    private static SqlConnection CreateMasterConnection()
    {
        SqlConnectionStringBuilder builder = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!)
        {
            InitialCatalog = "master",
        };

        return new(builder.ConnectionString);
    }

    private sealed record MssqlGeneratedDdlDatabaseFilePaths(string DataFilePath, string LogFilePath);
}
