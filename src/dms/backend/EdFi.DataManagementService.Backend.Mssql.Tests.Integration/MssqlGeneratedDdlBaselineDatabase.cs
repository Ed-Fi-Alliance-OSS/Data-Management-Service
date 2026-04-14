// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed class MssqlGeneratedDdlBaselineDatabase : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private bool _disposed;

    private MssqlGeneratedDdlBaselineDatabase(
        string fixtureSignature,
        string snapshotName,
        MssqlGeneratedDdlTestDatabase database
    )
    {
        FixtureSignature = fixtureSignature;
        SnapshotName = snapshotName;
        Database = database;
    }

    public string FixtureSignature { get; }

    public string SnapshotName { get; }

    public MssqlGeneratedDdlTestDatabase Database { get; }

    public static async Task<MssqlGeneratedDdlBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            generatedDdl,
            commandTimeoutSeconds
        );
        var snapshotName = $"{database.DatabaseName}_baseline";

        try
        {
            await DropSnapshotIfExistsAsync(snapshotName, commandTimeoutSeconds);
            await CreateSnapshotAsync(database.DatabaseName, snapshotName, commandTimeoutSeconds);

            return new(fixtureSignature, snapshotName, database);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task<MssqlGeneratedDdlTestDatabase> RestoreAsync(
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await RestoreSnapshotAsync(Database.DatabaseName, SnapshotName, commandTimeoutSeconds);
        return Database;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await DropSnapshotIfExistsAsync(SnapshotName, DefaultCommandTimeoutSeconds);
        }
        finally
        {
            await Database.DisposeAsync();
        }
    }

    private static async Task CreateSnapshotAsync(
        string databaseName,
        string snapshotName,
        int commandTimeoutSeconds
    )
    {
        var snapshotFiles = await GetSnapshotFilesAsync(databaseName);
        var snapshotFileDefinitions = string.Join(
            "," + Environment.NewLine,
            snapshotFiles.Select(file =>
                $"""    ( NAME = N'{EscapeSqlLiteral(file.LogicalName)}', FILENAME = N'{EscapeSqlLiteral(file.SnapshotPath)}' )"""
            )
        );
        var sql = $"""
            CREATE DATABASE {QuoteIdentifier(snapshotName)}
            ON
            {snapshotFileDefinitions}
            AS SNAPSHOT OF {QuoteIdentifier(databaseName)};
            """;

        await ExecuteAdminNonQueryAsync(sql, commandTimeoutSeconds);
    }

    private static async Task RestoreSnapshotAsync(
        string databaseName,
        string snapshotName,
        int commandTimeoutSeconds
    )
    {
        SqlConnection.ClearAllPools();

        var sql = $"""
            BEGIN TRY
                ALTER DATABASE {QuoteIdentifier(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                RESTORE DATABASE {QuoteIdentifier(databaseName)} FROM DATABASE_SNAPSHOT = N'{EscapeSqlLiteral(
                snapshotName
            )}';
                ALTER DATABASE {QuoteIdentifier(databaseName)} SET MULTI_USER;
            END TRY
            BEGIN CATCH
                BEGIN TRY
                    ALTER DATABASE {QuoteIdentifier(databaseName)} SET MULTI_USER;
                END TRY
                BEGIN CATCH
                END CATCH;

                THROW;
            END CATCH;
            """;

        await ExecuteAdminNonQueryAsync(sql, commandTimeoutSeconds);
    }

    private static Task DropSnapshotIfExistsAsync(string snapshotName, int commandTimeoutSeconds)
    {
        var sql = $"""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE [name] = N'{EscapeSqlLiteral(snapshotName)}')
            BEGIN
                DROP DATABASE {QuoteIdentifier(snapshotName)};
            END
            """;

        return ExecuteAdminNonQueryAsync(sql, commandTimeoutSeconds);
    }

    private static async Task<IReadOnlyList<MssqlSnapshotSourceFile>> GetSnapshotFilesAsync(
        string databaseName
    )
    {
        const string sql = """
            SELECT [file_id], [name], [physical_name]
            FROM sys.master_files
            WHERE [database_id] = DB_ID(@databaseName)
              AND [type_desc] = N'ROWS'
            ORDER BY [file_id];
            """;

        await using SqlConnection connection = new(Configuration.MssqlAdminConnectionString!);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@databaseName", databaseName));

        List<MssqlSnapshotSourceFile> files = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var fileId = reader.GetInt32(0);
            var logicalName = reader.GetString(1);
            var physicalName = reader.GetString(2);
            var directoryPath =
                Path.GetDirectoryName(physicalName)
                ?? throw new InvalidOperationException(
                    $"Could not determine the data file directory for database '{databaseName}'."
                );

            files.Add(
                new(
                    LogicalName: logicalName,
                    SnapshotPath: Path.Combine(
                        directoryPath,
                        $"{databaseName}_baseline_{fileId}_{SanitizeFileName(logicalName)}.ss"
                    )
                )
            );
        }

        return files.Count != 0
            ? files
            : throw new InvalidOperationException(
                $"Could not locate data files for SQL Server database '{databaseName}'."
            );
    }

    private static async Task ExecuteAdminNonQueryAsync(string sql, int commandTimeoutSeconds)
    {
        await using SqlConnection connection = new(Configuration.MssqlAdminConnectionString!);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value)
    {
        return $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string SanitizeFileName(string value)
    {
        return new([
            .. value.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'
            ),
        ]);
    }

    private sealed record MssqlSnapshotSourceFile(string LogicalName, string SnapshotPath);
}
