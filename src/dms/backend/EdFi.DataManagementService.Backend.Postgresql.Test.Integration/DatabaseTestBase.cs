// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Npgsql;
using NUnit.Framework;
using Respawn;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

// A database test base class that creates a datasource and manages table truncation
public abstract class DatabaseTestBase
{
    private static readonly string _connectionString = Configuration.DatabaseConnectionString ?? string.Empty;
    private NpgsqlConnection? _respawnerConnection;
    private Respawner? _respawner;

    public NpgsqlDataSource? DataSource { get; set; }
    public static readonly IsolationLevel ConfiguredIsolationLevel = Configuration.IsolationLevel;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        new Deploy.DatabaseDeploy().DeployDatabase(_connectionString);
    }

    [SetUp]
    public async Task SetupDatabase()
    {
        DataSource = NpgsqlDataSource.Create(_connectionString);
        _respawnerConnection = await DataSource.OpenConnectionAsync();

        _respawner = await Respawner.CreateAsync(
            _respawnerConnection,
            new RespawnerOptions
            {
                TablesToInclude = [new("dms", "document"), new("dms", "alias"), new("dms", "reference")],
                DbAdapter = DbAdapter.Postgres
            }
        );
    }

    [TearDown]
    public async Task TeardownDatabase()
    {
        if (_respawnerConnection is not null)
        {
            await _respawner!.ResetAsync(_respawnerConnection);
            await _respawnerConnection.DisposeAsync();
        }
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }
}
