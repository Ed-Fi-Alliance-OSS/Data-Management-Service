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
public class Given_DocumentCacheMaterializer_Coherence
{
    private const long DocumentId = 123L;
    private const short OrdinaryResourceKeyId = 11;
    private const short DescriptorResourceKeyId = 13;
    private const long ContentVersion = 987L;
    private static readonly Guid DocumentGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    public async Task It_returns_missing_source_when_the_final_metadata_read_finds_no_canonical_row()
    {
        var testContext = CreateMaterializerTestContext();
        var source = testContext.OrdinarySource;
        var sourceReader = new SequencedSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance
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

        result.Should().BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
        hydrator.CallCount.Should().Be(1);
        readMaterializer.CallCount.Should().Be(0);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    [TestCase("DocumentUuid")]
    [TestCase("ResourceKeyId")]
    [TestCase("ContentVersion")]
    [TestCase("ContentLastModifiedAt")]
    public async Task It_returns_source_changed_when_the_final_metadata_read_differs(string changedField)
    {
        var testContext = CreateMaterializerTestContext();
        var source = testContext.OrdinarySource;
        var sourceReader = new SequencedSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            new DocumentCacheCurrentSourceMetadataReadResult.Found(
                CreateChangedMetadata(source, changedField)
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
    public async Task It_throws_projection_processing_failure_when_final_metadata_is_stable_but_the_root_body_is_missing()
    {
        var testContext = CreateMaterializerTestContext();
        var source = testContext.OrdinarySource;
        var sourceReader = new SequencedSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            new DocumentCacheCurrentSourceMetadataReadResult.Found(CreateCurrentMetadata(source))
        );
        var hydrator = new RecordingDocumentHydrator
        {
            Result = CreateHydratedPage(testContext.ReadPlan, source, rootRows: []),
        };
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new ThrowingDescriptorHydrator(),
            hydrator,
            new RecordingReadMaterializer(),
            new RecordingServedEtagComposer("stream-etag")
        );

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.StableSourceBodyMissing);
    }

    [Test]
    public async Task It_returns_source_changed_for_descriptors_when_the_final_metadata_read_differs()
    {
        var testContext = CreateMaterializerTestContext();
        var source = testContext.DescriptorSource;
        var sourceReader = new SequencedSourceMetadataReader(
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
        var descriptorHydrator = new RecordingDescriptorHydrator
        {
            Result = new DocumentCacheDescriptorHydrationResult.Found(CreateDescriptorRow(source)),
        };
        var servedEtagComposer = new RecordingServedEtagComposer("stream-etag");
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            descriptorHydrator,
            new ThrowingDocumentHydrator(),
            new RecordingReadMaterializer(),
            servedEtagComposer
        );

        var result = await sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance);
        descriptorHydrator.CallCount.Should().Be(1);
        servedEtagComposer.CapturedContext.Should().BeNull();
    }

    [Test]
    public async Task It_throws_projection_processing_failure_when_final_descriptor_metadata_is_stable_but_the_body_is_missing()
    {
        var testContext = CreateMaterializerTestContext();
        var source = testContext.DescriptorSource;
        var sourceReader = new SequencedSourceMetadataReader(
            new DocumentCacheSourceMetadataReadResult.Found(source),
            new DocumentCacheCurrentSourceMetadataReadResult.Found(CreateCurrentMetadata(source))
        );
        var sut = new DocumentCacheMaterializer(
            sourceReader,
            new RecordingDescriptorHydrator
            {
                Result = DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance,
            },
            new ThrowingDocumentHydrator(),
            new RecordingReadMaterializer(),
            new RecordingServedEtagComposer("stream-etag")
        );

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.StableSourceBodyMissing);
    }

    private static DocumentCacheMaterializationRequest CreateRequest(MappingSet mappingSet) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet
            ),
            DocumentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.DurableWorkProjection,
            CancellationToken.None
        );

    private static DocumentCacheCurrentSourceMetadata CreateChangedMetadata(
        DocumentCacheResolvedSourceMetadata source,
        string changedField
    ) =>
        changedField switch
        {
            "DocumentUuid" => new DocumentCacheCurrentSourceMetadata(
                source.DocumentId,
                new DocumentUuid(Guid.Parse("22222222-2222-3333-4444-555555555555")),
                source.ResourceKeyId,
                source.ContentVersion,
                source.ContentLastModifiedAt
            ),
            "ResourceKeyId" => new DocumentCacheCurrentSourceMetadata(
                source.DocumentId,
                source.DocumentUuid,
                (short)(source.ResourceKeyId + 1),
                source.ContentVersion,
                source.ContentLastModifiedAt
            ),
            "ContentVersion" => new DocumentCacheCurrentSourceMetadata(
                source.DocumentId,
                source.DocumentUuid,
                source.ResourceKeyId,
                source.ContentVersion + 1,
                source.ContentLastModifiedAt
            ),
            "ContentLastModifiedAt" => new DocumentCacheCurrentSourceMetadata(
                source.DocumentId,
                source.DocumentUuid,
                source.ResourceKeyId,
                source.ContentVersion,
                source.ContentLastModifiedAt.AddSeconds(1)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(changedField), changedField, "Unknown field."),
        };

    private static DocumentCacheCurrentSourceMetadata CreateCurrentMetadata(
        DocumentCacheResolvedSourceMetadata source
    ) =>
        new(
            source.DocumentId,
            source.DocumentUuid,
            source.ResourceKeyId,
            source.ContentVersion,
            source.ContentLastModifiedAt
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
                    source.ContentVersion,
                    source.ContentLastModifiedAt,
                    source.ContentLastModifiedAt
                ),
            ],
            TableRowsInDependencyOrder:
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    rootRows ?? [new object?[] { source.DocumentId, "Lincoln High" }]
                ),
            ],
            DescriptorRowsInPlanOrder: []
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
            null,
            null,
            "SchoolTypeDescriptor"
        );

    private static MaterializerTestContext CreateMaterializerTestContext()
    {
        var readPlan = CreateReadPlan();
        var ordinaryKey = new ResourceKeyEntry(OrdinaryResourceKeyId, SchoolResource, "5.2.0", false);
        var descriptorKey = new ResourceKeyEntry(DescriptorResourceKeyId, DescriptorResource, "5.2.0", false);
        var ordinaryModel = new ConcreteResourceModel(
            ordinaryKey,
            ResourceStorageKind.RelationalTables,
            readPlan.Model
        );
        var descriptorModel = new ConcreteResourceModel(
            descriptorKey,
            ResourceStorageKind.SharedDescriptorTable,
            CreateDescriptorRelationalModel()
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 2,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [ordinaryKey, descriptorKey]
        );
        var mappingSet = new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [ordinaryModel, descriptorModel],
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
                [SchoolResource] = OrdinaryResourceKeyId,
                [DescriptorResource] = DescriptorResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [OrdinaryResourceKeyId] = ordinaryKey,
                [DescriptorResourceKeyId] = descriptorKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

        return new MaterializerTestContext(
            mappingSet,
            readPlan,
            new DocumentCacheResolvedSourceMetadata.OrdinaryResource(
                DocumentId,
                new DocumentUuid(DocumentGuid),
                OrdinaryResourceKeyId,
                ordinaryKey,
                ordinaryModel,
                ContentVersion,
                LastModifiedAt,
                readPlan
            ),
            new DocumentCacheResolvedSourceMetadata.DescriptorResource(
                DocumentId,
                new DocumentUuid(DocumentGuid),
                DescriptorResourceKeyId,
                descriptorKey,
                descriptorModel,
                ContentVersion,
                LastModifiedAt
            )
        );
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

    private sealed record MaterializerTestContext(
        MappingSet MappingSet,
        ResourceReadPlan ReadPlan,
        DocumentCacheResolvedSourceMetadata.OrdinaryResource OrdinarySource,
        DocumentCacheResolvedSourceMetadata.DescriptorResource DescriptorSource
    );

    private sealed class SequencedSourceMetadataReader(
        DocumentCacheSourceMetadataReadResult initialResult,
        DocumentCacheCurrentSourceMetadataReadResult finalResult
    ) : IDocumentCacheSourceMetadataReader
    {
        public Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(initialResult);

        public Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(finalResult);
    }

    private sealed class RecordingDocumentHydrator : IDocumentHydrator
    {
        public HydratedPage Result { get; init; } = null!;

        public int CallCount { get; private set; }

        public Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingDocumentHydrator : IDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        ) => throw new NotSupportedException("Descriptor coherence tests must not use ordinary hydration.");
    }

    private sealed class RecordingDescriptorHydrator : IDocumentCacheDescriptorHydrator
    {
        public DocumentCacheDescriptorHydrationResult Result { get; init; } =
            DocumentCacheDescriptorHydrationResult.StableDescriptorBodyMissing.Instance;

        public int CallCount { get; private set; }

        public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            SqlDialect dialect,
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingDescriptorHydrator : IDocumentCacheDescriptorHydrator
    {
        public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            SqlDialect dialect,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Ordinary coherence tests must not hydrate descriptors.");
    }

    private sealed class RecordingReadMaterializer : IRelationalReadMaterializer
    {
        public int CallCount { get; private set; }

        public JsonNode Materialize(RelationalReadMaterializationRequest request)
        {
            CallCount++;
            return JsonNode.Parse(
                """
                {"id":"11111111-2222-3333-4444-555555555555","_lastModifiedDate":"2026-07-30T14:15:16Z"}
                """
            )!;
        }

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) => throw new NotSupportedException("Coherence tests use single-document Materialize.");

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
