// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class Given_A_Provisioned_EffectiveSchema_Table
{
    private static readonly string _qualifiedEffectiveSchemaTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Pgsql,
        EffectiveSchemaTableDefinition.Table
    );

    private static readonly string _effectiveSchemaSingletonId = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Pgsql,
        EffectiveSchemaTableDefinition.EffectiveSchemaSingletonId
    );

    private static readonly string _apiSchemaFormatVersion = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Pgsql,
        EffectiveSchemaTableDefinition.ApiSchemaFormatVersion
    );

    private static readonly string _effectiveSchemaHash = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Pgsql,
        EffectiveSchemaTableDefinition.EffectiveSchemaHash
    );

    private static readonly string _resourceKeyCount = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Pgsql,
        EffectiveSchemaTableDefinition.ResourceKeyCount
    );

    private static readonly string _resourceKeySeedHash = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Pgsql,
        EffectiveSchemaTableDefinition.ResourceKeySeedHash
    );

    private static readonly string _expectedEffectiveSchemaHash = new('a', 64);
    private static readonly byte[] _expectedResourceKeySeedHash = Enumerable
        .Range(0, 32)
        .Select(i => (byte)i)
        .ToArray();
    private static readonly string _expectedResourceKeySeedHashHex = Convert
        .ToHexString(_expectedResourceKeySeedHash)
        .ToLowerInvariant();

    private PostgresqlFingerprintTestDatabase _database = null!;
    private DatabaseFingerprint? _result;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlFingerprintTestDatabase.CreateProvisionedAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();

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
                '1.0',
                '{{_expectedEffectiveSchemaHash}}',
                42,
                decode('{{_expectedResourceKeySeedHashHex}}', 'hex')
            );
            """;

        await ExecuteNonQueryAsync(insertSql);

        var reader = new PostgresqlDatabaseFingerprintReader(
            NullLogger<PostgresqlDatabaseFingerprintReader>.Instance
        );

        _result = await reader.ReadFingerprintAsync(_database.ConnectionString);
    }

    [Test]
    public void It_reads_the_stored_fingerprint()
    {
        _result.Should().NotBeNull();
        _result!.ApiSchemaFormatVersion.Should().Be("1.0");
        _result.EffectiveSchemaHash.Should().Be(_expectedEffectiveSchemaHash);
        _result.ResourceKeyCount.Should().Be(42);
        _result.ResourceKeySeedHash.Should().Equal(_expectedResourceKeySeedHash);
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
