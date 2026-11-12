// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_DocumentCacheMaterializer_Descriptor
{
    private const long DescriptorDocumentId = 5000970301;
    private const long ContentVersion = 222;

    private static readonly Guid DescriptorDocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000301");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly DateTimeOffset ContentCreatedAt = new DateTimeOffset(
        2001,
        2,
        3,
        4,
        5,
        6,
        TimeSpan.Zero
    ).AddTicks(1_234_567);
    private string _databaseName = null!;
    private string _connectionString = null!;
    private MappingSet _mappingSet = null!;
    private DocumentCacheMaterializationResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "MSSQL connection string not configured."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        _mappingSet = DocumentCacheMaterializerDescriptorMappingSet.Create(SqlDialect.Mssql);

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await ExecuteSql(
                connection,
                """
                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');

                CREATE TABLE [dms].[Document] (
                    [DocumentId] bigint NOT NULL CONSTRAINT [PK_Document] PRIMARY KEY,
                    [DocumentUuid] uniqueidentifier NOT NULL,
                    [ResourceKeyId] smallint NOT NULL,
                    [CreatedByOwnershipTokenId] smallint NULL,
                    [ContentVersion] bigint NOT NULL
                );

                CREATE TABLE [dms].[Descriptor] (
                    [DescriptorId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Descriptor] PRIMARY KEY,
                    [DocumentId] bigint NOT NULL UNIQUE REFERENCES [dms].[Document] ([DocumentId]) ON DELETE CASCADE,
                    [ResourceKeyId] smallint NOT NULL,
                    [ContentLastModifiedAt] datetimeoffset NOT NULL,
                    [ContentCreatedAt] datetime2(7) NOT NULL,
                    [Namespace] varchar(255) NOT NULL,
                    [CodeValue] varchar(50) NOT NULL,
                    [ShortDescription] varchar(75) NOT NULL,
                    [Description] varchar(1024) NULL,
                    [EffectiveBeginDate] date NULL,
                    [EffectiveEndDate] date NULL
                );

                INSERT INTO [dms].[Document] (
                    [DocumentId],
                    [DocumentUuid],
                    [ResourceKeyId],
                    [CreatedByOwnershipTokenId],
                    [ContentVersion]
                )
                VALUES (
                    5000970301,
                    'aaaaaaaa-bbbb-cccc-dddd-000000000301',
                    13,
                    NULL,
                    222
                );

                INSERT INTO [dms].[Descriptor] (
                    [DocumentId],
                    [ResourceKeyId],
                    [ContentLastModifiedAt],
                    [ContentCreatedAt],
                    [Namespace],
                    [CodeValue],
                    [ShortDescription],
                    [Description],
                    [EffectiveBeginDate],
                    [EffectiveEndDate]
                )
                VALUES (
                    5000970301,
                    13,
                    '2026-07-30T14:15:16+00:00',
                    '2001-02-03T04:05:06.1234567',
                    'uri://ed-fi.org/SchoolTypeDescriptor',
                    'Alternative',
                    'Alternative',
                    'Alternative school type',
                    CAST('2025-01-15' AS date),
                    CAST('2025-12-31' AS date)
                );
                """
            );
        }

        var commandExecutor = new MssqlRelationalCommandExecutor(
            async cancellationToken =>
            {
                var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                return (DbConnection)connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );
        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            commandExecutor,
            new ThrowingDocumentHydrator()
        );

        var sut = new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(materializationDataStore),
            new DocumentCacheDescriptorHydrator(materializationDataStore),
            materializationDataStore,
            new ThrowingReadMaterializer(),
            new ServedEtagComposer()
        );

        _result = await sut.MaterializeAsync(CreateRequest(_mappingSet));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_materializes_a_descriptor_cache_projection_from_real_Mssql_hydration()
    {
        var success = _result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;

        success.Candidate.DocumentId.Should().Be(DescriptorDocumentId);
        success.Candidate.DocumentUuid.Should().Be(new DocumentUuid(DescriptorDocumentGuid));
        success.Candidate.ProjectName.Should().Be("Ed-Fi");
        success.Candidate.ResourceName.Should().Be("SchoolTypeDescriptor");
        success.Candidate.ResourceVersion.Should().Be("1.0");
        success.Candidate.ContentVersion.Should().Be(ContentVersion);
        success.Candidate.LastModifiedAt.Should().Be(LastModifiedAt);
        success.Candidate.ContentCreatedAt.Should().Be(ContentCreatedAt);
        success.Candidate.StreamEtag.Should().Be("222-01234567.j._.n.i");

        var documentJson = success.Candidate.DocumentJson;
        documentJson["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        documentJson["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        documentJson["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        documentJson["description"]!.GetValue<string>().Should().Be("Alternative school type");
        documentJson["effectiveBeginDate"]!.GetValue<string>().Should().Be("2025-01-15");
        documentJson["effectiveEndDate"]!.GetValue<string>().Should().Be("2025-12-31");
        documentJson["id"]!.GetValue<string>().Should().Be(DescriptorDocumentGuid.ToString());
        documentJson["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-07-30T14:15:16Z");
        documentJson.Should().NotContainKey("_etag");
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            DescriptorDocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The descriptor owner is keyed by its DocumentId. These DescriptorId values are swapped against the
/// DocumentIds, so an owner read keyed by DescriptorId would return the other row's creation timestamp.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_DocumentCacheMaterializer_Descriptor_With_Crossed_Descriptor_And_Document_Ids
{
    private const long MaterializedDocumentId = 2;

    // Owned by DocumentId 2 (DescriptorId 1).
    private static readonly DateTimeOffset CreatedAtA = new DateTimeOffset(
        2001,
        2,
        3,
        4,
        5,
        6,
        TimeSpan.Zero
    ).AddTicks(1_234_567);

    // Owned by DocumentId 1 (DescriptorId 2).
    private static readonly DateTimeOffset CreatedAtB = new DateTimeOffset(
        2002,
        3,
        4,
        5,
        6,
        7,
        TimeSpan.Zero
    ).AddTicks(6_543_210);

    private string _databaseName = null!;
    private DocumentCacheMaterializationResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "MSSQL connection string not configured."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        var mappingSet = DocumentCacheMaterializerDescriptorMappingSet.Create(SqlDialect.Mssql);

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                """
                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');

                CREATE TABLE [dms].[Document] (
                    [DocumentId] bigint NOT NULL CONSTRAINT [PK_Document] PRIMARY KEY,
                    [DocumentUuid] uniqueidentifier NOT NULL,
                    [ResourceKeyId] smallint NOT NULL,
                    [CreatedByOwnershipTokenId] smallint NULL,
                    [ContentVersion] bigint NOT NULL
                );

                CREATE TABLE [dms].[Descriptor] (
                    [DescriptorId] int NOT NULL CONSTRAINT [PK_Descriptor] PRIMARY KEY,
                    [DocumentId] bigint NOT NULL UNIQUE REFERENCES [dms].[Document] ([DocumentId]) ON DELETE CASCADE,
                    [ResourceKeyId] smallint NOT NULL,
                    [ContentLastModifiedAt] datetimeoffset NOT NULL,
                    [ContentCreatedAt] datetime2(7) NOT NULL,
                    [Namespace] varchar(255) NOT NULL,
                    [CodeValue] varchar(50) NOT NULL,
                    [ShortDescription] varchar(75) NOT NULL,
                    [Description] varchar(1024) NULL,
                    [EffectiveBeginDate] date NULL,
                    [EffectiveEndDate] date NULL
                );

                INSERT INTO [dms].[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion])
                VALUES
                    (1, 'aaaaaaaa-bbbb-cccc-dddd-000000000001', 13, 11),
                    (2, 'aaaaaaaa-bbbb-cccc-dddd-000000000002', 13, 12);

                INSERT INTO [dms].[Descriptor] (
                    [DescriptorId], [DocumentId], [ResourceKeyId], [ContentLastModifiedAt], [ContentCreatedAt],
                    [Namespace], [CodeValue], [ShortDescription]
                )
                VALUES
                    (1, 2, 13, '2026-07-30T14:15:16+00:00', '2001-02-03T04:05:06.1234567',
                        'uri://ed-fi.org/SchoolTypeDescriptor', 'OwnedByDocumentTwo', 'OwnedByDocumentTwo'),
                    (2, 1, 13, '2026-07-30T14:15:16+00:00', '2002-03-04T05:06:07.6543210',
                        'uri://ed-fi.org/SchoolTypeDescriptor', 'OwnedByDocumentOne', 'OwnedByDocumentOne');
                """,
                connection
            );
            await command.ExecuteNonQueryAsync();
        }

        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            new MssqlRelationalCommandExecutor(
                async cancellationToken =>
                {
                    var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    return (DbConnection)connection;
                },
                NullLogger<MssqlRelationalCommandExecutor>.Instance
            ),
            new ThrowingDocumentHydrator()
        );
        var sut = new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(materializationDataStore),
            new DocumentCacheDescriptorHydrator(materializationDataStore),
            materializationDataStore,
            new ThrowingReadMaterializer(),
            new ServedEtagComposer()
        );

        _result = await sut.MaterializeAsync(
            new DocumentCacheMaterializationRequest(
                new DocumentCacheMaterializationTargetContext(
                    new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                    mappingSet,
                    DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
                ),
                MaterializedDocumentId,
                selectedRequiredContentVersion: 12,
                DocumentCacheMaterializationPurpose.Fixture,
                CancellationToken.None
            )
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_copies_the_creation_timestamp_of_the_row_owned_by_the_document_id()
    {
        var candidate = _result
            .Should()
            .BeOfType<DocumentCacheMaterializationResult.Success>()
            .Subject.Candidate;

        candidate.DocumentId.Should().Be(MaterializedDocumentId);
        candidate.ContentCreatedAt.Should().Be(CreatedAtA);
        candidate.ContentCreatedAt.Should().NotBe(CreatedAtB);
    }

    [Test]
    public void It_reads_the_descriptor_body_from_the_same_owner_row()
    {
        var candidate = _result
            .Should()
            .BeOfType<DocumentCacheMaterializationResult.Success>()
            .Subject.Candidate;

        candidate.DocumentJson["codeValue"]!.GetValue<string>().Should().Be("OwnedByDocumentTwo");
    }
}

file sealed class ThrowingDocumentHydrator : IDocumentHydrator
{
    public Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    ) => throw new NotSupportedException("Descriptor materialization must not use ordinary hydration.");
}

file sealed class ThrowingReadMaterializer : IRelationalReadMaterializer
{
    public JsonNode Materialize(RelationalReadMaterializationRequest request) =>
        throw new NotSupportedException("Descriptor materialization must not use ordinary materialization.");

    public IReadOnlyList<MaterializedDocument> MaterializePage(
        RelationalReadPageMaterializationRequest request
    ) => throw new NotSupportedException("Descriptor materialization tests use single-document Materialize.");

    public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) { }
}
