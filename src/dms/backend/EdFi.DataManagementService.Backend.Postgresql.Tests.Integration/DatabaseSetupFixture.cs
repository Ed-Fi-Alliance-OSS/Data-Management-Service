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
/// Holds the assembly-wide data source that <see cref="DatabaseSetupFixture"/> opens on the
/// test database.
/// </summary>
internal static class IntegrationDatabase
{
    private static NpgsqlDataSource? _dataSource;

    internal static NpgsqlDataSource DataSource =>
        _dataSource
        ?? throw new InvalidOperationException(
            "DataSource has not been initialized. Ensure DatabaseSetupFixture has run."
        );

    internal static void InitializeDataSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    internal static async ValueTask DisposeDataSourceAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }
}

/// <summary>
/// One-time setup fixture that provisions the dms schema, the change-version sequence and the
/// dms.GetMaxChangeVersion() helper function.
/// Runs once per test assembly before any test fixtures execute.
/// </summary>
[SetUpFixture]
public class DatabaseSetupFixture
{
    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await EnsureDatabaseExistsAsync();

        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var schema = new DbSchemaName("dms");

        var createSchema = dialect.CreateSchemaIfNotExists(schema);
        var createSequence = dialect.CreateSequenceIfNotExists(schema, "ChangeVersionSequence");
        var createGetMax = dialect.CreateGetMaxChangeVersionFunction(schema);

        var dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync();

            await using (var cmd = new NpgsqlCommand(createSchema, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(createSequence, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(createGetMax, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            IntegrationDatabase.InitializeDataSource(dataSource);
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
        await IntegrationDatabase.DisposeDataSourceAsync();
    }

    /// <summary>
    /// Connects to the default 'postgres' database and creates the test database
    /// if it does not already exist. This isolates the new backend integration tests
    /// from the old DbUp-based tests that share the same PostgreSQL instance in CI.
    /// </summary>
    private static async Task EnsureDatabaseExistsAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Configuration.DatabaseConnectionString);
        var databaseName = builder.Database!;
        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name",
            connection
        );
        checkCmd.Parameters.AddWithValue("name", databaseName);

        if (await checkCmd.ExecuteScalarAsync() is null)
        {
            // Database names are safe identifiers from appsettings, not user input
            await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await createCmd.ExecuteNonQueryAsync();
        }
    }
}
