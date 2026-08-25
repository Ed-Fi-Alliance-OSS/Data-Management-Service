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
public class Given_DocumentCacheMaterializer_With_Ordinary_ResourceHydration
{
    private const long DocumentId = 123L;
    private const short ResourceKeyId = 11;
    private const long ContentVersion = 987L;
    private static readonly Guid DocumentGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    [Test]
    public async Task It_hydrates_one_document_by_DocumentId_and_returns_a_cache_projection_candidate()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateOrdinarySource(testContext);
        var expectedDocumentJson = JsonNode
            .Parse(
                """
                {"id":"11111111-2222-3333-4444-555555555555","_lastModifiedDate":"2026-07-30T14:15:16Z","nameOfInstitution":"Lincoln High"}
                """
            )!
            .AsObject();
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source)
        );
        var hydrator = new RecordingDocumentHydrator
        {
            Result = CreateHydratedPage(testContext.ReadPlan, source),
        };
        var readMaterializer = new RecordingReadMaterializer { Result = expectedDocumentJson };
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new ThrowingDescriptorHydrator(),
            hydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(
            CreateRequest(testContext.MappingSet, selectedRequiredContentVersion: 1)
        );

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        success.Candidate.DocumentId.Should().Be(DocumentId);
        success.Candidate.DocumentUuid.Should().Be(new DocumentUuid(DocumentGuid));
        success.Candidate.ProjectName.Should().Be("Ed-Fi");
        success.Candidate.ResourceName.Should().Be("School");
        success.Candidate.ResourceVersion.Should().Be("5.2.0");
        success.Candidate.ContentVersion.Should().Be(ContentVersion);
        success.Candidate.LastModifiedAt.Should().Be(LastModifiedAt);
        success.Candidate.StreamEtag.Should().Be("stream-etag");
        success.Candidate.DocumentJson.Should().BeSameAs(expectedDocumentJson);

        sourceReader.CapturedRequest.Should().NotBeNull();
        sourceReader.CapturedRequest!.SelectedRequiredContentVersion.Should().Be(1);
        hydrator.CapturedPlan.Should().BeSameAs(testContext.ReadPlan);
        hydrator.CapturedKeyset.Should().Be(new PageKeysetSpec.Single(DocumentId));
        hydrator
            .CapturedExecutionOptions.Should()
            .Be(
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: true,
                    IncludeDocumentReferenceLookup: true,
                    UseSingleDocumentFastPath: true
                )
            );
        readMaterializer.CapturedRequest.Should().NotBeNull();
        readMaterializer.CapturedRequest!.ReadPlan.Should().BeSameAs(testContext.ReadPlan);
        readMaterializer
            .CapturedRequest.ReadMode.Should()
            .Be(RelationalReadMaterializationMode.CacheProjection);
        readMaterializer.CapturedRequest.MappingSet.Should().BeSameAs(testContext.MappingSet);
        readMaterializer
            .CapturedRequest.DocumentReferenceLookup.Should()
            .BeSameAs(hydrator.Result.DocumentReferenceLookup);
        readMaterializer.CapturedRequest.EtagVariant.Should().BeNull();
        readMaterializer
            .CapturedRequest.DocumentMetadata.Should()
            .Be(
                new DocumentMetadataRow(
                    DocumentId,
                    DocumentGuid,
                    ContentVersion,
                    LastModifiedAt,
                    ResourceKeyId
                )
            );
        servedEtagComposer
            .CapturedContext.Should()
            .Be(
                new ServedEtagContext(
                    testContext.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    ProfileName: null,
                    LinksEnabled: true,
                    ContentVersion,
                    ResponseContentCoding.Identity
                )
            );
        servedEtagComposer.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_uses_TargetContext_data_store_for_metadata_reads_and_ordinary_hydration()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateOrdinarySource(testContext);
        var request = CreateRequest(testContext.MappingSet);
        var expectedDocumentJson = JsonNode
            .Parse(
                """
                {"id":"11111111-2222-3333-4444-555555555555","_lastModifiedDate":"2026-07-30T14:15:16Z","nameOfInstitution":"Lincoln High"}
                """
            )!
            .AsObject();
        var targetConnectionStrings = new Queue<string>(["Host=initial", "Host=replacement"]);
        var dataStore = new InMemoryDocumentCacheMaterializationDataStore(
            [
                new InMemoryRelationalCommandExecution([CreateSourceMetadataResultSet(source)]),
                new InMemoryRelationalCommandExecution([CreateSourceMetadataResultSet(source)]),
            ],
            hydratedPages: [CreateHydratedPage(testContext.ReadPlan, source)],
            bindRequest: currentRequest =>
            {
                var connectionString = targetConnectionStrings.Dequeue();

                var targetContext = new DocumentCacheMaterializationTargetContext(
                    currentRequest.TargetContext.TargetKey,
                    currentRequest.TargetContext.MappingSet,
                    currentRequest.TargetContext.TargetValidation,
                    connectionString
                );

                return new DocumentCacheMaterializationRequest(
                    targetContext,
                    currentRequest.DocumentId,
                    currentRequest.SelectedRequiredContentVersion,
                    currentRequest.Purpose,
                    currentRequest.CancellationToken
                );
            }
        );
        var readMaterializer = new RecordingReadMaterializer { Result = expectedDocumentJson };
        var sut = new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(dataStore),
            new DocumentCacheDescriptorHydrator(dataStore),
            dataStore,
            readMaterializer,
            new RecordingServedEtagComposer("stream-etag")
        );

        var result = await sut.MaterializeAsync(request);

        result.Should().BeOfType<DocumentCacheMaterializationResult.Success>();
        dataStore.BindRequests.Should().ContainSingle().Which.Should().BeSameAs(request);
        targetConnectionStrings.Should().ContainSingle().Which.Should().Be("Host=replacement");
        dataStore.CommandRequests.Should().HaveCount(2);
        dataStore
            .CommandRequests.Should()
            .AllSatisfy(capturedRequest =>
            {
                capturedRequest.TargetContext.TargetKey.DataStoreId.Should().Be(new DataStoreId(7));
                capturedRequest.TargetContext.TargetKey.TenantKey.Should().Be("tenant-a");
                capturedRequest
                    .TargetContext.TargetDataStore.Should()
                    .Be(new DocumentCacheMaterializationTargetDataStore("Host=initial"));
            });
        dataStore
            .HydrationRequests.Should()
            .ContainSingle()
            .Which.TargetContext.TargetDataStore.Should()
            .Be(new DocumentCacheMaterializationTargetDataStore("Host=initial"));
        dataStore.HydrationPlans.Should().ContainSingle().Which.Should().BeSameAs(testContext.ReadPlan);
        dataStore
            .HydrationKeysets.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new PageKeysetSpec.Single(DocumentId));
        dataStore
            .HydrationExecutionOptions.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: true,
                    IncludeDocumentReferenceLookup: true,
                    UseSingleDocumentFastPath: true
                )
            );
    }

    [TestCaseSource(nameof(SelectedRequiredContentVersionCases))]
    public async Task It_ignores_selected_required_content_version_when_materializing_current_source(
        long? selectedRequiredContentVersion
    )
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateOrdinarySource(testContext);
        var expectedDocumentJson = JsonNode
            .Parse(
                """
                {"id":"11111111-2222-3333-4444-555555555555","_lastModifiedDate":"2026-07-30T14:15:16Z","nameOfInstitution":"Lincoln High"}
                """
            )!
            .AsObject();
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source)
        );
        var hydrator = new RecordingDocumentHydrator
        {
            Result = CreateHydratedPage(testContext.ReadPlan, source),
        };
        var readMaterializer = new RecordingReadMaterializer { Result = expectedDocumentJson };
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new ThrowingDescriptorHydrator(),
            hydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(
            CreateRequest(testContext.MappingSet, selectedRequiredContentVersion)
        );

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        success.Candidate.ContentVersion.Should().Be(ContentVersion);
        success.Candidate.LastModifiedAt.Should().Be(LastModifiedAt);
        success.Candidate.DocumentJson.Should().BeSameAs(expectedDocumentJson);
        sourceReader
            .CapturedRequest!.SelectedRequiredContentVersion.Should()
            .Be(selectedRequiredContentVersion);
        hydrator.CallCount.Should().Be(1);
        readMaterializer.CallCount.Should().Be(1);
        servedEtagComposer
            .CapturedContext.Should()
            .Be(
                new ServedEtagContext(
                    testContext.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    ProfileName: null,
                    LinksEnabled: true,
                    ContentVersion,
                    ResponseContentCoding.Identity
                )
            );
    }

    [Test]
    public async Task It_returns_missing_source_without_hydrating_when_the_metadata_reader_finds_no_source()
    {
        var testContext = CreateMaterializerTestContext();
        var sourceReader = new StubSourceMetadataReader(
            DocumentCacheSourceMetadataReadResult.MissingSource.Instance
        );
        var hydrator = new RecordingDocumentHydrator();
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new ThrowingDescriptorHydrator(),
            hydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
        hydrator.CallCount.Should().Be(0);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    [Test]
    public async Task It_returns_source_changed_when_final_metadata_differs_after_hydration()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateOrdinarySource(testContext);
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            new DocumentCacheCurrentSourceMetadataReadResult.Found(
                new DocumentCacheCurrentSourceMetadata(
                    source.DocumentId,
                    source.DocumentUuid,
                    source.ResourceKeyId,
                    source.ContentVersion + 1,
                    source.ContentLastModifiedAt
                )
            )
        );
        var hydrator = new RecordingDocumentHydrator
        {
            Result = CreateHydratedPage(testContext.ReadPlan, source),
        };
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new ThrowingDescriptorHydrator(),
            hydrator,
            readMaterializer,
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    [Test]
    public async Task It_throws_projection_processing_failure_when_stable_metadata_has_no_root_body_row()
    {
        var testContext = CreateMaterializerTestContext();
        var source = CreateOrdinarySource(testContext);
        var sourceReader = new StubSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source)
        );
        var hydrator = new RecordingDocumentHydrator
        {
            Result = CreateHydratedPage(testContext.ReadPlan, source, rootRows: []),
        };
        var readMaterializer = new RecordingReadMaterializer();
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new ThrowingDescriptorHydrator(),
            hydrator,
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
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("5.2.0");
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    private static IEnumerable<long?> SelectedRequiredContentVersionCases()
    {
        yield return null;
        yield return 1L;
        yield return ContentVersion;
        yield return ContentVersion + 1;
    }

    private static DocumentCacheMaterializationRequest CreateRequest(
        MappingSet mappingSet,
        long? selectedRequiredContentVersion = null
    ) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            DocumentId,
            selectedRequiredContentVersion,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            CancellationToken.None
        );

    private static DocumentCacheResolvedSourceMetadata.OrdinaryResource CreateOrdinarySource(
        MaterializerTestContext testContext
    ) =>
        new(
            DocumentId,
            new DocumentUuid(DocumentGuid),
            ResourceKeyId,
            testContext.ResourceKey,
            testContext.ConcreteResourceModel,
            ContentVersion,
            LastModifiedAt,
            testContext.ReadPlan
        );

    private static HydratedPage CreateHydratedPage(
        ResourceReadPlan readPlan,
        DocumentCacheResolvedSourceMetadata.OrdinaryResource source,
        IReadOnlyList<object?[]>? rootRows = null
    ) =>
        new(
            TotalCount: null,
            DocumentMetadata:
            [
                new DocumentMetadataRow(
                    source.DocumentId,
                    source.DocumentUuid.Value,
                    source.ContentVersion,
                    source.ContentLastModifiedAt,
                    source.ResourceKeyId
                ),
            ],
            TableRowsInDependencyOrder:
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    rootRows ?? [new object?[] { source.DocumentId, "Lincoln High" }]
                ),
            ],
            DescriptorRowsInPlanOrder:
            [
                new HydratedDescriptorRows([
                    new DescriptorUriRow(601L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"),
                ]),
            ]
        )
        {
            DocumentReferenceLookup = new HydratedDocumentReferenceLookup([
                new DocumentReferenceLookupRow(901L, Guid.Parse("99999999-8888-7777-6666-555555555555"), 30),
            ]),
        };

    private static InMemoryRelationalResultSet CreateSourceMetadataResultSet(
        DocumentCacheResolvedSourceMetadata source
    ) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["DocumentId"] = source.DocumentId,
                ["DocumentUuid"] = source.DocumentUuid.Value,
                ["ResourceKeyId"] = source.ResourceKeyId,
                ["ContentVersion"] = source.ContentVersion,
                ["ContentLastModifiedAt"] = source.ContentLastModifiedAt,
            }
        );

    private static MaterializerTestContext CreateMaterializerTestContext()
    {
        var readPlan = CreateReadPlan();
        var resourceKey = new ResourceKeyEntry(ResourceKeyId, SchoolResource, "5.2.0", false);
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            readPlan.Model
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

        return new MaterializerTestContext(mappingSet, readPlan, resourceKey, concreteResourceModel);
    }

    private static ResourceReadPlan CreateReadPlan()
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
                new DbColumnModel(
                    new DbColumnName("NameOfInstitution"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String),
                    false,
                    new JsonPathExpression(
                        "$.nameOfInstitution",
                        [new JsonPathSegment.Property("nameOfInstitution")]
                    ),
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
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(rootTable, "select DocumentId")],
            [],
            []
        );
    }

    private sealed record MaterializerTestContext(
        MappingSet MappingSet,
        ResourceReadPlan ReadPlan,
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

    private sealed class ThrowingDescriptorHydrator : IDocumentCacheDescriptorHydrator
    {
        public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
            DocumentCacheMaterializationRequest request,
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException(
                "Ordinary resource hydration tests must not hydrate descriptors."
            );
    }

    private sealed class RecordingDocumentHydrator : IDocumentCacheMaterializationDataStore
    {
        public HydratedPage Result { get; init; } = null!;

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int CallCount { get; private set; }

        public DocumentCacheMaterializationRequest? CapturedRequest { get; private set; }

        public ResourceReadPlan? CapturedPlan { get; private set; }

        public PageKeysetSpec? CapturedKeyset { get; private set; }

        public HydrationExecutionOptions? CapturedExecutionOptions { get; private set; }

        public Task<TResult> ExecuteReaderAsync<TResult>(
            DocumentCacheMaterializationRequest request,
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException(
                "Ordinary resource hydration tests provide source metadata through a stub reader."
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
            CapturedRequest = request;
            CapturedPlan = plan;
            CapturedKeyset = keyset;
            CapturedExecutionOptions = executionOptions;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingReadMaterializer : IRelationalReadMaterializer
    {
        public JsonNode Result { get; init; } = JsonNode.Parse("""{}""")!;

        public int CallCount { get; private set; }

        public RelationalReadMaterializationRequest? CapturedRequest { get; private set; }

        public JsonNode Materialize(RelationalReadMaterializationRequest request)
        {
            CallCount++;
            CapturedRequest = request;
            return Result;
        }

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) =>
            throw new NotSupportedException(
                "DocumentCacheMaterializer tests use single-document Materialize."
            );

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) { }
    }

    private sealed class RecordingServedEtagComposer(string returnValue) : IServedEtagComposer
    {
        public ServedEtagContext? CapturedContext { get; private set; }

        public int CallCount { get; private set; }

        public string Compose(ServedEtagContext context)
        {
            CallCount++;
            CapturedContext = context;
            return returnValue;
        }
    }
}
