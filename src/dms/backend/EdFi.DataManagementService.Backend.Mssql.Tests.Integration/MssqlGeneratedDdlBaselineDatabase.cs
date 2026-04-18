// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
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
    private bool _disposed;

    private MssqlGeneratedDdlBaselineDatabase(
        string fixtureSignature,
        SharedBaselineEntry sharedBaselineEntry
    )
    {
        FixtureSignature = fixtureSignature;
        _sharedBaselineEntry = sharedBaselineEntry;
    }

    public string FixtureSignature { get; }

    public static Task<MssqlGeneratedDdlBaselineDatabase> CreateAsync(
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

                sharedBaselineEntry.ManagerHandleCount++;
            }
            else
            {
                sharedBaselineEntry = new(generatedDdlHash, generatedDdl, commandTimeoutSeconds);
                _sharedBaselines[fixtureSignature] = sharedBaselineEntry;
            }
        }

        return Task.FromResult(new MssqlGeneratedDdlBaselineDatabase(fixtureSignature, sharedBaselineEntry));
    }

    public async Task<MssqlGeneratedDdlBaselineLease> AcquireRestoredDatabaseAsync(
        int? commandTimeoutSeconds = null
    )
    {
        var resolvedCommandTimeoutSeconds =
            commandTimeoutSeconds ?? _sharedBaselineEntry.SlotCommandTimeoutSeconds;
        SharedBaselineSlot? leasedSlot = null;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sharedBaselineEntry.ActiveSlotLeaseCount++;

            if (_sharedBaselineEntry.IdleSlots.Count != 0)
            {
                leasedSlot = _sharedBaselineEntry.IdleSlots.Pop();
            }
        }

        try
        {
            if (leasedSlot is null)
            {
                leasedSlot = await CreateSharedBaselineSlotAsync(
                    _sharedBaselineEntry.GeneratedDdl,
                    resolvedCommandTimeoutSeconds
                );
            }
            else
            {
                await RestoreSnapshotAsync(
                    leasedSlot.Database.DatabaseName,
                    leasedSlot.SnapshotName,
                    resolvedCommandTimeoutSeconds
                );
            }

            var acquiredSlot = leasedSlot;

            return new(acquiredSlot.Database, acquiredSlot.SnapshotName, () => ReturnSlotAsync(acquiredSlot));
        }
        catch
        {
            await ReleaseFaultedSlotAsync(leasedSlot);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeSlotsAsync(ReleaseManagerHandle());
    }

    private List<SharedBaselineSlot> ReleaseManagerHandle()
    {
        lock (_sync)
        {
            if (
                !_sharedBaselines.TryGetValue(FixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, _sharedBaselineEntry)
            )
            {
                return [];
            }

            _sharedBaselineEntry.ManagerHandleCount--;
            return CollectDisposableIdleSlotsIfUnused();
        }
    }

    private async ValueTask ReturnSlotAsync(SharedBaselineSlot slot)
    {
        await DisposeSlotsAsync(ReleaseSlot(slot));
    }

    private async ValueTask ReleaseFaultedSlotAsync(SharedBaselineSlot? slot)
    {
        await DisposeSlotsAsync(ReleaseFaultedSlot(slot));
    }

    private List<SharedBaselineSlot> ReleaseSlot(SharedBaselineSlot slot)
    {
        lock (_sync)
        {
            if (
                !_sharedBaselines.TryGetValue(FixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, _sharedBaselineEntry)
            )
            {
                return [slot];
            }

            _sharedBaselineEntry.ActiveSlotLeaseCount--;

            if (
                _sharedBaselineEntry.ManagerHandleCount == 0
                && _sharedBaselineEntry.ActiveSlotLeaseCount == 0
            )
            {
                List<SharedBaselineSlot> slotsToDispose = [slot, .. _sharedBaselineEntry.IdleSlots];
                _sharedBaselineEntry.IdleSlots.Clear();
                _sharedBaselines.Remove(FixtureSignature);
                return slotsToDispose;
            }

            _sharedBaselineEntry.IdleSlots.Push(slot);
            return [];
        }
    }

    private List<SharedBaselineSlot> ReleaseFaultedSlot(SharedBaselineSlot? slot)
    {
        lock (_sync)
        {
            List<SharedBaselineSlot> slotsToDispose = slot is not null ? [slot] : [];

            if (
                !_sharedBaselines.TryGetValue(FixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, _sharedBaselineEntry)
            )
            {
                return slotsToDispose;
            }

            _sharedBaselineEntry.ActiveSlotLeaseCount--;
            slotsToDispose.AddRange(CollectDisposableIdleSlotsIfUnused());
            return slotsToDispose;
        }
    }

    private List<SharedBaselineSlot> CollectDisposableIdleSlotsIfUnused()
    {
        if (_sharedBaselineEntry.ManagerHandleCount != 0 || _sharedBaselineEntry.ActiveSlotLeaseCount != 0)
        {
            return [];
        }

        List<SharedBaselineSlot> slotsToDispose = [.. _sharedBaselineEntry.IdleSlots];
        _sharedBaselineEntry.IdleSlots.Clear();
        _sharedBaselines.Remove(FixtureSignature);
        return slotsToDispose;
    }

    private static async Task<SharedBaselineSlot> CreateSharedBaselineSlotAsync(
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
            var snapshotFileName = $"{databaseName}_baseline_{fileId}_{SanitizeFileName(logicalName)}.ss";
            var directoryPath =
                Path.GetDirectoryName(physicalName)
                ?? throw new InvalidOperationException(
                    $"Could not determine the data file directory for database '{databaseName}'."
                );
            var snapshotPath = Path.Combine(directoryPath, snapshotFileName);

            files.Add(new(LogicalName: logicalName, SnapshotPath: snapshotPath));
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

    private static async ValueTask DisposeSlotsAsync(IEnumerable<SharedBaselineSlot> slots)
    {
        List<Exception> exceptions = [];

        foreach (var slot in slots)
        {
            try
            {
                await slot.DisposeAsync();
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        if (exceptions.Count == 0)
        {
            return;
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }

        throw new AggregateException(exceptions);
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
        string generatedDdl,
        int slotCommandTimeoutSeconds
    )
    {
        public string GeneratedDdlHash { get; } = generatedDdlHash;

        public string GeneratedDdl { get; } = generatedDdl;

        public int SlotCommandTimeoutSeconds { get; } = slotCommandTimeoutSeconds;

        public int ManagerHandleCount { get; set; } = 1;

        public int ActiveSlotLeaseCount { get; set; }

        public Stack<SharedBaselineSlot> IdleSlots { get; } = new();
    }

    private sealed class SharedBaselineSlot(string snapshotName, MssqlGeneratedDdlTestDatabase database)
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

internal sealed class MssqlGeneratedDdlBaselineLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _releaseLeaseAsync;
    private bool _disposed;

    public MssqlGeneratedDdlBaselineLease(
        MssqlGeneratedDdlTestDatabase database,
        string snapshotName,
        Func<ValueTask> releaseLeaseAsync
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotName);
        ArgumentNullException.ThrowIfNull(releaseLeaseAsync);

        Database = database;
        SnapshotName = snapshotName;
        _releaseLeaseAsync = releaseLeaseAsync;
    }

    public MssqlGeneratedDdlTestDatabase Database { get; }

    public string SnapshotName { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _releaseLeaseAsync();
    }
}
