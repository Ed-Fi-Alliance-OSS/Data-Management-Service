// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// One-time setup fixture that provisions the dms schema and the
/// dms.uuidv5() helper function required by the parity tests.
/// Runs once per test assembly before any test fixtures execute.
/// </summary>
[SetUpFixture]
public class DatabaseSetupFixture
{
    private string? _databaseName;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.json (or appsettings.Test.json)"
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);

        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        var dialect = new MssqlDialect(new MssqlDialectRules());
        var schema = new DbSchemaName("dms");

        var createSchema = dialect.CreateSchemaIfNotExists(schema);
        var createSequence = dialect.CreateSequenceIfNotExists(schema, "ChangeVersionSequence");
        var createGetMax = dialect.CreateGetMaxChangeVersionFunction(schema);
        var createUuidv5 = dialect.CreateUuidv5Function(schema);

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        // Create the dms schema
        await using (var cmd = new SqlCommand(createSchema, connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Sequence must exist before GetMaxChangeVersion is invoked.
        await using (var cmd = new SqlCommand(createSequence, connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Each CREATE OR ALTER FUNCTION goes in its own SqlCommand (no GO in command text).
        await using (var cmd = new SqlCommand(createGetMax, connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new SqlCommand(createUuidv5, connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        Uuidv5ParityTestBase.InitializeConnectionString(connectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await MssqlBackendBaselineCache.DisposeAllAsync();

        if (_databaseName is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
            _databaseName = null;
        }
    }
}
