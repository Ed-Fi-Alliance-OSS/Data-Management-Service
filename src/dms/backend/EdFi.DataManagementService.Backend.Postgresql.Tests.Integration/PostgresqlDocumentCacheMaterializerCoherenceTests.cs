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
    private const short ResourceKeyId = 11;
    private const long ContentVersion = 222;
    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000009801");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private MappingSet _mappingSet = null!;

    [SetUp]
    public async Task SetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);
        _mappingSet = CreateMappingSet(SqlDialect.Pgsql);

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
                "IdentityVersion" bigint NOT NULL,
                "ContentLastModifiedAt" timestamptz NOT NULL,
                "IdentityLastModifiedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL
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
                980101,
                'aaaaaaaa-bbbb-cccc-dddd-000000009801',
                11,
                NULL,
                222,
                111,
                '2026-07-30T14:15:16Z',
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

        return new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(commandExecutor),
            new ThrowingDescriptorHydrator(),
            new MutatingDocumentHydrator(
                _mappingSet.ReadPlansByResource[SchoolResource],
                mutateDuringHydration
            ),
            new ThrowingReadMaterializer(),
            new ThrowingServedEtagComposer()
        );
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet
            ),
            DocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var readPlan = CreateReadPlan(dialect);
        var resourceKey = new ResourceKeyEntry(ResourceKeyId, SchoolResource, "1.0", false);
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            readPlan.Model
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [resourceKey]
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [concreteResourceModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [SchoolResource] = ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry> { [ResourceKeyId] = resourceKey },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(SqlDialect dialect)
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new ResourceReadPlan(
            new RelationalResourceModel(
                SchoolResource,
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                rootTable,
                [rootTable],
                [],
                []
            ),
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [new TableReadPlan(rootTable, "select DocumentId")],
            [],
            []
        );
    }

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
                    new DocumentMetadataRow(
                        DocumentId,
                        DocumentGuid,
                        ContentVersion,
                        ContentVersion,
                        LastModifiedAt,
                        LastModifiedAt
                    ),
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
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            SqlDialect dialect,
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
