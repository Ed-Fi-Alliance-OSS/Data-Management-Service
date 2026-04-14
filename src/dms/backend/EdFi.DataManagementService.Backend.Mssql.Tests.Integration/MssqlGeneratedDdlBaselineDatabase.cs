// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed class MssqlGeneratedDdlBaselineDatabase : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private static readonly object _sync = new();
    private static readonly Dictionary<string, SharedBaselineEntry> _sharedBaselines = new(
        StringComparer.Ordinal
    );

    private readonly SharedBaselineEntry _sharedBaselineEntry;
    private readonly SharedBaselineState _sharedBaselineState;
    private bool _disposed;

    private MssqlGeneratedDdlBaselineDatabase(
        string fixtureSignature,
        SharedBaselineEntry sharedBaselineEntry,
        SharedBaselineState sharedBaselineState
    )
    {
        FixtureSignature = fixtureSignature;
        _sharedBaselineEntry = sharedBaselineEntry;
        _sharedBaselineState = sharedBaselineState;
    }

    public string FixtureSignature { get; }

    public string SnapshotName => _sharedBaselineState.SnapshotName;

    public MssqlGeneratedDdlTestDatabase Database => _sharedBaselineState.Database;

    public static async Task<MssqlGeneratedDdlBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var generatedDdlHash = ComputeGeneratedDdlHash(generatedDdl);
        SharedBaselineEntry sharedBaselineEntry;

        lock (_sync)
        {
            if (_sharedBaselines.TryGetValue(fixtureSignature, out var existingEntry))
            {
                sharedBaselineEntry = existingEntry;

                if (
                    !string.Equals(
                        sharedBaselineEntry.GeneratedDdlHash,
                        generatedDdlHash,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Fixture signature '{fixtureSignature}' is already associated with different generated DDL."
                    );
                }

                sharedBaselineEntry.LeaseCount++;
            }
            else
            {
                sharedBaselineEntry = new(
                    generatedDdlHash,
                    CreateSharedBaselineStateAsync(generatedDdl, commandTimeoutSeconds)
                );
                _sharedBaselines[fixtureSignature] = sharedBaselineEntry;
            }
        }

        try
        {
            var sharedBaselineState = await sharedBaselineEntry.InitializationTask;
            return new(fixtureSignature, sharedBaselineEntry, sharedBaselineState);
        }
        catch
        {
            ReleaseLease(fixtureSignature, sharedBaselineEntry);
            throw;
        }
    }

    public async Task<MssqlGeneratedDdlTestDatabase> RestoreAsync(
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await RestoreSnapshotAsync(
            _sharedBaselineState.Database.DatabaseName,
            _sharedBaselineState.SnapshotName,
            commandTimeoutSeconds
        );
        return _sharedBaselineState.Database;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (ReleaseLease(FixtureSignature, _sharedBaselineEntry))
        {
            await _sharedBaselineState.DisposeAsync();
        }
    }

    private static bool ReleaseLease(string fixtureSignature, SharedBaselineEntry sharedBaselineEntry)
    {
        lock (_sync)
        {
            if (
                !_sharedBaselines.TryGetValue(fixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, sharedBaselineEntry)
            )
            {
                return false;
            }

            sharedBaselineEntry.LeaseCount--;

            if (sharedBaselineEntry.LeaseCount != 0)
            {
                return false;
            }

            _sharedBaselines.Remove(fixtureSignature);
            return true;
        }
    }

    private static async Task<SharedBaselineState> CreateSharedBaselineStateAsync(
        string generatedDdl,
        int commandTimeoutSeconds
    )
    {
        var database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            generatedDdl,
            commandTimeoutSeconds
        );
        var snapshotName = $"{database.DatabaseName}_baseline";

        try
        {
            await DropSnapshotIfExistsAsync(snapshotName, commandTimeoutSeconds);
            await CreateSnapshotAsync(database.DatabaseName, snapshotName, commandTimeoutSeconds);
            return new(snapshotName, database);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static string ComputeGeneratedDdlHash(string generatedDdl)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generatedDdl)));
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

    private sealed class SharedBaselineEntry(
        string generatedDdlHash,
        Task<SharedBaselineState> initializationTask
    )
    {
        public string GeneratedDdlHash { get; } = generatedDdlHash;

        public Task<SharedBaselineState> InitializationTask { get; } = initializationTask;

        public int LeaseCount { get; set; } = 1;
    }

    private sealed class SharedBaselineState(string snapshotName, MssqlGeneratedDdlTestDatabase database)
        : IAsyncDisposable
    {
        public string SnapshotName { get; } = snapshotName;

        public MssqlGeneratedDdlTestDatabase Database { get; } = database;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await DropSnapshotIfExistsAsync(SnapshotName, DefaultCommandTimeoutSeconds);
            }
            finally
            {
                await Database.DisposeAsync();
            }
        }
    }

    private sealed record MssqlSnapshotSourceFile(string LogicalName, string SnapshotPath);
}
