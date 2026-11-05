// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class Given_Postgresql_DocumentCacheMaterializer_Descriptor
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
    ).AddTicks(1_234_560);
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private MappingSet _mappingSet = null!;
    private DocumentCacheMaterializationResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);
        _mappingSet = DocumentCacheMaterializerDescriptorMappingSet.Create(SqlDialect.Pgsql);

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await ExecuteSql(
                connection,
                """
                CREATE SCHEMA dms;

                CREATE TABLE dms."Document" (
                    "DocumentId" bigint PRIMARY KEY,
                    "DocumentUuid" uuid NOT NULL,
                    "ResourceKeyId" smallint NOT NULL,
                    "CreatedByOwnershipTokenId" smallint NULL,
                    "ContentVersion" bigint NOT NULL
                );

                CREATE TABLE dms."Descriptor" (
                    "DescriptorId" int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    "DocumentId" bigint NOT NULL UNIQUE REFERENCES dms."Document" ("DocumentId") ON DELETE CASCADE,
                    "ResourceKeyId" smallint NOT NULL,
                    "ContentLastModifiedAt" timestamptz NOT NULL,
                    "ContentCreatedAt" timestamptz NOT NULL,
                    "Namespace" varchar(255) NOT NULL,
                    "CodeValue" varchar(50) NOT NULL,
                    "ShortDescription" varchar(75) NOT NULL,
                    "Description" varchar(1024) NULL,
                    "EffectiveBeginDate" date NULL,
                    "EffectiveEndDate" date NULL
                );

                INSERT INTO dms."Document" (
                    "DocumentId",
                    "DocumentUuid",
                    "ResourceKeyId",
                    "CreatedByOwnershipTokenId",
                    "ContentVersion"
                )
                VALUES (
                    5000970301,
                    'aaaaaaaa-bbbb-cccc-dddd-000000000301',
                    13,
                    NULL,
                    222
                );

                INSERT INTO dms."Descriptor" (
                    "DocumentId",
                    "ResourceKeyId",
                    "ContentLastModifiedAt",
                    "ContentCreatedAt",
                    "Namespace",
                    "CodeValue",
                    "ShortDescription",
                    "Description",
                    "EffectiveBeginDate",
                    "EffectiveEndDate"
                )
                VALUES (
                    5000970301,
                    13,
                    '2026-07-30T14:15:16Z',
                    '2001-02-03T04:05:06.123456Z',
                    'uri://ed-fi.org/SchoolTypeDescriptor',
                    'Alternative',
                    'Alternative',
                    'Alternative school type',
                    DATE '2025-01-15',
                    DATE '2025-12-31'
                );
                """
            );
        }

        var commandExecutor = new PostgresqlRelationalCommandExecutor(
            async cancellationToken => (DbConnection)await _dataSource.OpenConnectionAsync(cancellationToken),
            NullLogger<PostgresqlRelationalCommandExecutor>.Instance
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
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_materializes_a_descriptor_cache_projection_from_real_Postgresql_hydration()
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The descriptor owner is keyed by its DocumentId. These DescriptorId values are swapped against the
/// DocumentIds, so an owner read keyed by DescriptorId would return the other row's creation timestamp.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_Postgresql_DocumentCacheMaterializer_Descriptor_With_Crossed_Descriptor_And_Document_Ids
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
    ).AddTicks(1_234_560);

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

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private DocumentCacheMaterializationResult _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);
        var mappingSet = DocumentCacheMaterializerDescriptorMappingSet.Create(SqlDialect.Pgsql);

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                CREATE SCHEMA dms;

                CREATE TABLE dms."Document" (
                    "DocumentId" bigint PRIMARY KEY,
                    "DocumentUuid" uuid NOT NULL,
                    "ResourceKeyId" smallint NOT NULL,
                    "CreatedByOwnershipTokenId" smallint NULL,
                    "ContentVersion" bigint NOT NULL
                );

                CREATE TABLE dms."Descriptor" (
                    "DescriptorId" int PRIMARY KEY,
                    "DocumentId" bigint NOT NULL UNIQUE REFERENCES dms."Document" ("DocumentId") ON DELETE CASCADE,
                    "ResourceKeyId" smallint NOT NULL,
                    "ContentLastModifiedAt" timestamptz NOT NULL,
                    "ContentCreatedAt" timestamptz NOT NULL,
                    "Namespace" varchar(255) NOT NULL,
                    "CodeValue" varchar(50) NOT NULL,
                    "ShortDescription" varchar(75) NOT NULL,
                    "Description" varchar(1024) NULL,
                    "EffectiveBeginDate" date NULL,
                    "EffectiveEndDate" date NULL
                );

                INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion")
                VALUES
                    (1, 'aaaaaaaa-bbbb-cccc-dddd-000000000001', 13, 11),
                    (2, 'aaaaaaaa-bbbb-cccc-dddd-000000000002', 13, 12);

                INSERT INTO dms."Descriptor" (
                    "DescriptorId", "DocumentId", "ResourceKeyId", "ContentLastModifiedAt", "ContentCreatedAt",
                    "Namespace", "CodeValue", "ShortDescription"
                )
                VALUES
                    (1, 2, 13, '2026-07-30T14:15:16Z', '2001-02-03T04:05:06.123456Z',
                        'uri://ed-fi.org/SchoolTypeDescriptor', 'OwnedByDocumentTwo', 'OwnedByDocumentTwo'),
                    (2, 1, 13, '2026-07-30T14:15:16Z', '2002-03-04T05:06:07.654321Z',
                        'uri://ed-fi.org/SchoolTypeDescriptor', 'OwnedByDocumentOne', 'OwnedByDocumentOne');
                """,
                connection
            );
            await command.ExecuteNonQueryAsync();
        }

        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            new PostgresqlRelationalCommandExecutor(
                async cancellationToken =>
                    (DbConnection)await _dataSource.OpenConnectionAsync(cancellationToken),
                NullLogger<PostgresqlRelationalCommandExecutor>.Instance
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
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
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
