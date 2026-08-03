// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("DocumentCacheFingerprint")]
public class Given_A_Postgresql_DocumentCacheFingerprint_Physical_Source_Reader
{
    private const string ConformanceSourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private const string ExpectedConformanceFingerprint =
        "sha256:193c47b34d9751c73d06dbf5ccf2655a1cce46154a4808f152d3db0e91b676bc";

    private PostgresqlFingerprintTestDatabase _database = null!;
    private PostgresqlDocumentCachePhysicalSourceFingerprintReader _reader = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlFingerprintTestDatabase.CreateProvisionedAsync();
        _reader = new PostgresqlDocumentCachePhysicalSourceFingerprintReader(
            NullLogger<PostgresqlDocumentCachePhysicalSourceFingerprintReader>.Instance
        );
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
    public void It_reports_the_postgresql_provider_token()
    {
        _reader.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
    }

    [Test]
    public async Task It_reads_the_opaque_fingerprint_from_the_singleton_source_identity()
    {
        await RecreateUuidSourceIdentityTableAsync();
        await ExecuteNonQueryAsync(
            $$"""
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
            VALUES (1, '{{ConformanceSourceIdentity}}'::uuid);
            """
        );

        DocumentCachePhysicalSourceFingerprintReadResult result = await _reader.ReadFingerprintAsync(
            _database.ConnectionString
        );

        result.Status.Should().Be(DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded);
        result.Fingerprint!.Value.Should().Be(ExpectedConformanceFingerprint);
    }

    [Test]
    public async Task It_classifies_a_missing_DataStoreIdentity_table_as_missing_inventory()
    {
        await ExecuteNonQueryAsync("""DROP TABLE IF EXISTS "dms"."DataStoreIdentity";""");

        DocumentCachePhysicalSourceFingerprintReadResult result = await _reader.ReadFingerprintAsync(
            _database.ConnectionString
        );

        result.Status.Should().Be(DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentityMissing);
        result.Fingerprint.Should().BeNull();
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Missing);
    }

    [Test]
    public async Task It_classifies_a_missing_singleton_row_as_missing_inventory()
    {
        await RecreateUuidSourceIdentityTableAsync();

        DocumentCachePhysicalSourceFingerprintReadResult result = await _reader.ReadFingerprintAsync(
            _database.ConnectionString
        );

        result
            .Status.Should()
            .Be(DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentitySingletonMissing);
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Missing);
    }

    [Test]
    public async Task It_classifies_a_malformed_source_identity_as_invalid_inventory()
    {
        await RecreateTextSourceIdentityTableAsync();
        await ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
            VALUES (1, 'not-a-uuid');
            """
        );

        DocumentCachePhysicalSourceFingerprintReadResult result = await _reader.ReadFingerprintAsync(
            _database.ConnectionString
        );

        result.Status.Should().Be(DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed);
        result.Fingerprint.Should().BeNull();
        result.Message.Should().NotContain("not-a-uuid");
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_classifies_an_all_zero_source_identity_as_invalid_inventory()
    {
        await RecreateUuidSourceIdentityTableAsync();
        await ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
            VALUES (1, '00000000-0000-0000-0000-000000000000'::uuid);
            """
        );

        DocumentCachePhysicalSourceFingerprintReadResult result = await _reader.ReadFingerprintAsync(
            _database.ConnectionString
        );

        result.Status.Should().Be(DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityAllZero);
        result.Fingerprint.Should().BeNull();
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_classifies_an_unreadable_source_identity_as_unreadable_inventory()
    {
        await ExecuteNonQueryAsync(
            """
            DROP TABLE IF EXISTS "dms"."DataStoreIdentity";
            CREATE TABLE "dms"."DataStoreIdentity"
            (
                "DataStoreIdentitySingletonId" smallint NOT NULL
            );
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId")
            VALUES (1);
            """
        );

        DocumentCachePhysicalSourceFingerprintReadResult result = await _reader.ReadFingerprintAsync(
            _database.ConnectionString
        );

        result.Status.Should().Be(DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable);
        result.Fingerprint.Should().BeNull();
        result.Message.Should().NotContain("DataStoreIdentitySingletonId");
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Unreadable);
    }

    private Task RecreateUuidSourceIdentityTableAsync() =>
        ExecuteNonQueryAsync(
            """
            DROP TABLE IF EXISTS "dms"."DataStoreIdentity";
            CREATE TABLE "dms"."DataStoreIdentity"
            (
                "DataStoreIdentitySingletonId" smallint NOT NULL,
                "SourceIdentity" uuid NOT NULL
            );
            """
        );

    private Task RecreateTextSourceIdentityTableAsync() =>
        ExecuteNonQueryAsync(
            """
            DROP TABLE IF EXISTS "dms"."DataStoreIdentity";
            CREATE TABLE "dms"."DataStoreIdentity"
            (
                "DataStoreIdentitySingletonId" smallint NOT NULL,
                "SourceIdentity" text NOT NULL
            );
            """
        );

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
