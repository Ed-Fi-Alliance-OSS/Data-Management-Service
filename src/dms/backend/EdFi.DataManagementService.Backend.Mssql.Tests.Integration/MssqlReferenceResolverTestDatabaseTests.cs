// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_MssqlReferenceResolverTestDatabase
{
    private MssqlReferenceResolverTestDatabase? _database;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }

    [Test]
    public async Task It_provisions_the_minimal_resolver_schema_shape_used_by_the_shared_fixture()
    {
        var database = _database ?? throw new InvalidOperationException("Test database not initialized.");
        var tableNames = await ReadRelationNamesAsync(
            database.ConnectionString,
            """
            SELECT s.name + N'.' + t.name
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name IN (N'dms', N'edfi')
            ORDER BY s.name, t.name;
            """
        );
        var viewNames = await ReadRelationNamesAsync(
            database.ConnectionString,
            """
            SELECT s.name + N'.' + v.name
            FROM sys.views v
            JOIN sys.schemas s ON v.schema_id = s.schema_id
            WHERE s.name = N'edfi'
            ORDER BY s.name, v.name;
            """
        );

        tableNames
            .Should()
            .Contain([
                "dms.Descriptor",
                "dms.Document",
                "dms.DocumentCache",
                "dms.DocumentChangeEvent",
                "dms.EffectiveSchema",
                "dms.ReferentialIdentity",
                "dms.ResourceKey",
                "dms.SchemaComponent",
                "edfi.LocalEducationAgency",
                "edfi.School",
                "edfi.Student",
            ]);
        viewNames.Should().Contain("edfi.EducationOrganization_View");
    }

    [Test]
    public async Task It_seeds_and_resets_repeatable_fixture_data_between_test_runs()
    {
        var database = _database ?? throw new InvalidOperationException("Test database not initialized.");
        await database.SeedAsync();
        await ExecuteNonQueryAsync(
            database.ConnectionString,
            """INSERT INTO [edfi].[Student] ([DocumentId]) VALUES (999);"""
        );

        var firstSeedCounts = await ReadFixtureTableCountsAsync(database.ConnectionString);
        var firstStudentTableCount = await ReadTableCountAsync(database.ConnectionString, "[edfi].[Student]");

        await database.ResetAsync();

        var resetCounts = await ReadFixtureTableCountsAsync(database.ConnectionString);
        var resetStudentTableCount = await ReadTableCountAsync(database.ConnectionString, "[edfi].[Student]");

        await database.SeedAsync();

        var secondSeedCounts = await ReadFixtureTableCountsAsync(database.ConnectionString);

        firstSeedCounts
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, long>
                {
                    ["dms.ResourceKey"] = database.Fixture.SeedData.ResourceKeys.Count,
                    ["dms.Document"] = database.Fixture.SeedData.Documents.Count,
                    ["dms.ReferentialIdentity"] = database.Fixture.SeedData.ReferentialIdentities.Count,
                    ["dms.Descriptor"] = database.Fixture.SeedData.Descriptors.Count,
                }
            );
        firstStudentTableCount.Should().Be(1);

        resetCounts.Values.Should().OnlyContain(count => count == 0);
        resetStudentTableCount.Should().Be(0);

        secondSeedCounts.Should().BeEquivalentTo(firstSeedCounts);
    }

    private static async Task<IDictionary<string, long>> ReadFixtureTableCountsAsync(string connectionString)
    {
        return new Dictionary<string, long>
        {
            ["dms.ResourceKey"] = await ReadTableCountAsync(connectionString, "[dms].[ResourceKey]"),
            ["dms.Document"] = await ReadTableCountAsync(connectionString, "[dms].[Document]"),
            ["dms.ReferentialIdentity"] = await ReadTableCountAsync(
                connectionString,
                "[dms].[ReferentialIdentity]"
            ),
            ["dms.Descriptor"] = await ReadTableCountAsync(connectionString, "[dms].[Descriptor]"),
        };
    }

    private static async Task<long> ReadTableCountAsync(string connectionString, string qualifiedTableName)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""SELECT COUNT(*) FROM {qualifiedTableName};""";

        return Convert.ToInt64((await command.ExecuteScalarAsync())!);
    }

    private static async Task<string[]> ReadRelationNamesAsync(string connectionString, string sql)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        List<string> names = [];

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
