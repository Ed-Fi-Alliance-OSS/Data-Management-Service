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
public class Given_Postgresql_DocumentCacheMaterializer_Coherence
{
    private const long DocumentId = 980101;
    private const long ContentVersion = 222;
    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000009801");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private MappingSet _mappingSet = null!;

    [SetUp]
    public async Task SetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);
        _mappingSet = DocumentCacheMaterializerCoherenceMappingSet.Create(SqlDialect.Pgsql);

        await using var connection = await _dataSource.OpenConnectionAsync();
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
                "ContentLastModifiedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );

            INSERT INTO dms."Document" (
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "CreatedByOwnershipTokenId",
                "ContentVersion",
                "ContentLastModifiedAt",
                "CreatedAt"
            )
            VALUES (
                980101,
                'aaaaaaaa-bbbb-cccc-dddd-000000009801',
                11,
                NULL,
                222,
                '2026-07-30T14:15:16Z',
                '2026-07-30T14:15:16Z'
            );
            """
        );
    }

    [TearDown]
    public async Task TearDown()
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
    public async Task It_returns_source_changed_when_document_metadata_changes_before_the_final_coherence_read()
    {
        var sut = CreateMaterializer(async () =>
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await ExecuteSql(
                connection,
                """
                UPDATE dms."Document"
                SET "ContentVersion" = 223
                WHERE "DocumentId" = 980101;
                """
            );
        });

        var result = await sut.MaterializeAsync(CreateRequest(_mappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance);
    }

    [Test]
    public async Task It_returns_missing_source_when_document_is_deleted_before_the_final_coherence_read()
    {
        var sut = CreateMaterializer(async () =>
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await ExecuteSql(
                connection,
                """
                DELETE FROM dms."Document"
                WHERE "DocumentId" = 980101;
                """
            );
        });

        var result = await sut.MaterializeAsync(CreateRequest(_mappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
    }

    private DocumentCacheMaterializer CreateMaterializer(Func<Task> mutateDuringHydration)
    {
        var commandExecutor = new PostgresqlRelationalCommandExecutor(
            async cancellationToken => (DbConnection)await _dataSource.OpenConnectionAsync(cancellationToken),
            NullLogger<PostgresqlRelationalCommandExecutor>.Instance
        );
        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            commandExecutor,
            new MutatingDocumentHydrator(
                _mappingSet.ReadPlansByResource[DocumentCacheMaterializerCoherenceMappingSet.SchoolResource],
                mutateDuringHydration
            )
        );

        return new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(materializationDataStore),
            new ThrowingDescriptorHydrator(),
            materializationDataStore,
            new ThrowingReadMaterializer(),
            new ThrowingServedEtagComposer()
        );
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            DocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MutatingDocumentHydrator(ResourceReadPlan readPlan, Func<Task> mutateDuringHydration)
        : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            plan.Should().BeSameAs(readPlan);
            await mutateDuringHydration();

            return new HydratedPage(
                TotalCount: null,
                DocumentMetadata:
                [
                    new DocumentMetadataRow(DocumentId, DocumentGuid, ContentVersion, LastModifiedAt, 11),
                ],
                TableRowsInDependencyOrder:
                [
                    new HydratedTableRows(readPlan.Model.Root, [new object?[] { DocumentId }]),
                ],
                DescriptorRowsInPlanOrder: []
            );
        }
    }

    private sealed class ThrowingDescriptorHydrator : IDocumentCacheDescriptorHydrator
    {
        public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
            DocumentCacheMaterializationRequest request,
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Coherence integration tests use ordinary resources.");
    }

    private sealed class ThrowingReadMaterializer : IRelationalReadMaterializer
    {
        public JsonNode Materialize(RelationalReadMaterializationRequest request) =>
            throw new NotSupportedException("Source coherence should be checked before materialization.");

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) => throw new NotSupportedException("Coherence integration tests use single-document Materialize.");

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) { }
    }

    private sealed class ThrowingServedEtagComposer : IServedEtagComposer
    {
        public string Compose(ServedEtagContext context) =>
            throw new NotSupportedException("Source coherence should be checked before ETag composition.");
    }
}
