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
    private const long DescriptorDocumentId = 970301;
    private const long ContentVersion = 222;

    private static readonly Guid DescriptorDocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000301");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
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
                    "ContentVersion" bigint NOT NULL,
                    "IdentityVersion" bigint NOT NULL,
                    "ContentLastModifiedAt" timestamptz NOT NULL,
                    "IdentityLastModifiedAt" timestamptz NOT NULL,
                    "CreatedAt" timestamptz NOT NULL
                );

                CREATE TABLE dms."Descriptor" (
                    "DocumentId" bigint PRIMARY KEY,
                    "ResourceKeyId" smallint NOT NULL,
                    "Namespace" varchar(255) NOT NULL,
                    "CodeValue" varchar(50) NOT NULL,
                    "ShortDescription" varchar(75) NOT NULL,
                    "Description" varchar(1024) NULL,
                    "EffectiveBeginDate" date NULL,
                    "EffectiveEndDate" date NULL,
                    "Discriminator" varchar(128) NOT NULL
                );

                INSERT INTO dms."Document" (
                    "DocumentId",
                    "DocumentUuid",
                    "ResourceKeyId",
                    "CreatedByOwnershipTokenId",
                    "ContentVersion",
                    "IdentityVersion",
                    "ContentLastModifiedAt",
                    "IdentityLastModifiedAt",
                    "CreatedAt"
                )
                VALUES (
                    970301,
                    'aaaaaaaa-bbbb-cccc-dddd-000000000301',
                    13,
                    NULL,
                    222,
                    111,
                    '2026-07-30T14:15:16Z',
                    '2026-07-30T14:15:16Z',
                    '2026-07-30T14:15:16Z'
                );

                INSERT INTO dms."Descriptor" (
                    "DocumentId",
                    "ResourceKeyId",
                    "Namespace",
                    "CodeValue",
                    "ShortDescription",
                    "Description",
                    "EffectiveBeginDate",
                    "EffectiveEndDate",
                    "Discriminator"
                )
                VALUES (
                    970301,
                    13,
                    'uri://ed-fi.org/SchoolTypeDescriptor',
                    'Alternative',
                    'Alternative',
                    'Alternative school type',
                    DATE '2025-01-15',
                    DATE '2025-12-31',
                    'SchoolTypeDescriptor'
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

    private sealed class ThrowingDocumentHydrator : IDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        ) => throw new NotSupportedException("Descriptor materialization must not use ordinary hydration.");
    }

    private sealed class ThrowingReadMaterializer : IRelationalReadMaterializer
    {
        public JsonNode Materialize(RelationalReadMaterializationRequest request) =>
            throw new NotSupportedException(
                "Descriptor materialization must not use ordinary materialization."
            );

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) =>
            throw new NotSupportedException(
                "Descriptor materialization tests use single-document Materialize."
            );

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) { }
    }
}
