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
    private const long DescriptorDocumentId = 970301;
    private const short DescriptorResourceKeyId = 13;
    private const long ContentVersion = 222;

    private static readonly Guid DescriptorDocumentGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000301");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

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
        _mappingSet = CreateMappingSet(SqlDialect.Mssql);

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
                    [ContentVersion] bigint NOT NULL,
                    [IdentityVersion] bigint NOT NULL,
                    [ContentLastModifiedAt] datetimeoffset NOT NULL,
                    [IdentityLastModifiedAt] datetimeoffset NOT NULL,
                    [CreatedAt] datetimeoffset NOT NULL
                );

                CREATE TABLE [dms].[Descriptor] (
                    [DocumentId] bigint NOT NULL CONSTRAINT [PK_Descriptor] PRIMARY KEY,
                    [ResourceKeyId] smallint NOT NULL,
                    [Namespace] varchar(255) NOT NULL,
                    [CodeValue] varchar(50) NOT NULL,
                    [ShortDescription] varchar(75) NOT NULL,
                    [Description] varchar(1024) NULL,
                    [EffectiveBeginDate] date NULL,
                    [EffectiveEndDate] date NULL,
                    [Discriminator] varchar(128) NOT NULL
                );

                INSERT INTO [dms].[Document] (
                    [DocumentId],
                    [DocumentUuid],
                    [ResourceKeyId],
                    [CreatedByOwnershipTokenId],
                    [ContentVersion],
                    [IdentityVersion],
                    [ContentLastModifiedAt],
                    [IdentityLastModifiedAt],
                    [CreatedAt]
                )
                VALUES (
                    970301,
                    'aaaaaaaa-bbbb-cccc-dddd-000000000301',
                    13,
                    NULL,
                    222,
                    111,
                    '2026-07-30T14:15:16+00:00',
                    '2026-07-30T14:15:16+00:00',
                    '2026-07-30T14:15:16+00:00'
                );

                INSERT INTO [dms].[Descriptor] (
                    [DocumentId],
                    [ResourceKeyId],
                    [Namespace],
                    [CodeValue],
                    [ShortDescription],
                    [Description],
                    [EffectiveBeginDate],
                    [EffectiveEndDate],
                    [Discriminator]
                )
                VALUES (
                    970301,
                    13,
                    'uri://ed-fi.org/SchoolTypeDescriptor',
                    'Alternative',
                    'Alternative',
                    'Alternative school type',
                    CAST('2025-01-15' AS date),
                    CAST('2025-12-31' AS date),
                    'SchoolTypeDescriptor'
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
                mappingSet
            ),
            DescriptorDocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var descriptorKey = new ResourceKeyEntry(DescriptorResourceKeyId, DescriptorResource, "1.0", false);
        var descriptorModel = new ConcreteResourceModel(
            descriptorKey,
            ResourceStorageKind.SharedDescriptorTable,
            CreateDescriptorRelationalModel()
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [descriptorKey]
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [descriptorModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [DescriptorResource] = DescriptorResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [DescriptorResourceKeyId] = descriptorKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static RelationalResourceModel CreateDescriptorRelationalModel()
    {
        var descriptorTable = new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Descriptor",
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

        return new RelationalResourceModel(
            DescriptorResource,
            new DbSchemaName("dms"),
            ResourceStorageKind.SharedDescriptorTable,
            descriptorTable,
            [descriptorTable],
            [],
            []
        );
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
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
