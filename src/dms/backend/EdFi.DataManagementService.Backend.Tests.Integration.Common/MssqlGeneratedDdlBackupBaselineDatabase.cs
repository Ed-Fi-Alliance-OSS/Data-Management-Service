// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public sealed class MssqlGeneratedDdlBackupBaselineDatabase : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private static readonly object _sync = new();
    private static readonly Dictionary<string, SharedBackupEntry> _sharedBackups = new(
        StringComparer.Ordinal
    );

    private readonly SharedBackupEntry _sharedBackupEntry;
    private bool _disposed;

    private MssqlGeneratedDdlBackupBaselineDatabase(
        string fixtureSignature,
        SharedBackupEntry sharedBackupEntry
    )
    {
        FixtureSignature = fixtureSignature;
        _sharedBackupEntry = sharedBackupEntry;
    }

    public string FixtureSignature { get; }

    public static Task<MssqlGeneratedDdlBackupBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var generatedDdlHash = MssqlProvisioningTimingRecorder.ComputeGeneratedDdlHash(generatedDdl);
        var timingContext = new MssqlProvisioningTimingContext(
            fixtureSignature,
            generatedDdlHash,
            MssqlGeneratedDdlLeaseStrategy.BackupRestore,
            callerMemberName,
            callerFilePath,
            callerLineNumber
        );
        SharedBackupEntry sharedBackupEntry;

        lock (_sync)
        {
            if (_sharedBackups.TryGetValue(fixtureSignature, out var existingEntry))
            {
                sharedBackupEntry = existingEntry;

                if (
                    !string.Equals(
                        sharedBackupEntry.GeneratedDdlHash,
                        generatedDdlHash,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Fixture signature '{fixtureSignature}' is already associated with different generated DDL."
                    );
                }

                sharedBackupEntry.ManagerHandleCount++;
            }
            else
            {
                sharedBackupEntry = new(generatedDdlHash, generatedDdl, commandTimeoutSeconds, timingContext);
                _sharedBackups[fixtureSignature] = sharedBackupEntry;
            }
        }

        return Task.FromResult(
            new MssqlGeneratedDdlBackupBaselineDatabase(fixtureSignature, sharedBackupEntry)
        );
    }

    public async Task<MssqlGeneratedDdlBackupBaselineLease> AcquireRestoredDatabaseAsync(
        int? commandTimeoutSeconds = null
    )
    {
        var resolvedCommandTimeoutSeconds =
            commandTimeoutSeconds ?? _sharedBackupEntry.LeaseCommandTimeoutSeconds;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sharedBackupEntry.ActiveLeaseCount++;
        }

        var databaseName = "";

        try
        {
            MssqlBackupBaselineState backupState = await _sharedBackupEntry.BackupState.Value;
            databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();

            await RestoreBackupAsync(
                _sharedBackupEntry.TimingContext,
                backupState.BackupPath,
                databaseName,
                backupState.Files,
                resolvedCommandTimeoutSeconds
            );

            var restoredDatabase = MssqlGeneratedDdlTestDatabase.AttachExisting(
                databaseName,
                FixtureSignature,
                _sharedBackupEntry.GeneratedDdlHash,
                MssqlGeneratedDdlLeaseStrategy.BackupRestore,
                backupState.ResetPlan
            );

            return new(restoredDatabase, () => ReturnLeaseAsync(restoredDatabase));
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                MssqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
            }

            ReleaseActiveLease();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        ReleaseManagerHandle();
        return ValueTask.CompletedTask;
    }

    public static async Task<IReadOnlyList<MssqlDatabaseFileMetadata>> ReadDatabaseFilesAsync(
        string databaseName,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        const string sql = """
            SELECT [file_id], [name], [type_desc], [physical_name]
            FROM sys.master_files
            WHERE [database_id] = DB_ID(@databaseName)
            ORDER BY [file_id];
            """;

        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@databaseName", databaseName));

        List<MssqlDatabaseFileMetadata> files = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            files.Add(
                new(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2).Equals("LOG", StringComparison.OrdinalIgnoreCase) ? "L" : "D",
                    reader.GetString(3)
                )
            );
        }

        return files.Count != 0
            ? files
            : throw new InvalidOperationException(
                $"Could not locate SQL Server database files for '{databaseName}'."
            );
    }

    private async ValueTask ReturnLeaseAsync(MssqlGeneratedDdlTestDatabase database)
    {
        try
        {
            await database.DisposeAsync();
        }
        finally
        {
            ReleaseActiveLease();
        }
    }

    private void ReleaseManagerHandle()
    {
        lock (_sync)
        {
            if (
                !_sharedBackups.TryGetValue(FixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, _sharedBackupEntry)
            )
            {
                return;
            }

            _sharedBackupEntry.ManagerHandleCount--;
            RemoveSharedEntryIfUnused();
        }
    }

    private void ReleaseActiveLease()
    {
        lock (_sync)
        {
            if (
                !_sharedBackups.TryGetValue(FixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, _sharedBackupEntry)
            )
            {
                return;
            }

            _sharedBackupEntry.ActiveLeaseCount--;
            RemoveSharedEntryIfUnused();
        }
    }

    private void RemoveSharedEntryIfUnused()
    {
        if (_sharedBackupEntry.ManagerHandleCount != 0 || _sharedBackupEntry.ActiveLeaseCount != 0)
        {
            return;
        }

        // SQL Server has no safe file-delete primitive here; backup files live in the container
        // data directory and are cleaned up when the local or CI SQL Server container is removed.
        _sharedBackups.Remove(FixtureSignature);
    }

    private static async Task RestoreBackupAsync(
        MssqlProvisioningTimingContext context,
        string backupPath,
        string databaseName,
        IReadOnlyList<MssqlDatabaseFileMetadata> files,
        int commandTimeoutSeconds
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "Succeeded";

        try
        {
            SqlConnection.ClearAllPools();

            List<string> restoreOptions = ["CHECKSUM"];
            restoreOptions.AddRange(
                files.Select(file =>
                    $"MOVE N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(file.LogicalName)}' TO N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(file.BuildRestorePath(databaseName))}'"
                )
            );
            restoreOptions.Add("RECOVERY");
            restoreOptions.Add("REPLACE");

            var sql = $"""
                RESTORE DATABASE {MssqlTestDatabaseHelper.QuoteIdentifier(databaseName)}
                FROM DISK = N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(backupPath)}'
                WITH {string.Join("," + Environment.NewLine + "    ", restoreOptions)};
                """;

            await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(sql, commandTimeoutSeconds);
        }
        catch
        {
            outcome = "Failed";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                databaseName,
                commandTimeoutSeconds,
                context.FixtureSignature,
                context.GeneratedDdlHash,
                "restore-backup",
                context.LeaseStrategy,
                context.CallerMemberName,
                context.CallerFilePath,
                context.CallerLineNumber
            );
        }
    }

    private sealed class SharedBackupEntry
    {
        private readonly string _generatedDdl;

        public SharedBackupEntry(
            string generatedDdlHash,
            string generatedDdl,
            int leaseCommandTimeoutSeconds,
            MssqlProvisioningTimingContext timingContext
        )
        {
            GeneratedDdlHash = generatedDdlHash;
            _generatedDdl = generatedDdl;
            LeaseCommandTimeoutSeconds = leaseCommandTimeoutSeconds;
            TimingContext = timingContext;
            BackupState = new(BuildBackupBaselineStateAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string GeneratedDdlHash { get; }

        public int LeaseCommandTimeoutSeconds { get; }

        public MssqlProvisioningTimingContext TimingContext { get; }

        public Lazy<Task<MssqlBackupBaselineState>> BackupState { get; }

        public int ManagerHandleCount { get; set; } = 1;

        public int ActiveLeaseCount { get; set; }

        private async Task<MssqlBackupBaselineState> BuildBackupBaselineStateAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var outcome = "Succeeded";
            var databaseName = "";
            MssqlGeneratedDdlTestDatabase? baselineDatabase = null;
            MssqlBackupBaselineState backupBaselineState;

            try
            {
                baselineDatabase = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
                    _generatedDdl,
                    LeaseCommandTimeoutSeconds,
                    TimingContext
                );
                databaseName = baselineDatabase.DatabaseName;

                await ApplyBackupReadyDatabaseOptionsAsync(databaseName, LeaseCommandTimeoutSeconds);
                IReadOnlyList<MssqlDatabaseFileMetadata> databaseFiles = await ReadDatabaseFilesAsync(
                    databaseName,
                    LeaseCommandTimeoutSeconds
                );
                MssqlDatabaseFileMetadata dataFile = databaseFiles.First(file =>
                    file.Type.Equals("D", StringComparison.OrdinalIgnoreCase)
                );
                var backupPath = MssqlDatabaseFileMetadata.BuildBackupPath(
                    databaseName,
                    dataFile.PhysicalName
                );

                await BackupDatabaseAsync(databaseName, backupPath, LeaseCommandTimeoutSeconds);
                IReadOnlyList<MssqlDatabaseFileMetadata> backupFiles = await ReadBackupFileListAsync(
                    backupPath,
                    LeaseCommandTimeoutSeconds
                );

                backupBaselineState = new(backupPath, backupFiles, baselineDatabase.ResetPlan);
            }
            catch
            {
                outcome = "Failed";
                throw;
            }
            finally
            {
                stopwatch.Stop();
                MssqlProvisioningTimingRecorder.Record(
                    outcome,
                    stopwatch.Elapsed,
                    databaseName,
                    LeaseCommandTimeoutSeconds,
                    TimingContext.FixtureSignature,
                    TimingContext.GeneratedDdlHash,
                    "backup-baseline",
                    TimingContext.LeaseStrategy,
                    TimingContext.CallerMemberName,
                    TimingContext.CallerFilePath,
                    TimingContext.CallerLineNumber
                );

                if (baselineDatabase is not null)
                {
                    await baselineDatabase.DisposeAsync();
                }
            }

            return backupBaselineState;
        }

        private static Task ApplyBackupReadyDatabaseOptionsAsync(
            string databaseName,
            int commandTimeoutSeconds
        )
        {
            var quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
            var sql = $"""
                ALTER DATABASE {quotedDatabaseName} SET RECOVERY SIMPLE;
                ALTER DATABASE {quotedDatabaseName} SET AUTO_CLOSE OFF;
                ALTER DATABASE {quotedDatabaseName} SET AUTO_SHRINK OFF;
                USE {quotedDatabaseName};
                CHECKPOINT;
                """;

            return MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(sql, commandTimeoutSeconds);
        }

        private static Task BackupDatabaseAsync(
            string databaseName,
            string backupPath,
            int commandTimeoutSeconds
        )
        {
            var quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
            var escapedBackupPath = MssqlTestDatabaseHelper.EscapeSqlLiteral(backupPath);
            var escapedBackupName = MssqlTestDatabaseHelper.EscapeSqlLiteral(
                $"{databaseName} generated-DDL baseline"
            );
            var sql = $"""
                BACKUP DATABASE {quotedDatabaseName}
                TO DISK = N'{escapedBackupPath}'
                WITH COPY_ONLY, CHECKSUM, COMPRESSION, FORMAT, INIT, NAME = N'{escapedBackupName}';
                """;

            return MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(sql, commandTimeoutSeconds);
        }

        private static async Task<IReadOnlyList<MssqlDatabaseFileMetadata>> ReadBackupFileListAsync(
            string backupPath,
            int commandTimeoutSeconds
        )
        {
            var sql = $"""
                RESTORE FILELISTONLY
                FROM DISK = N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(backupPath)}';
                """;

            await using SqlConnection connection = new(
                BaselineDatabaseConfiguration.MssqlAdminConnectionString!
            );
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = commandTimeoutSeconds;

            List<MssqlDatabaseFileMetadata> files = [];
            await using var reader = await command.ExecuteReaderAsync();
            var fileIdOrdinal = reader.GetOrdinal("FileId");
            var logicalNameOrdinal = reader.GetOrdinal("LogicalName");
            var typeOrdinal = reader.GetOrdinal("Type");
            var physicalNameOrdinal = reader.GetOrdinal("PhysicalName");

            while (await reader.ReadAsync())
            {
                files.Add(
                    new(
                        Convert.ToInt32(reader.GetValue(fileIdOrdinal), CultureInfo.InvariantCulture),
                        reader.GetString(logicalNameOrdinal),
                        reader.GetString(typeOrdinal),
                        reader.GetString(physicalNameOrdinal)
                    )
                );
            }

            return files.Count != 0
                ? files
                : throw new InvalidOperationException(
                    $"Could not locate SQL Server backup file metadata for '{backupPath}'."
                );
        }
    }

    private sealed record MssqlBackupBaselineState(
        string BackupPath,
        IReadOnlyList<MssqlDatabaseFileMetadata> Files,
        MssqlDatabaseResetPlan ResetPlan
    );
}

public sealed class MssqlGeneratedDdlBackupBaselineLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _releaseLeaseAsync;
    private bool _disposed;

    public MssqlGeneratedDdlBackupBaselineLease(
        MssqlGeneratedDdlTestDatabase database,
        Func<ValueTask> releaseLeaseAsync
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(releaseLeaseAsync);

        Database = database;
        _releaseLeaseAsync = releaseLeaseAsync;
    }

    public MssqlGeneratedDdlTestDatabase Database { get; }

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
