// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// One-time setup fixture that provisions the pgcrypto extension and the
/// dms.uuidv5() helper function required by the parity tests.
/// Runs once per test assembly before any test fixtures execute.
/// </summary>
[SetUpFixture]
public class DatabaseSetupFixture
{
    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var schema = new DbSchemaName("dms");

        var createSchema = dialect.CreateSchemaIfNotExists(schema);
        var createExtension = dialect.CreateExtensionIfNotExists("pgcrypto");
        var createFunction = dialect.CreateUuidv5Function(schema);

        var dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync();

            await using (var cmd = new NpgsqlCommand(createSchema, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(createExtension, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(createFunction, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            Uuidv5ParityTestBase.InitializeDataSource(dataSource);
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await Uuidv5ParityTestBase.DisposeDataSourceAsync();
    }
}
