// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheFingerprint")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheFingerprint_Physical_Source_Reader
{
    private const string ConformanceSourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private const string ExpectedConformanceFingerprint =
        "sha256:1780ea8893149195e89a46c70698dfdf64e8e6f9b31c7b7e9a9872baff498d75";

    private MssqlFingerprintTestDatabase _database = null!;
    private MssqlDocumentCachePhysicalSourceFingerprintReader _reader = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlFingerprintTestDatabase.CreateProvisionedAsync();
        _reader = new MssqlDocumentCachePhysicalSourceFingerprintReader(
            NullLogger<MssqlDocumentCachePhysicalSourceFingerprintReader>.Instance
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
    public void It_reports_the_sqlserver_provider_token()
    {
        _reader.ProviderToken.Should().Be(RelationalProviderToken.SqlServer);
    }

    [Test]
    public async Task It_reads_the_opaque_fingerprint_from_the_singleton_source_identity()
    {
        await RecreateUniqueIdentifierSourceIdentityTableAsync();
        await ExecuteNonQueryAsync(
            $$"""
            INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity])
            VALUES (1, CONVERT(uniqueidentifier, '{{ConformanceSourceIdentity}}'));
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
        await ExecuteNonQueryAsync(
            """
            IF OBJECT_ID(N'dms.DataStoreIdentity', N'U') IS NOT NULL
                DROP TABLE [dms].[DataStoreIdentity];
            """
        );

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
        await RecreateUniqueIdentifierSourceIdentityTableAsync();

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
            INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity])
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
        await RecreateUniqueIdentifierSourceIdentityTableAsync();
        await ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity])
            VALUES (1, CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000'));
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
            IF OBJECT_ID(N'dms.DataStoreIdentity', N'U') IS NOT NULL
                DROP TABLE [dms].[DataStoreIdentity];

            CREATE TABLE [dms].[DataStoreIdentity]
            (
                [DataStoreIdentitySingletonId] smallint NOT NULL
            );

            INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId])
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

    private Task RecreateUniqueIdentifierSourceIdentityTableAsync() =>
        ExecuteNonQueryAsync(
            """
            IF OBJECT_ID(N'dms.DataStoreIdentity', N'U') IS NOT NULL
                DROP TABLE [dms].[DataStoreIdentity];

            CREATE TABLE [dms].[DataStoreIdentity]
            (
                [DataStoreIdentitySingletonId] smallint NOT NULL,
                [SourceIdentity] uniqueidentifier NOT NULL
            );
            """
        );

    private Task RecreateTextSourceIdentityTableAsync() =>
        ExecuteNonQueryAsync(
            """
            IF OBJECT_ID(N'dms.DataStoreIdentity', N'U') IS NOT NULL
                DROP TABLE [dms].[DataStoreIdentity];

            CREATE TABLE [dms].[DataStoreIdentity]
            (
                [DataStoreIdentitySingletonId] smallint NOT NULL,
                [SourceIdentity] varchar(64) NOT NULL
            );
            """
        );

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
