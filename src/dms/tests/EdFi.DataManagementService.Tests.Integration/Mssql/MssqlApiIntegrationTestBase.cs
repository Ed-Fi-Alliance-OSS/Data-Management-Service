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
            await _lease.DisposeAsync();
            _lease = null;
        }
    }
}
