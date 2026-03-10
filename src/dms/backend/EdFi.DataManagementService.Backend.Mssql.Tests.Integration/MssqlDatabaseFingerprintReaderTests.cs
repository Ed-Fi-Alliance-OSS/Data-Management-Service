// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_MssqlDatabaseFingerprintReaderTests_A_Provisioned_Core_Dms_Schema
{
    private MssqlFingerprintTestDatabase? _database;
    private string[] _dmsTableNames = [];
    private string[] _effectiveSchemaColumnNames = [];

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlFingerprintTestDatabase.CreateProvisionedAsync();
        _dmsTableNames = await ReadDmsTableNamesAsync(_database.ConnectionString);
        _effectiveSchemaColumnNames = await ReadColumnNamesAsync(
            _database.ConnectionString,
            EffectiveSchemaTableDefinition.Table.Name
        );
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
    public void It_provisions_the_core_dms_tables_required_for_reader_tests()
    {
        _dmsTableNames
            .Should()
            .Equal(
                "Descriptor",
                "Document",
                "DocumentCache",
                "DocumentChangeEvent",
                "EffectiveSchema",
                "ReferentialIdentity",
                "ResourceKey",
                "SchemaComponent"
            );
    }

    [Test]
    public void It_creates_the_expected_dms_EffectiveSchema_columns()
    {
        _effectiveSchemaColumnNames
            .Should()
            .Equal(
                EffectiveSchemaTableDefinition.EffectiveSchemaSingletonId.Value,
                EffectiveSchemaTableDefinition.ApiSchemaFormatVersion.Value,
                EffectiveSchemaTableDefinition.EffectiveSchemaHash.Value,
                EffectiveSchemaTableDefinition.ResourceKeyCount.Value,
                EffectiveSchemaTableDefinition.ResourceKeySeedHash.Value,
                EffectiveSchemaTableDefinition.AppliedAt.Value
            );
    }

    private static async Task<string[]> ReadDmsTableNamesAsync(string connectionString)
    {
        const string sql = """
            SELECT t.name
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = N'dms'
            ORDER BY t.name;
            """;

        return await ReadNameColumnAsync(connectionString, sql);
    }

    private static async Task<string[]> ReadColumnNamesAsync(string connectionString, string tableName)
    {
        const string sql = """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.tables t ON c.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = N'dms'
              AND t.name = @tableName
            ORDER BY c.column_id;
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@tableName", tableName);

        await using var reader = await command.ExecuteReaderAsync();
        List<string> columnNames = [];

        while (await reader.ReadAsync())
        {
            columnNames.Add(reader.GetString(0));
        }

        return [.. columnNames];
    }

    private static async Task<string[]> ReadNameColumnAsync(string connectionString, string sql)
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
}
