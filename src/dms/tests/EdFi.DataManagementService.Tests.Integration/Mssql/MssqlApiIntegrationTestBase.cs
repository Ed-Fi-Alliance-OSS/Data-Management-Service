// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Mssql;

/// <summary>
/// SQL Server-flavored concrete <see cref="ApiIntegrationTestBase"/>. Acquires a
/// strategy-selected per-test database lease from the cached
/// <see cref="IMssqlGeneratedDdlBaselineDatabase"/> for the bound fixture and hands
/// its connection string to the harness.
/// </summary>
[Category("MssqlIntegration")]
public abstract class MssqlApiIntegrationTestBase : ApiIntegrationTestBase
{
    private IMssqlGeneratedDdlBaselineLease? _lease;

    protected override string Datastore => "mssql";

    /// <summary>
    /// Applies the snapshot isolation settings production provisioning applies, so a test that
    /// depends on locking behavior exercises the configuration production actually runs under.
    /// These are database-scoped options that live in master rather than in the data pages, so a
    /// snapshot revert does not clear them and the slot pool would otherwise hand the changed
    /// configuration to every later test that leases the same database. Opting in here rather than
    /// in the test keeps the revert paired with the change.
    /// </summary>
    protected virtual bool MatchProductionWriteIsolation => false;

    [OneTimeSetUp]
    public void GuardConnectionStringPresent()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "MssqlAdmin is not configured (set ConnectionStrings__MssqlAdmin or add it to appsettings.Test.json); skipping SQL Server API integration tests."
        );
    }

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        IMssqlGeneratedDdlBaselineDatabase baseline = await MssqlBaselineCache.CreateOrGetAsync(fixture);
        _lease = await baseline.AcquireRestoredDatabaseAsync();
        if (EnableDocumentCacheReadAcceleration || MatchProductionWriteIsolation)
        {
            await SetReadCommittedSnapshotAsync(_lease.Database.DatabaseName, enabled: true);
        }

        if (MatchProductionWriteIsolation)
        {
            await SetAllowSnapshotIsolationAsync(_lease.Database.DatabaseName, enabled: true);
        }

        return _lease.Database.ConnectionString;
    }

    protected override async Task<DbConnection> OpenAssertionConnectionAsync(string leasedConnectionString)
    {
        SqlConnection connection = new(leasedConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    protected override async Task ReleaseDatabaseAsync(string leasedConnectionString)
    {
        if (_lease is not null)
        {
            if (MatchProductionWriteIsolation)
            {
                // Returned to the pooled default before the slot is handed back, so the next test to
                // lease this database sees the isolation configuration it was built with.
                await SetAllowSnapshotIsolationAsync(_lease.Database.DatabaseName, enabled: false);
                await SetReadCommittedSnapshotAsync(
                    _lease.Database.DatabaseName,
                    enabled: EnableDocumentCacheReadAcceleration
                );
            }

            await _lease.DisposeAsync();
            _lease = null;
        }
    }

    private static Task SetReadCommittedSnapshotAsync(string databaseName, bool enabled)
    {
        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
        return MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"ALTER DATABASE {quotedDatabaseName} SET READ_COMMITTED_SNAPSHOT {(enabled ? "ON" : "OFF")} WITH ROLLBACK IMMEDIATE;"
        );
    }

    private static Task SetAllowSnapshotIsolationAsync(string databaseName, bool enabled)
    {
        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
        return MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"ALTER DATABASE {quotedDatabaseName} SET ALLOW_SNAPSHOT_ISOLATION {(enabled ? "ON" : "OFF")};"
        );
    }
}
