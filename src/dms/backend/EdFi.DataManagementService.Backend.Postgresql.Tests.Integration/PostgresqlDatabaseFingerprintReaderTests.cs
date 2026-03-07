// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class Given_A_Provisioned_EffectiveSchema_Table
{
    private static readonly string _coreDdl = new CoreDdlEmitter(
        new PgsqlDialect(new PgsqlDialectRules())
    ).Emit();

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

    private DatabaseFingerprint? _result;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await ExecuteNonQueryAsync(_coreDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await ExecuteNonQueryAsync($"DELETE FROM {_qualifiedEffectiveSchemaTable};");

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

        _result = await reader.ReadFingerprintAsync(Configuration.DatabaseConnectionString);
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

    private static async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Configuration.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
