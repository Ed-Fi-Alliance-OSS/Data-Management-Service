// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_MssqlDatabaseFingerprintReaderTests_A_Provisioned_Core_Dms_Schema
{
    private static readonly string _qualifiedEffectiveSchemaTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Mssql,
        EffectiveSchemaTableDefinition.Table
    );
    private static readonly string _effectiveSchemaSingletonId = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Mssql,
        EffectiveSchemaTableDefinition.EffectiveSchemaSingletonId
    );
    private static readonly string _apiSchemaFormatVersion = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Mssql,
        EffectiveSchemaTableDefinition.ApiSchemaFormatVersion
    );
    private static readonly string _effectiveSchemaHash = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Mssql,
        EffectiveSchemaTableDefinition.EffectiveSchemaHash
    );
    private static readonly string _resourceKeyCount = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Mssql,
        EffectiveSchemaTableDefinition.ResourceKeyCount
    );
    private static readonly string _resourceKeySeedHash = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Mssql,
        EffectiveSchemaTableDefinition.ResourceKeySeedHash
    );
    private static readonly string _expectedEffectiveSchemaHash = new('a', 64);
    private static readonly byte[] _expectedResourceKeySeedHash = Enumerable
        .Range(0, 32)
        .Select(static i => (byte)i)
        .ToArray();

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

    [Test]
    public async Task It_reads_the_stored_fingerprint()
    {
        var database = _database ?? throw new InvalidOperationException("Test database not initialized.");
        await InsertFingerprintAsync(database.ConnectionString);

        var reader = new MssqlDatabaseFingerprintReader(NullLogger<MssqlDatabaseFingerprintReader>.Instance);

        var result = await reader.ReadFingerprintAsync(database.ConnectionString);

        result.Should().NotBeNull();
        result!.ApiSchemaFormatVersion.Should().Be("1.0");
        result.EffectiveSchemaHash.Should().Be(_expectedEffectiveSchemaHash);
        result.ResourceKeyCount.Should().Be(42);
        result.ResourceKeySeedHash.Should().Equal(_expectedResourceKeySeedHash);
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

    private static async Task InsertFingerprintAsync(string connectionString)
    {
        var insertSql = $$"""
            INSERT INTO {{_qualifiedEffectiveSchemaTable}} (
                {{_effectiveSchemaSingletonId}},
                {{_apiSchemaFormatVersion}},
                {{_effectiveSchemaHash}},
                {{_resourceKeyCount}},
                {{_resourceKeySeedHash}}
            )
            VALUES (
                1,
                @apiSchemaFormatVersion,
                @effectiveSchemaHash,
                @resourceKeyCount,
                @resourceKeySeedHash
            );
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = insertSql;
        command.Parameters.AddWithValue("@apiSchemaFormatVersion", "1.0");
        command.Parameters.AddWithValue("@effectiveSchemaHash", _expectedEffectiveSchemaHash);
        command.Parameters.AddWithValue("@resourceKeyCount", (short)42);
        command.Parameters.AddWithValue("@resourceKeySeedHash", _expectedResourceKeySeedHash);
        await command.ExecuteNonQueryAsync();
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
