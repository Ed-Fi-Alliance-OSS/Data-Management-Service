// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheMaterializer_With_DescriptorHydration
{
    private const long DocumentId = 321L;
    private const short ResourceKeyId = 13;
    private const long ContentVersion = 42L;
    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    public async Task It_hydrates_the_descriptor_row_and_returns_a_no_link_cache_projection_candidate()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateDescriptorSource(testContext);
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source)
        );
        var descriptorHydrator = new RecordingDescriptorHydrator
        {
            Result = new DocumentCacheDescriptorHydrationResult.Found(CreateDescriptorRow(source)),
        };
        var ordinaryHydrator = new RecordingDocumentHydrator();
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            descriptorHydrator,
            ordinaryHydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(
            CreateRequest(testContext.MappingSet, selectedRequiredContentVersion: 19)
        );

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        success.Candidate.DocumentId.Should().Be(DocumentId);
        success.Candidate.DocumentUuid.Should().Be(new DocumentUuid(DocumentGuid));
        success.Candidate.ProjectName.Should().Be("Ed-Fi");
        success.Candidate.ResourceName.Should().Be("SchoolTypeDescriptor");
        success.Candidate.ResourceVersion.Should().Be("5.2.0");
        success.Candidate.ContentVersion.Should().Be(ContentVersion);
        success.Candidate.LastModifiedAt.Should().Be(LastModifiedAt);
        success.Candidate.StreamEtag.Should().Be("stream-etag");

        var documentJson = success.Candidate.DocumentJson;
        documentJson["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        documentJson["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        documentJson["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        documentJson["description"]!.GetValue<string>().Should().Be("Alternative school type");
        documentJson["effectiveBeginDate"]!.GetValue<string>().Should().Be("2025-01-15");
        documentJson["effectiveEndDate"]!.GetValue<string>().Should().Be("2025-12-31");
        documentJson["id"]!.GetValue<string>().Should().Be(DocumentGuid.ToString());
        documentJson["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-07-30T14:15:16Z");
        documentJson.Should().NotContainKey("_etag");

        sourceReader.CapturedRequest.Should().NotBeNull();
        sourceReader.CapturedRequest!.SelectedRequiredContentVersion.Should().Be(19);
        descriptorHydrator.CapturedSource.Should().BeSameAs(source);
        descriptorHydrator.CapturedRequest.Should().NotBeNull();
        descriptorHydrator
            .CapturedRequest!.TargetContext.MappingSet.Key.Dialect.Should()
            .Be(SqlDialect.Pgsql);
        ordinaryHydrator.CallCount.Should().Be(0);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer
            .CapturedContext.Should()
            .Be(
                new ServedEtagContext(
                    testContext.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    ProfileName: null,
                    LinksEnabled: false,
                    ContentVersion,
                    ResponseContentCoding.Identity
                )
            );
    }

    [Test]
    public async Task It_returns_missing_source_when_final_coherence_no_longer_finds_the_canonical_document()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateDescriptorSource(testContext);
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance
        );
        var descriptorHydrator = new RecordingDescriptorHydrator
        {
            Result = new DocumentCacheDescriptorHydrationResult.Found(CreateDescriptorRow(source)),
        };
        var ordinaryHydrator = new RecordingDocumentHydrator();
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            descriptorHydrator,
            ordinaryHydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
        ordinaryHydrator.CallCount.Should().Be(0);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    [Test]
    public async Task It_returns_source_changed_when_final_coherence_observes_changed_source_metadata()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateDescriptorSource(testContext);
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            new DocumentCacheCurrentSourceMetadataReadResult.Found(
                new DocumentCacheCurrentSourceMetadata(
                    source.DocumentId,
                    source.DocumentUuid,
                    source.ResourceKeyId,
                    source.ContentVersion,
                    source.ContentLastModifiedAt.AddSeconds(1)
                )
            )
        );
        var descriptorHydrator = new RecordingDescriptorHydrator
        {
            Result = new DocumentCacheDescriptorHydrationResult.Found(CreateDescriptorRow(source)),
        };
        var ordinaryHydrator = new RecordingDocumentHydrator();
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            descriptorHydrator,
            ordinaryHydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance);
        ordinaryHydrator.CallCount.Should().Be(0);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    [Test]
    public async Task It_throws_projection_processing_failure_when_stable_metadata_has_no_descriptor_body()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateDescriptorSource(testContext);
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source)
        );
        var descriptorHydrator = new RecordingDescriptorHydrator
        {
            Result = DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance,
        };
        var ordinaryHydrator = new RecordingDocumentHydrator();
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            descriptorHydrator,
            ordinaryHydrator,
            readMaterializer,
            servedEtagComposer
        );

        Func<Task> act = () =>
            sut.MaterializeAsync(CreateRequest(testContext.MappingSet, selectedRequiredContentVersion: 456));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.StableSourceBodyMissing);
        exception.FailureMetadata.DocumentId.Should().Be(DocumentId);
        exception.FailureMetadata.SelectedRequiredContentVersion.Should().Be(456);
        exception.FailureMetadata.ResourceKeyId.Should().Be(ResourceKeyId);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("SchoolTypeDescriptor");
        exception.FailureMetadata.ResourceVersion.Should().Be("5.2.0");
        ordinaryHydrator.CallCount.Should().Be(0);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    private static DocumentCacheMaterializationRequest CreateRequest(
        MappingSet mappingSet,
        long? selectedRequiredContentVersion = null
    ) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet
            ),
            DocumentId,
            selectedRequiredContentVersion,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            CancellationToken.None
        );

    private static DocumentCacheResolvedSourceMetadata.DescriptorResource CreateDescriptorSource(
        MaterializerTestContext testContext
    ) =>
        new(
            DocumentId,
            new DocumentUuid(DocumentGuid),
            ResourceKeyId,
            testContext.ResourceKey,
            testContext.ConcreteResourceModel,
            ContentVersion,
            LastModifiedAt
        );

    private static DescriptorReadRow CreateDescriptorRow(DocumentCacheResolvedSourceMetadata source) =>
        new(
            source.DocumentId,
            source.DocumentUuid.Value,
            source.ContentVersion,
            source.ContentLastModifiedAt,
            source.ResourceKeyId,
            "uri://ed-fi.org/SchoolTypeDescriptor",
            "Alternative",
            "Alternative",
            "Alternative school type",
            new DateOnly(2025, 1, 15),
            new DateOnly(2025, 12, 31),
            "SchoolTypeDescriptor"
        );

    private static MaterializerTestContext CreateMaterializerTestContext()
    {
        var resourceKey = new ResourceKeyEntry(ResourceKeyId, DescriptorResource, "5.2.0", false);
        var relationalModel = CreateDescriptorRelationalModel(DescriptorResource);
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.SharedDescriptorTable,
            relationalModel
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [resourceKey]
        );
        var modelSet = new DerivedRelationalModelSet(
            effectiveSchema,
            SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder: [concreteResourceModel],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );
        var mappingSet = new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [DescriptorResource] = ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry> { [ResourceKeyId] = resourceKey },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

        return new MaterializerTestContext(mappingSet, resourceKey, concreteResourceModel);
    }

    private static RelationalResourceModel CreateDescriptorRelationalModel(QualifiedResourceName resource)
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
            resource,
            new DbSchemaName("dms"),
            ResourceStorageKind.SharedDescriptorTable,
            descriptorTable,
            [descriptorTable],
            [],
            []
        );
    }

    private sealed record MaterializerTestContext(
        MappingSet MappingSet,
        ResourceKeyEntry ResourceKey,
        ConcreteResourceModel ConcreteResourceModel
    );

    private sealed class StubSourceMetadataReader(
        DocumentCacheSourceMetadataReadResult result,
        DocumentCacheCurrentSourceMetadataReadResult? currentResult = null
    ) : IDocumentCacheSourceMetadataReader
    {
        public DocumentCacheMaterializationRequest? CapturedRequest { get; private set; }

        public Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            CapturedRequest = request;
            return Task.FromResult(result);
        }

        public Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            CapturedRequest = request;
            return Task.FromResult(currentResult ?? CreateCurrentResult(result));
        }

        private static DocumentCacheCurrentSourceMetadataReadResult CreateCurrentResult(
            DocumentCacheSourceMetadataReadResult sourceResult
        ) =>
            sourceResult switch
            {
                DocumentCacheSourceMetadataReadResult.MissingSource =>
                    DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance,
                DocumentCacheSourceMetadataReadResult.Found found =>
                    new DocumentCacheCurrentSourceMetadataReadResult.Found(
                        new DocumentCacheCurrentSourceMetadata(
                            found.Metadata.DocumentId,
                            found.Metadata.DocumentUuid,
                            found.Metadata.ResourceKeyId,
                            found.Metadata.ContentVersion,
                            found.Metadata.ContentLastModifiedAt
                        )
                    ),
                _ => throw new InvalidOperationException(
                    $"Unsupported source metadata test result '{sourceResult.GetType().Name}'."
                ),
            };
    }

    private sealed class RecordingDescriptorHydrator : IDocumentCacheDescriptorHydrator
    {
        public DocumentCacheDescriptorHydrationResult Result { get; init; } =
            DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;

        public DocumentCacheMaterializationRequest? CapturedRequest { get; private set; }

        public DocumentCacheResolvedSourceMetadata.DescriptorResource? CapturedSource { get; private set; }

        public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
            DocumentCacheMaterializationRequest request,
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            CancellationToken cancellationToken = default
        )
        {
            CapturedRequest = request;
            CapturedSource = source;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingDocumentHydrator : IDocumentCacheMaterializationDataStore
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int CallCount { get; private set; }

        public Task<TResult> ExecuteReaderAsync<TResult>(
            DocumentCacheMaterializationRequest request,
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException(
                "Descriptor hydration tests provide source metadata through a stub reader."
            );

        public Task<HydratedPage> HydrateAsync(
            DocumentCacheMaterializationRequest request,
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            throw new NotSupportedException("Descriptor hydration tests must not use ordinary hydration.");
        }
    }

    private sealed class RecordingReadMaterializer : IRelationalReadMaterializer
    {
        public int CallCount { get; private set; }

        public JsonNode Materialize(RelationalReadMaterializationRequest request)
        {
            CallCount++;
            throw new NotSupportedException(
                "Descriptor hydration tests must not use ordinary materialization."
            );
        }

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) =>
            throw new NotSupportedException(
                "DocumentCacheMaterializer descriptor tests use single-document Materialize."
            );

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) { }
    }

    private sealed class RecordingServedEtagComposer(string returnValue) : IServedEtagComposer
    {
        public ServedEtagContext? CapturedContext { get; private set; }

        public string Compose(ServedEtagContext context)
        {
            CapturedContext = context;
            return returnValue;
        }
    }
}

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheDescriptorHydrator
{
    private const long DocumentId = 321L;
    private const short ResourceKeyId = 13;
    private const long ContentVersion = 42L;
    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    public async Task It_reads_descriptor_body_from_the_expected_DocumentId_and_ResourceKeyId()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([CreateDescriptorResultSet(CreateHydrationRow())]),
        ]);
        var sut = new DocumentCacheDescriptorHydrator(dataStore);

        var result = await sut.HydrateAsync(CreateRequest(), CreateDescriptorSource());

        var found = result.Should().BeOfType<DocumentCacheDescriptorHydrationResult.Found>().Subject;
        found.DescriptorRow.DocumentId.Should().Be(DocumentId);
        found.DescriptorRow.DocumentUuid.Should().Be(DocumentGuid);
        found.DescriptorRow.ResourceKeyId.Should().Be(ResourceKeyId);
        found.DescriptorRow.Namespace.Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        found.DescriptorRow.CodeValue.Should().Be("Alternative");
        found.DescriptorRow.ShortDescription.Should().Be("Alternative");

        dataStore.Commands.Should().ContainSingle();
        dataStore.Commands[0].CommandText.Should().Contain("""FROM dms."Descriptor" descriptor""");
        dataStore.Commands[0].CommandText.Should().Contain("""descriptor."DocumentId" = @documentId""");
        dataStore.Commands[0].CommandText.Should().Contain("""descriptor."ResourceKeyId" = @resourceKeyId""");
        dataStore.Commands[0].CommandText.Should().NotContain("dms.\"Document\"");
        dataStore.Commands[0].CommandText.Should().NotContain("LEFT JOIN");
        dataStore.Commands[0].CommandText.Should().NotContain("@documentUuid");
        dataStore.Commands[0].CommandText.Should().NotContain("Uri");
        dataStore
            .Commands[0]
            .Parameters.Should()
            .Contain(parameter => parameter.Name == "@documentId" && (long)parameter.Value! == DocumentId)
            .And.Contain(parameter =>
                parameter.Name == "@resourceKeyId" && (short)parameter.Value! == ResourceKeyId
            );
    }

    [Test]
    public async Task It_returns_stable_body_missing_when_the_descriptor_row_is_absent()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = new DocumentCacheDescriptorHydrator(dataStore);

        var result = await sut.HydrateAsync(CreateRequest(), CreateDescriptorSource());

        result.Should().BeSameAs(DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance);
    }

    [Test]
    public async Task It_returns_stable_body_missing_when_required_descriptor_fields_are_absent()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore([
            new InMemoryRelationalCommandExecution([
                CreateDescriptorResultSet(
                    CreateHydrationRow(("Namespace", null), ("CodeValue", null), ("ShortDescription", null))
                ),
            ]),
        ]);
        var sut = new DocumentCacheDescriptorHydrator(dataStore);

        var result = await sut.HydrateAsync(CreateRequest(), CreateDescriptorSource());

        result.Should().BeSameAs(DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance);
    }

    [Test]
    public async Task It_uses_sql_server_descriptor_body_sql_for_sql_server_mapping_sets()
    {
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore(
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])],
            SqlDialect.Mssql
        );
        var sut = new DocumentCacheDescriptorHydrator(dataStore);

        await sut.HydrateAsync(CreateRequest(SqlDialect.Mssql), CreateDescriptorSource());

        dataStore.Commands.Should().ContainSingle();
        dataStore.Commands[0].CommandText.Should().Contain("FROM [dms].[Descriptor] descriptor");
        dataStore.Commands[0].CommandText.Should().Contain("descriptor.[DocumentId] = @documentId");
        dataStore.Commands[0].CommandText.Should().Contain("descriptor.[ResourceKeyId] = @resourceKeyId");
        dataStore.Commands[0].CommandText.Should().NotContain("[dms].[Document]");
        dataStore.Commands[0].CommandText.Should().NotContain("LEFT JOIN");
    }

    private static DocumentCacheMaterializationRequest CreateRequest(SqlDialect dialect = SqlDialect.Pgsql) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                CreateMappingSet(dialect)
            ),
            DocumentId,
            selectedRequiredContentVersion: null,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(DescriptorResource);

        return mappingSet with
        {
            Key = new MappingSetKey("test-hash", dialect, "v1"),
        };
    }

    private static InMemoryRelationalResultSet CreateDescriptorResultSet(
        IReadOnlyDictionary<string, object?> row
    ) => InMemoryRelationalResultSet.Create(row);

    private static IReadOnlyDictionary<string, object?> CreateHydrationRow(
        params (string ColumnName, object? Value)[] overrides
    )
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DocumentId"] = DocumentId,
            ["DocumentUuid"] = DocumentGuid,
            ["ContentVersion"] = ContentVersion,
            ["ContentLastModifiedAt"] = LastModifiedAt,
            ["ResourceKeyId"] = ResourceKeyId,
            ["DescriptorDocumentId"] = DocumentId,
            ["DescriptorResourceKeyId"] = ResourceKeyId,
            ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
            ["CodeValue"] = "Alternative",
            ["ShortDescription"] = "Alternative",
            ["Description"] = "Alternative school type",
            ["EffectiveBeginDate"] = new DateOnly(2025, 1, 15),
            ["EffectiveEndDate"] = new DateOnly(2025, 12, 31),
            ["Discriminator"] = "SchoolTypeDescriptor",
        };

        foreach (var (columnName, value) in overrides)
        {
            row[columnName] = value;
        }

        return row;
    }

    private static DocumentCacheResolvedSourceMetadata.DescriptorResource CreateDescriptorSource()
    {
        var resourceKey = new ResourceKeyEntry(ResourceKeyId, DescriptorResource, "5.2.0", false);
        var relationalModel = CreateDescriptorRelationalModel(DescriptorResource);
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.SharedDescriptorTable,
            relationalModel
        );

        return new DocumentCacheResolvedSourceMetadata.DescriptorResource(
            DocumentId,
            new DocumentUuid(DocumentGuid),
            ResourceKeyId,
            resourceKey,
            concreteResourceModel,
            ContentVersion,
            LastModifiedAt
        );
    }

    private static RelationalResourceModel CreateDescriptorRelationalModel(QualifiedResourceName resource)
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
            resource,
            new DbSchemaName("dms"),
            ResourceStorageKind.SharedDescriptorTable,
            descriptorTable,
            [descriptorTable],
            [],
            []
        );
    }
}
