// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
[NonParallelizable]
public class Given_MssqlTestDatabaseHelper_CreateDatabase_With_Collation
{
    // A case-sensitive collation that differs from the CI server default (SQL_Latin1_General_CP1_CI_AS).
    private const string RequestedCollation = "Latin1_General_100_CS_AS_SC_UTF8";

    private readonly List<string> _createdDatabases = [];

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        if (!await MssqlTestDatabaseHelper.CollationExistsAsync(RequestedCollation))
        {
            Assert.Ignore($"SQL Server instance does not support collation '{RequestedCollation}'.");
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var databaseName in _createdDatabases)
        {
            await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(databaseName);
        }

        _createdDatabases.Clear();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_creates_the_database_with_the_requested_collation(bool useExplicitFileSizing)
    {
        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        _createdDatabases.Add(databaseName);

        await MssqlTestDatabaseHelper.CreateGeneratedDdlDatabaseAsync(
            databaseName,
            useExplicitFileSizing,
            RequestedCollation
        );

        (await ReadDatabaseCollationAsync(databaseName)).Should().Be(RequestedCollation);
    }

    [Test]
    public async Task It_provisions_generated_ddl_into_a_database_with_the_requested_collation()
    {
        await using var database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            "CREATE TABLE [dbo].[CollationProbe] ([Code] nvarchar(20) NOT NULL);",
            databaseCollation: RequestedCollation
        );

        (await ReadDatabaseCollationAsync(database.DatabaseName)).Should().Be(RequestedCollation);
    }

    private static async Task<string?> ReadDatabaseCollationAsync(string databaseName)
    {
        await using var connection = new SqlConnection(
            BaselineDatabaseConfiguration.MssqlAdminConnectionString
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(@name, 'Collation'));";
        command.Parameters.AddWithValue("name", databaseName);

        return await command.ExecuteScalarAsync() as string;
    }
}

[TestFixture]
[Category(MssqlCiShards.Shard3)]
public class Given_MssqlTestDatabaseHelper_CreateDatabase_With_An_Invalid_Collation
{
    [TestCase("Latin1_General_100_CS_AS; DROP DATABASE master")]
    [TestCase("Latin1_General_100_CS_AS]")]
    [TestCase(" ")]
    public async Task It_rejects_the_collation_name_before_creating_a_database(string collationName)
    {
        var act = () =>
            MssqlTestDatabaseHelper.CreateGeneratedDdlDatabaseAsync(
                "dms_never_created",
                databaseCollation: collationName
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
