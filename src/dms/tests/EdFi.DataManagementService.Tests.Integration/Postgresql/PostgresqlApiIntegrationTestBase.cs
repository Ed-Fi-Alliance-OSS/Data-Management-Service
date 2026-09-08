// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
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
