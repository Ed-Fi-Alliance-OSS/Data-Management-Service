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
public class Given_MssqlReferenceResolverTestDatabase
{
    private MssqlReferenceResolverTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_provisions_the_minimal_resolver_schema_shape_used_by_the_shared_fixture()
    {
        var tableNames = await ReadRelationNamesAsync(
            _database.ConnectionString,
            """
            SELECT s.name + N'.' + t.name
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name IN (N'dms', N'edfi')
            ORDER BY s.name, t.name;
            """
        );
        var viewNames = await ReadRelationNamesAsync(
            _database.ConnectionString,
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
        await _database.SeedAsync();
        await ExecuteNonQueryAsync(
            _database.ConnectionString,
            """INSERT INTO [edfi].[Student] ([DocumentId]) VALUES (999);"""
        );

        var firstSeedCounts = await ReadFixtureTableCountsAsync(_database.ConnectionString);
        var firstStudentTableCount = await ReadTableCountAsync(
            _database.ConnectionString,
            "[edfi].[Student]"
        );

        await _database.ResetAsync();

        var resetCounts = await ReadFixtureTableCountsAsync(_database.ConnectionString);
        var resetStudentTableCount = await ReadTableCountAsync(
            _database.ConnectionString,
            "[edfi].[Student]"
        );

        await _database.SeedAsync();

        var secondSeedCounts = await ReadFixtureTableCountsAsync(_database.ConnectionString);

        firstSeedCounts
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, long>
                {
                    ["dms.ResourceKey"] = _database.Fixture.SeedData.ResourceKeys.Count,
                    ["dms.Document"] = _database.Fixture.SeedData.Documents.Count,
                    ["dms.ReferentialIdentity"] = _database.Fixture.SeedData.ReferentialIdentities.Count,
                    ["dms.Descriptor"] = _database.Fixture.SeedData.Descriptors.Count,
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
