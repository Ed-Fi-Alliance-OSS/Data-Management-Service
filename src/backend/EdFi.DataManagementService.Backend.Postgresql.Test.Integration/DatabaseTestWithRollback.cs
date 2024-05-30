// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using Respawn;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

/// <summary>
/// Implements a database cleanup after each test
/// </summary>
[AttributeUsage(AttributeTargets.All)]
public class DatabaseTestWithRollback : Attribute, ITestAction
{
    private static readonly string _connectionString =
        "host=localhost;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_test_integration;Application Name=EdFi.DataManagementService.Backend.Postgresql.Test.Integration";
    private NpgsqlConnection? _respawnerConnection;
    private Respawner? _respawner;

    public async void BeforeTest(ITest test)
    {
        if (test.Fixture is IDatabaseTest databaseTest)
        {
            new Deploy.DatabaseDeploy().DeployDatabase(_connectionString);

            databaseTest.DataSource = NpgsqlDataSource.Create(_connectionString);
            _respawnerConnection = await databaseTest.DataSource.OpenConnectionAsync();

            _respawner = await Respawner.CreateAsync(
                _respawnerConnection,
                new RespawnerOptions
                {
                    TablesToInclude =
                    [
                        new("public", "documents"),
                        new("public", "aliases"),
                        new("public", "references")
                    ],
                    DbAdapter = DbAdapter.Postgres
                }
            );
        }
    }

    public async void AfterTest(ITest test)
    {
        if (test.Fixture is IDatabaseTest databaseTest)
        {
            await _respawner!.ResetAsync(_respawnerConnection!);
            _respawnerConnection?.Dispose();
            databaseTest.DataSource?.Dispose();
        }
    }

    public ActionTargets Targets => ActionTargets.Test;
}
