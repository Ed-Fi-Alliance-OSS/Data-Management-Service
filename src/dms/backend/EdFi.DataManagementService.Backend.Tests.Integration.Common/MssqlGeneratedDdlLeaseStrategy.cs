// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public static class MssqlGeneratedDdlLeaseStrategy
{
    public const string EnvironmentVariableName = "MSSQL_GENERATED_DDL_LEASE_STRATEGY";
    public const string SnapshotSlot = "snapshot-slot";
    public const string BackupRestore = "backup-restore";

    private static readonly string[] _supportedValues = [SnapshotSlot, BackupRestore];

    public static string FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    public static string Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SnapshotSlot;
        }

        var trimmedValue = value.Trim();

        if (trimmedValue.Equals(SnapshotSlot, StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotSlot;
        }

        if (trimmedValue.Equals(BackupRestore, StringComparison.OrdinalIgnoreCase))
        {
            return BackupRestore;
        }

        throw new InvalidOperationException(
            $"Unsupported {EnvironmentVariableName} value '{trimmedValue}'. Supported values are: {string.Join(", ", _supportedValues)}."
        );
    }
}

public interface IMssqlGeneratedDdlBaselineDatabase : IAsyncDisposable
{
    string FixtureSignature { get; }

    string LeaseStrategy { get; }

    Task<IMssqlGeneratedDdlBaselineLease> AcquireRestoredDatabaseAsync(int? commandTimeoutSeconds = null);
}

public interface IMssqlGeneratedDdlBaselineLease : IAsyncDisposable
{
    MssqlGeneratedDdlTestDatabase Database { get; }

    string LeaseStrategy { get; }
}

public static class MssqlGeneratedDdlBaselineDatabaseFactory
{
    private const int DefaultCommandTimeoutSeconds = 300;

    public static async Task<IMssqlGeneratedDdlBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        var leaseStrategy = MssqlGeneratedDdlLeaseStrategy.FromEnvironment();

        return leaseStrategy switch
        {
            MssqlGeneratedDdlLeaseStrategy.SnapshotSlot => new SnapshotBaselineDatabaseAdapter(
                await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
                    fixtureSignature,
                    generatedDdl,
                    commandTimeoutSeconds,
                    callerMemberName,
                    callerFilePath,
                    callerLineNumber
                )
            ),
            MssqlGeneratedDdlLeaseStrategy.BackupRestore => new BackupBaselineDatabaseAdapter(
                await MssqlGeneratedDdlBackupBaselineDatabase.CreateAsync(
                    fixtureSignature,
                    generatedDdl,
                    commandTimeoutSeconds,
                    callerMemberName,
                    callerFilePath,
                    callerLineNumber
                )
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported generated-DDL lease strategy '{leaseStrategy}'."
            ),
        };
    }

    private sealed class SnapshotBaselineDatabaseAdapter(MssqlGeneratedDdlBaselineDatabase baselineDatabase)
        : IMssqlGeneratedDdlBaselineDatabase
    {
        public string FixtureSignature => baselineDatabase.FixtureSignature;

        public string LeaseStrategy => MssqlGeneratedDdlLeaseStrategy.SnapshotSlot;

        public async Task<IMssqlGeneratedDdlBaselineLease> AcquireRestoredDatabaseAsync(
            int? commandTimeoutSeconds = null
        )
        {
            MssqlGeneratedDdlBaselineLease lease = await baselineDatabase.AcquireRestoredDatabaseAsync(
                commandTimeoutSeconds
            );
            return new BaselineLeaseAdapter(lease.Database, LeaseStrategy, lease.DisposeAsync);
        }

        public ValueTask DisposeAsync() => baselineDatabase.DisposeAsync();
    }

    private sealed class BackupBaselineDatabaseAdapter(
        MssqlGeneratedDdlBackupBaselineDatabase baselineDatabase
    ) : IMssqlGeneratedDdlBaselineDatabase
    {
        public string FixtureSignature => baselineDatabase.FixtureSignature;

        public string LeaseStrategy => MssqlGeneratedDdlLeaseStrategy.BackupRestore;

        public async Task<IMssqlGeneratedDdlBaselineLease> AcquireRestoredDatabaseAsync(
            int? commandTimeoutSeconds = null
        )
        {
            MssqlGeneratedDdlBackupBaselineLease lease = await baselineDatabase.AcquireRestoredDatabaseAsync(
                commandTimeoutSeconds
            );
            return new BaselineLeaseAdapter(lease.Database, LeaseStrategy, lease.DisposeAsync);
        }

        public ValueTask DisposeAsync() => baselineDatabase.DisposeAsync();
    }

    private sealed class BaselineLeaseAdapter(
        MssqlGeneratedDdlTestDatabase database,
        string leaseStrategy,
        Func<ValueTask> releaseLeaseAsync
    ) : IMssqlGeneratedDdlBaselineLease
    {
        private bool _disposed;

        public MssqlGeneratedDdlTestDatabase Database { get; } = database;

        public string LeaseStrategy { get; } = leaseStrategy;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await releaseLeaseAsync();
        }
    }
}
