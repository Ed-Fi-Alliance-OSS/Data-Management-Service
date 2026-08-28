// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Postgresql;

/// <summary>
/// PostgreSQL-flavored concrete <see cref="ApiIntegrationTestBase"/>. Leases an
/// isolated per-test database from the cached
/// <see cref="PostgresqlGeneratedDdlBaselineDatabase"/> for the bound fixture and
/// hands its connection string to the harness.
/// </summary>
[Category("PostgresqlIntegration")]
public abstract class PostgresqlApiIntegrationTestBase : ApiIntegrationTestBase
{
    private PostgresqlGeneratedDdlTestDatabase? _leasedDb;
    private readonly Dictionary<string, PostgresqlGeneratedDdlTestDatabase> _additionalDbs = new(
        StringComparer.Ordinal
    );

    protected override string Datastore => "postgresql";

    [OneTimeSetUp]
    public void GuardConnectionStringPresent()
    {
        try
        {
            _ = BaselineDatabaseConfiguration.DatabaseConnectionString;
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore(
                "DatabaseConnection is not configured (set ConnectionStrings__DatabaseConnection or add it to appsettings.Test.json); skipping PostgreSQL API integration tests."
            );
        }
    }

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        PostgresqlGeneratedDdlBaselineDatabase baseline = await PostgresqlBaselineCache.CreateOrGetAsync(
            fixture
        );
        _leasedDb = await baseline.CreateIsolatedDatabaseAsync();
        return _leasedDb.ConnectionString;
    }

    protected override async Task<string> LeaseAdditionalDatabaseAsync(FixtureContext fixture)
    {
        PostgresqlGeneratedDdlBaselineDatabase baseline = await PostgresqlBaselineCache.CreateOrGetAsync(
            fixture
        );
        PostgresqlGeneratedDdlTestDatabase database = await baseline.CreateIsolatedDatabaseAsync();
        _additionalDbs[database.ConnectionString] = database;

        return database.ConnectionString;
    }

    protected override async Task ReleaseAdditionalDatabaseAsync(string leasedConnectionString)
    {
        if (_additionalDbs.Remove(leasedConnectionString, out PostgresqlGeneratedDdlTestDatabase? database))
        {
            await database.DisposeAsync();
        }
    }

    protected override IDerivativeTargetReachability Reachability { get; } =
        new PostgresqlTargetReachability();

    /// <summary>
    /// PostgreSQL refuses new connections to a database whose CONNECTION LIMIT is zero, without
    /// touching the database itself or the connection string that names it.
    /// </summary>
    private sealed class PostgresqlTargetReachability : IDerivativeTargetReachability
    {
        public Task MakeUnreachableAsync(string leasedConnectionString) =>
            SetConnectionLimitAsync(leasedConnectionString, limit: 0);

        public Task MakeReachableAsync(string leasedConnectionString) =>
            SetConnectionLimitAsync(leasedConnectionString, limit: -1);

        public string AbsentDatabaseConnectionString(string leasedConnectionString) =>
            new NpgsqlConnectionStringBuilder(leasedConnectionString)
            {
                Database = $"absent_{Guid.NewGuid():N}",
            }.ConnectionString;

        private static async Task SetConnectionLimitAsync(string leasedConnectionString, int limit)
        {
            NpgsqlConnectionStringBuilder leased = new(leasedConnectionString);
            string databaseName = leased.Database!;

            NpgsqlConnectionStringBuilder admin = new(leasedConnectionString) { Database = "postgres" };

            await using NpgsqlConnection connection = new(admin.ConnectionString);
            await connection.OpenAsync();

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText =
                $"ALTER DATABASE \"{databaseName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" "
                + $"WITH CONNECTION LIMIT {limit};";
            await command.ExecuteNonQueryAsync();

            if (limit == 0)
            {
                // Existing sessions are unaffected by the limit, so anything pooled against this
                // database is terminated too; otherwise a reused connection would hide the failure.
                await using NpgsqlCommand terminate = connection.CreateCommand();
                terminate.CommandText =
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                    + "WHERE datname = @databaseName AND pid <> pg_backend_pid();";
                terminate.Parameters.AddWithValue("databaseName", databaseName);
                await terminate.ExecuteNonQueryAsync();
            }
        }
    }

    protected override async Task<DbConnection> OpenAssertionConnectionAsync(string leasedConnectionString)
    {
        NpgsqlConnection connection = new(leasedConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    protected override async Task ReleaseDatabaseAsync(string leasedConnectionString)
    {
        if (_leasedDb is not null)
        {
            await _leasedDb.DisposeAsync();
            _leasedDb = null;
        }
    }
}
