// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
public class Given_DocumentCacheMaterializer_InvariantValidation
{
    private const long DocumentId = 123L;
    private const short ResourceKeyId = 11;
    private const long ContentVersion = 987L;
    private static readonly Guid DocumentGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 7, 30, 14, 15, 16, TimeSpan.Zero);
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    [Test]
    public async Task It_throws_Invariant_failure_when_DocumentJson_is_not_a_JSON_object()
    {
        var testContext = CreateMaterializerTestContext();
        var readMaterializer = new RecordingReadMaterializer { Result = new JsonArray("not-an-object") };
        var servedEtagComposer = new SequencedServedEtagComposer("stream-etag");
        var sut = CreateSut(testContext, readMaterializer, servedEtagComposer);

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.DocumentJsonNotObject);
        servedEtagComposer.CapturedContexts.Should().BeEmpty();
    }

    [Test]
    public async Task It_throws_Invariant_failure_when_DocumentJson_id_does_not_match_canonical_DocumentUuid()
    {
        var testContext = CreateMaterializerTestContext();
        var documentJson = CreateValidDocumentJson(testContext.Source);
        documentJson["id"] = "22222222-2222-3333-4444-555555555555";
        var sut = CreateSut(testContext, new RecordingReadMaterializer { Result = documentJson });

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.DocumentJsonIdMismatch);
    }

    [Test]
    public async Task It_accepts_LastModifiedDate_as_whole_second_UTC_text_without_rounding()
    {
        var sourceLastModifiedAt = new DateTimeOffset(2026, 7, 30, 9, 15, 16, 900, TimeSpan.FromHours(-5));
        var testContext = CreateMaterializerTestContext(sourceLastModifiedAt: sourceLastModifiedAt);
        var documentJson = CreateValidDocumentJson(testContext.Source);
        documentJson["_lastModifiedDate"] = "2026-07-30T14:15:16Z";
        var sut = CreateSut(testContext, new RecordingReadMaterializer { Result = documentJson });

        var result = await sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        success.Candidate.LastModifiedAt.Should().Be(sourceLastModifiedAt);
        success.Candidate.DocumentJson["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be("2026-07-30T14:15:16Z");
    }

    [Test]
    public async Task It_throws_Invariant_failure_when_DocumentJson_LastModifiedDate_is_not_the_source_formatter_value()
    {
        var sourceLastModifiedAt = new DateTimeOffset(2026, 7, 30, 9, 15, 16, 900, TimeSpan.FromHours(-5));
        var testContext = CreateMaterializerTestContext(sourceLastModifiedAt: sourceLastModifiedAt);
        var documentJson = CreateValidDocumentJson(testContext.Source);
        documentJson["_lastModifiedDate"] = "2026-07-30T14:15:17Z";
        var sut = CreateSut(testContext, new RecordingReadMaterializer { Result = documentJson });

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception
            .Reason.Should()
            .Be(DocumentCacheProjectionProcessingFailureReason.DocumentJsonLastModifiedDateMismatch);
    }

    [Test]
    public async Task It_throws_Invariant_failure_when_DocumentJson_contains_an_etag()
    {
        var testContext = CreateMaterializerTestContext();
        var documentJson = CreateValidDocumentJson(testContext.Source);
        documentJson["_etag"] = "do-not-store";
        var sut = CreateSut(testContext, new RecordingReadMaterializer { Result = documentJson });

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag);
    }

    [Test]
    public async Task It_throws_Invariant_failure_when_StreamEtag_does_not_match_the_fixed_cache_representation()
    {
        var testContext = CreateMaterializerTestContext();
        var servedEtagComposer = new SequencedServedEtagComposer("candidate-etag", "expected-etag");
        var sut = CreateSut(
            testContext,
            new RecordingReadMaterializer { Result = CreateValidDocumentJson(testContext.Source) },
            servedEtagComposer
        );

        Func<Task> act = () => sut.MaterializeAsync(CreateRequest(testContext.MappingSet));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.StreamEtagMismatch);
        servedEtagComposer.CapturedContexts.Should().HaveCount(2);
        servedEtagComposer
            .CapturedContexts.Should()
            .OnlyContain(context =>
                context
                == new ServedEtagContext(
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
    public async Task It_throws_Invariant_failure_with_bounded_Diagnostics_without_DocumentJson()
    {
        var testContext = CreateMaterializerTestContext();
        var documentJson = CreateValidDocumentJson(testContext.Source);
        documentJson["_etag"] = "do-not-store";
        documentJson["authorizationDataThatMustNotLeak"] = "secret";
        var sut = CreateSut(testContext, new RecordingReadMaterializer { Result = documentJson });

        Func<Task> act = () =>
            sut.MaterializeAsync(CreateRequest(testContext.MappingSet, selectedRequiredContentVersion: 456));

        var exception = (
            await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
        ).Subject.Single();
        exception.Reason.Should().Be(DocumentCacheProjectionProcessingFailureReason.DocumentJsonContainsEtag);
        exception
            .FailureMetadata.TargetKey.Should()
            .Be(new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)));
        exception.FailureMetadata.MappingSetKey.Should().Be(testContext.MappingSet.Key);
        exception.FailureMetadata.DocumentId.Should().Be(DocumentId);
        exception.FailureMetadata.SelectedRequiredContentVersion.Should().Be(456);
        exception.FailureMetadata.ResourceKeyId.Should().Be(ResourceKeyId);
        exception.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        exception.FailureMetadata.ResourceName.Should().Be("School");
        exception.FailureMetadata.ResourceVersion.Should().Be("5.2.0");
        exception.Message.Should().NotContain("secret");
        exception.Message.Should().NotContain("Lincoln High");
        typeof(DocumentCacheMaterializerFailureMetadata)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain("DocumentJson");
    }

    private static DocumentCacheMaterializer CreateSut(
        MaterializerTestContext testContext,
        RecordingReadMaterializer readMaterializer,
        SequencedServedEtagComposer? servedEtagComposer = null
    ) =>
        new(
            new StubSourceMetadataReader(new DocumentCacheSourceMetadataReadResult.Found(testContext.Source)),
            new ThrowingDescriptorHydrator(),
            new SuccessfulDocumentHydrator(CreateHydratedPage(testContext.ReadPlan, testContext.Source)),
            readMaterializer,
            servedEtagComposer ?? new SequencedServedEtagComposer("stream-etag")
        );

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

    private static JsonObject CreateValidDocumentJson(DocumentCacheResolvedSourceMetadata source) =>
        new()
        {
            ["id"] = source.DocumentUuid.Value.ToString(),
            ["_lastModifiedDate"] = FormatLastModifiedDate(source.ContentLastModifiedAt),
            ["nameOfInstitution"] = "Lincoln High",
        };

    private static string FormatLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static MaterializerTestContext CreateMaterializerTestContext(
        DateTimeOffset? sourceLastModifiedAt = null,
        QualifiedResourceName? readPlanResource = null
    )
    {
        var resourceKey = new ResourceKeyEntry(ResourceKeyId, SchoolResource, "5.2.0", false);
        var readPlan = CreateReadPlan(readPlanResource ?? SchoolResource);
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
        var mappingSet = new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                SqlDialect.Pgsql,
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
        var source = new DocumentCacheResolvedSourceMetadata.OrdinaryResource(
            DocumentId,
            new DocumentUuid(DocumentGuid),
            ResourceKeyId,
            resourceKey,
            concreteResourceModel,
            ContentVersion,
            sourceLastModifiedAt ?? LastModifiedAt,
            readPlan
        );

        return new MaterializerTestContext(mappingSet, readPlan, source);
    }

    private static HydratedPage CreateHydratedPage(
        ResourceReadPlan readPlan,
        DocumentCacheResolvedSourceMetadata.OrdinaryResource source
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
                    [new object?[] { source.DocumentId, "Lincoln High" }]
                ),
            ],
            DescriptorRowsInPlanOrder: []
        );

    private static ResourceReadPlan CreateReadPlan(QualifiedResourceName resource)
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), resource.ResourceName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{resource.ResourceName}",
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
                resource,
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
        DocumentCacheResolvedSourceMetadata.OrdinaryResource Source
    );

    private sealed class StubSourceMetadataReader(DocumentCacheSourceMetadataReadResult result)
        : IDocumentCacheSourceMetadataReader
    {
        public Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(result);

        public Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        ) =>
            result switch
            {
                DocumentCacheSourceMetadataReadResult.Found found =>
                    Task.FromResult<DocumentCacheCurrentSourceMetadataReadResult>(
                        new DocumentCacheCurrentSourceMetadataReadResult.Found(
                            new DocumentCacheCurrentSourceMetadata(
                                found.Metadata.DocumentId,
                                found.Metadata.DocumentUuid,
                                found.Metadata.ResourceKeyId,
                                found.Metadata.ContentVersion,
                                found.Metadata.ContentLastModifiedAt
                            )
                        )
                    ),
                _ => Task.FromResult<DocumentCacheCurrentSourceMetadataReadResult>(
                    DocumentCacheCurrentSourceMetadataReadResult.MissingSource.Instance
                ),
            };
    }

    private sealed class ThrowingDescriptorHydrator : IDocumentCacheDescriptorHydrator
    {
        public Task<DocumentCacheDescriptorHydrationResult> HydrateAsync(
            DocumentCacheMaterializationRequest request,
            DocumentCacheResolvedSourceMetadata.DescriptorResource source,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Invariant validation tests use ordinary resources.");
    }

    private sealed class SuccessfulDocumentHydrator(HydratedPage result)
        : IDocumentCacheMaterializationDataStore
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            DocumentCacheMaterializationRequest request,
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException(
                "Invariant validation tests provide source metadata through a stub reader."
            );

        public Task<HydratedPage> HydrateAsync(
            DocumentCacheMaterializationRequest request,
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(result);
    }

    private sealed class RecordingReadMaterializer : IRelationalReadMaterializer
    {
        public JsonNode Result { get; init; } = null!;

        public JsonNode Materialize(RelationalReadMaterializationRequest request) => Result;

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) => throw new NotSupportedException("Invariant validation tests use single-document Materialize.");

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) { }
    }

    private sealed class SequencedServedEtagComposer(params string[] returnValues) : IServedEtagComposer
    {
        private int _returnValueIndex;

        public List<ServedEtagContext> CapturedContexts { get; } = [];

        public string Compose(ServedEtagContext context)
        {
            CapturedContexts.Add(context);
            var returnValue = returnValues[Math.Min(_returnValueIndex, returnValues.Length - 1)];
            _returnValueIndex++;
            return returnValue;
        }
    }
}
