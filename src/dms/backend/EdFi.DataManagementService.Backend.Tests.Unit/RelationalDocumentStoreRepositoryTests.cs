// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalDocumentStoreRepositoryTests
{
    private static readonly ResourceInfo _schoolResourceInfo = CreateResourceInfo("School");
    private const string StampStyleEtagPattern = "^\"\\d+\"$";
    private static readonly BaseResourceInfo _localEducationAgencyResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("LocalEducationAgency"),
        false
    );
    private static readonly BaseResourceInfo _schoolCategoryDescriptorResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("SchoolCategoryDescriptor"),
        true
    );
    private static readonly ResourceInfo _descriptorResourceInfo = CreateResourceInfo(
        "SchoolTypeDescriptor",
        isDescriptor: true
    );

    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteExecutor _writeExecutor = null!;
    private RecordingRelationalWriteTargetLookupService _targetLookupService = null!;
    private IReferenceResolver _referenceResolver = null!;
    private IDocumentHydrator _documentHydrator = null!;
    private IRelationalReadTargetLookupService _readTargetLookupService = null!;
    private IRelationalReadMaterializer _readMaterializer = null!;
    private IReadableProfileProjector _readableProfileProjector = null!;
    private IRelationalCommandExecutor _commandExecutor = null!;
    private ConfigurableRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
    private RelationalWriteExecutorRequest _capturedExecutorRequest = null!;
    private List<RelationalWriteExecutorRequest> _capturedExecutorRequests = null!;

    [SetUp]
    public void Setup()
    {
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _targetLookupService = new RecordingRelationalWriteTargetLookupService();
        _referenceResolver = A.Fake<IReferenceResolver>();
        _documentHydrator = A.Fake<IDocumentHydrator>();
        _readTargetLookupService = A.Fake<IRelationalReadTargetLookupService>();
        _readMaterializer = A.Fake<IRelationalReadMaterializer>();
        _readableProfileProjector = A.Fake<IReadableProfileProjector>();
        _commandExecutor = A.Fake<IRelationalCommandExecutor>();
        _writeExceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier();
        _capturedExecutorRequests = [];
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UnknownFailure("Unexpected write-executor test fallback.")
                    )
                )
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            new DefaultDescriptorWriteHandler(),
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _commandExecutor,
            _writeExceptionClassifier
        );
    }

    [Test]
    public async Task It_materializes_successful_get_requests_through_the_single_document_read_path()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var resourceAuthorizationHandler = new RecordingResourceAuthorizationHandler();
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            resourceAuthorizationHandler
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
        );
        var materializedDocument = JsonNode.Parse("""{"id":"hydrated","name":"Lincoln High"}""")!;
        RelationalReadMaterializationRequest capturedReadRequest = null!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call => capturedReadRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!)
            .Returns(materializedDocument);

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeEquivalentTo(
                new GetResult.GetSuccess(
                    documentUuid,
                    materializedDocument,
                    new DateTime(2026, 4, 11, 17, 30, 45, DateTimeKind.Utc),
                    null
                )
            );
        capturedReadRequest.ReadPlan.Should().BeSameAs(readPlan);
        capturedReadRequest.DocumentMetadata.Should().Be(hydratedPage.DocumentMetadata[0]);
        capturedReadRequest
            .TableRowsInDependencyOrder.Should()
            .BeSameAs(hydratedPage.TableRowsInDependencyOrder);
        capturedReadRequest
            .DescriptorRowsInPlanOrder.Should()
            .BeSameAs(hydratedPage.DescriptorRowsInPlanOrder);
        capturedReadRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.ExternalResponse);
        resourceAuthorizationHandler.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_a_precise_not_implemented_failure_for_descriptor_get_requests()
    {
        var getRequest = A.Fake<IRelationalGetRequest>();
        A.CallTo(() => getRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => getRequest.ResourceInfo)
            .Returns(
                new BaseResourceInfo(
                    _descriptorResourceInfo.ProjectName,
                    _descriptorResourceInfo.ResourceName,
                    _descriptorResourceInfo.IsDescriptor
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeEquivalentTo(
                new GetResult.GetFailureNotImplemented(
                    "Relational descriptor GET by id is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor'."
                )
            );
    }

    [Test]
    public async Task It_passes_stored_document_mode_through_for_internal_get_requests()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-cccccccccccc"));
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            new RecordingResourceAuthorizationHandler(),
            RelationalGetRequestReadMode.StoredDocument
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 92L),
            (345L, "Roosevelt High")
        );
        RelationalReadMaterializationRequest capturedReadRequest = null!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call => capturedReadRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!)
            .Returns(JsonNode.Parse("""{"name":"Roosevelt High"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        capturedReadRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.StoredDocument);
    }

    [Test]
    public async Task It_recomputes_etag_after_readable_profile_projection_while_preserving_other_metadata()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var mappingSet = CreateProfileProjectionOrderSensitiveMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var expectedProjectedEtag = RelationalApiMetadataFormatter.FormatEtag(
            JsonNode.Parse("""{"schoolId":255901,"nameOfInstitution":"Lincoln High"}""")!
        );
        var projectionContext = new ReadableProfileProjectionContext(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("nameOfInstitution")],
                [],
                [],
                []
            ),
            new HashSet<string> { "schoolId" }
        );
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            new RecordingResourceAuthorizationHandler(),
            readableProfileProjectionContext: projectionContext
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 93L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901, "Lincoln High"],
                    ]
                ),
            ],
            []
        );
        var materializedDocument = JsonNode.Parse(
            """
            {
              "id": "cccccccc-1111-2222-3333-dddddddddddd",
              "_etag": "\"93\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High",
              "webSite": "https://example.com"
            }
            """
        )!;
        var projectedDocument = JsonNode.Parse(
            """
            {
              "id": "cccccccc-1111-2222-3333-dddddddddddd",
              "_etag": "\"93\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High"
            }
            """
        )!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(materializedDocument);
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    materializedDocument,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .Returns(projectedDocument);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        var success = (GetResult.GetSuccess)result;
        success.EdfiDoc.Should().BeSameAs(projectedDocument);
        success.LastModifiedDate.Should().Be(new DateTime(2026, 4, 11, 17, 30, 45, DateTimeKind.Utc));
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(documentUuid.Value.ToString());
        success.EdfiDoc["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-04-11T17:30:45Z");
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be(expectedProjectedEtag);
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().NotBe("\"93\"");
        success.EdfiDoc["ChangeVersion"].Should().BeNull();
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    materializedDocument,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_returns_not_exists_for_missing_get_targets()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            new RecordingResourceAuthorizationHandler()
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.NotFound());

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_translates_wrong_resource_get_targets_to_not_exists()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            new RecordingResourceAuthorizationHandler()
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.WrongResource(
                    documentUuid,
                    new QualifiedResourceName("Ed-Fi", "LocalEducationAgency")
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_missing_read_plan_guard_rail_for_get_requests()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        const string expectedFailureMessage =
            "Read plan lookup failed for resource 'Ed-Fi.School' in mapping set "
            + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table read plan, "
            + "but no entry was found. This indicates an internal compilation/selection bug.";
        var getRequest = CreateGetRequest(
            documentUuid,
            CreateMissingReadPlanMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            new RecordingResourceAuthorizationHandler()
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeEquivalentTo(new GetResult.UnknownFailure(expectedFailureMessage));
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_precise_unknown_failure_for_delete_requests()
    {
        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_schoolResourceInfo);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeEquivalentTo(
                new DeleteResult.UnknownFailure(
                    "Relational DELETE is not implemented for resource 'Ed-Fi.School'."
                )
            );
    }

    [Test]
    public async Task It_returns_a_precise_not_implemented_failure_for_query_requests()
    {
        var firstDocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-eeeeeeeeeeee"));
        var secondDocumentUuid = new DocumentUuid(Guid.Parse("eeeeeeee-1111-2222-3333-ffffffffffff"));
        var mappingSet = CreateQuerySupportedMappingSet(
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "name",
                "$.name",
                "string",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Name"))
            )
        );
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("name", "$.name", "Lincoln High", "string")],
            totalCount: true
        );
        var hydratedPage = new HydratedPage(
            7,
            [
                CreateDocumentMetadataRow(firstDocumentUuid, 345L, 91L),
                CreateDocumentMetadataRow(secondDocumentUuid, 678L, 92L),
            ],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, "Lincoln High"],
                        [678L, "Roosevelt High"],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;
        RelationalReadPageMaterializationRequest capturedReadRequest = null!;

        A.CallTo(() => _documentHydrator.HydrateAsync(readPlan, A<PageKeysetSpec>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Relational query execution should hydrate through PageKeysetSpec.Query."
                    );
            })
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Invokes(call =>
                capturedReadRequest = call.GetArgument<RelationalReadPageMaterializationRequest>(0)!
            )
            .Returns([
                new MaterializedDocument(
                    hydratedPage.DocumentMetadata[0],
                    JsonNode.Parse($$"""{"id":"{{firstDocumentUuid.Value}}"}""")!
                ),
                new MaterializedDocument(
                    hydratedPage.DocumentMetadata[1],
                    JsonNode.Parse($$"""{"id":"{{secondDocumentUuid.Value}}"}""")!
                ),
            ]);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();

        var success = (QueryResult.QuerySuccess)result;

        success.TotalCount.Should().Be(7);
        success
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(firstDocumentUuid.Value.ToString(), secondDocumentUuid.Value.ToString());
        capturedKeyset.ParameterValues["name"].Should().Be("Lincoln High");
        capturedKeyset.ParameterValues["offset"].Should().Be(0L);
        capturedKeyset.ParameterValues["limit"].Should().Be(25L);
        capturedKeyset.Plan.TotalCountSql.Should().NotBeNull();
        capturedReadRequest.ReadPlan.Should().BeSameAs(readPlan);
        capturedReadRequest.HydratedPage.Should().BeSameAs(hydratedPage);
        capturedReadRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.ExternalResponse);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_executes_mixed_case_root_column_queries_without_returning_unknown_failure()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("abababab-1111-2222-3333-cdcdcdcdcdcd"));
        var mappingSet = CreateQuerySupportedMappingSet(
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "Name",
                "$.name",
                "string",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Name"))
            )
        );
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("nAmE", "$.name", "Lincoln High", "string")],
            totalCount: false
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() => _documentHydrator.HydrateAsync(readPlan, A<PageKeysetSpec>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Mixed-case root-column query should hydrate through PageKeysetSpec.Query."
                    );
            })
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([
                new MaterializedDocument(
                    hydratedPage.DocumentMetadata[0],
                    JsonNode.Parse($$"""{"id":"{{documentUuid.Value}}"}""")!
                ),
            ]);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        capturedKeyset.ParameterValues["name"].Should().Be("Lincoln High");
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [TestCase("1.5")]
    [TestCase("2147483648")]
    public async Task It_returns_an_empty_query_success_when_integer_number_filter_values_cannot_match(
        string rawSchoolId
    )
    {
        var mappingSet = CreateQuerySupportedMappingSet(
            CreateProfileProjectionOrderSensitiveMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "schoolId",
                "$.schoolId",
                "number",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId"))
            )
        );
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("schoolId", "$.schoolId", rawSchoolId, "number")],
            totalCount: true
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();

        var success = (QueryResult.QuerySuccess)result;

        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_applies_readable_profile_projection_to_each_query_result_and_refreshes_etags()
    {
        var firstDocumentUuid = new DocumentUuid(Guid.Parse("12121212-1111-2222-3333-444444444444"));
        var secondDocumentUuid = new DocumentUuid(Guid.Parse("34343434-1111-2222-3333-555555555555"));
        var mappingSet = CreateQuerySupportedMappingSet(
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "name",
                "$.name",
                "string",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Name"))
            )
        );
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var projectionContext = new ReadableProfileProjectionContext(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("nameOfInstitution")],
                [],
                [],
                []
            ),
            new HashSet<string> { "schoolId" }
        );
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("name", "$.name", "Lincoln High", "string")],
            totalCount: false,
            readableProfileProjectionContext: projectionContext
        );
        var hydratedPage = new HydratedPage(
            null,
            [
                CreateDocumentMetadataRow(firstDocumentUuid, 345L, 91L),
                CreateDocumentMetadataRow(secondDocumentUuid, 678L, 92L),
            ],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, "Lincoln High"],
                        [678L, "Roosevelt High"],
                    ]
                ),
            ],
            []
        );
        var materializedFirst = JsonNode.Parse(
            """
            {
              "id": "12121212-1111-2222-3333-444444444444",
              "_etag": "\"91\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High",
              "webSite": "https://example.com/lincoln"
            }
            """
        )!;
        var materializedSecond = JsonNode.Parse(
            """
            {
              "id": "34343434-1111-2222-3333-555555555555",
              "_etag": "\"92\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255902,
              "nameOfInstitution": "Roosevelt High",
              "webSite": "https://example.com/roosevelt"
            }
            """
        )!;
        var projectedFirst = JsonNode.Parse(
            """
            {
              "id": "12121212-1111-2222-3333-444444444444",
              "_etag": "\"91\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High"
            }
            """
        )!;
        var projectedSecond = JsonNode.Parse(
            """
            {
              "id": "34343434-1111-2222-3333-555555555555",
              "_etag": "\"92\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255902,
              "nameOfInstitution": "Roosevelt High"
            }
            """
        )!;
        var expectedFirstProjectedEtag = RelationalApiMetadataFormatter.FormatEtag(projectedFirst);
        var expectedSecondProjectedEtag = RelationalApiMetadataFormatter.FormatEtag(projectedSecond);

        A.CallTo(() => _documentHydrator.HydrateAsync(readPlan, A<PageKeysetSpec>._, A<CancellationToken>._))
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([
                new MaterializedDocument(hydratedPage.DocumentMetadata[0], materializedFirst),
                new MaterializedDocument(hydratedPage.DocumentMetadata[1], materializedSecond),
            ]);
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    A<JsonNode>._,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .ReturnsLazily(
                (JsonNode reconstitutedDocument, ContentTypeDefinition _, IReadOnlySet<string> _) =>
                    reconstitutedDocument["id"]!.GetValue<string>() switch
                    {
                        "12121212-1111-2222-3333-444444444444" => projectedFirst,
                        "34343434-1111-2222-3333-555555555555" => projectedSecond,
                        _ => throw new AssertionException("Unexpected readable profile projection request."),
                    }
            );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        var success = (QueryResult.QuerySuccess)result;
        success.TotalCount.Should().BeNull();
        success.EdfiDocs.Should().HaveCount(2);
        success.EdfiDocs[0].Should().BeSameAs(projectedFirst);
        success.EdfiDocs[1].Should().BeSameAs(projectedSecond);
        success.EdfiDocs[0]!["_etag"]!.GetValue<string>().Should().Be(expectedFirstProjectedEtag);
        success.EdfiDocs[1]!["_etag"]!.GetValue<string>().Should().Be(expectedSecondProjectedEtag);
        success.EdfiDocs[0]!["_etag"]!.GetValue<string>().Should().NotBe("\"91\"");
        success.EdfiDocs[1]!["_etag"]!.GetValue<string>().Should().NotBe("\"92\"");
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    materializedFirst,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    materializedSecond,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_short_circuits_mixed_case_invalid_id_queries_without_returning_unknown_failure()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(
                _schoolResourceInfo,
                CreateSupportedQueryField(
                    "id",
                    "$.id",
                    "string",
                    new RelationalQueryFieldTarget.DocumentUuid()
                )
            ),
            [CreateQueryElement("ID", "$.id", "not-a-guid", "string")],
            totalCount: true
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_allows_no_further_authorization_required_queries_to_continue_through_preprocessing()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(
                _schoolResourceInfo,
                CreateSupportedQueryField(
                    "id",
                    "$.id",
                    "string",
                    new RelationalQueryFieldTarget.DocumentUuid()
                )
            ),
            [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
            ]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_not_implemented_when_query_authorization_requires_filtering()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeEquivalentTo(
                new QueryResult.QueryFailureNotImplemented(
                    "Relational query authorization is not implemented for resource 'Ed-Fi.School' when effective GET-many authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or only 'NoFurtherAuthorizationRequired' are currently supported."
                )
            );
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_compiled_omission_reason_for_omitted_query_resources()
    {
        var omissionReason =
            "storage kind 'SharedDescriptorTable' uses the descriptor query path instead of relational GET-many support.";
        var queryRequest = CreateQueryRequest(
            CreateOmittedQueryCapabilityMappingSet(
                _schoolResourceInfo,
                new RelationalQueryCapabilityOmission(
                    RelationalQueryCapabilityOmissionKind.DescriptorResource,
                    omissionReason
                )
            ),
            [],
            totalCount: false
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeEquivalentTo(
                new QueryResult.QueryFailureNotImplemented(
                    "Relational query capability for resource 'Ed-Fi.School' was intentionally omitted: "
                        + omissionReason
                )
            );
    }

    [Test]
    public async Task It_returns_the_missing_query_capability_guard_rail_for_query_requests()
    {
        const string expectedFailureMessage =
            "Relational query capability lookup failed for resource 'Ed-Fi.School' in mapping set "
            + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have compiled relational GET-many capability metadata, "
            + "including intentional omission state when applicable, but no entry was found. This indicates an internal compilation/selection bug.";
        var queryRequest = CreateQueryRequest(
            CreateSupportedMappingSet(_schoolResourceInfo),
            [],
            totalCount: false
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.UnknownFailure(expectedFailureMessage));
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_short_circuits_invalid_id_queries_to_an_empty_page_without_hydration()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(
                _schoolResourceInfo,
                CreateSupportedQueryField(
                    "id",
                    "$.id",
                    "string",
                    new RelationalQueryFieldTarget.DocumentUuid()
                )
            ),
            [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
            totalCount: true
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_short_circuits_unresolved_descriptor_queries_to_an_empty_page_without_hydration()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(
                _schoolResourceInfo,
                CreateSupportedQueryField(
                    "schoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "string",
                    new RelationalQueryFieldTarget.DescriptorIdColumn(
                        new DbColumnName("SchoolCategoryDescriptorId"),
                        new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor")
                    )
                )
            ),
            [
                CreateQueryElement(
                    "schoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "uri://missing",
                    "string"
                ),
            ],
            totalCount: true
        );
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (ReferenceResolverRequest request, CancellationToken _) =>
                    Task.FromResult(
                        CreateResolvedReferenceSet(
                            invalidDescriptorReferences:
                            [
                                .. request.DescriptorReferences.Select(reference =>
                                    DescriptorReferenceFailure.From(
                                        reference,
                                        DescriptorReferenceFailureReason.Missing
                                    )
                                ),
                            ]
                        )
                    )
            );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_routes_post_requests_through_the_executor_with_reference_resolution_inputs()
    {
        var committedEtag = CreateCommittedReadbackEtag("Lincoln High");
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var requestBody = CreateRequestBody();
        requestBody["_etag"] = "\"stale-request-etag\"";
        var traceId = new TraceId("post-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var documentInfo = CreateDocumentInfo([documentReference], [descriptorReference]);

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(traceId);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, committedEtag));
        ((UpsertResult.InsertSuccess)result).ETag.Should().NotBe(requestBody["_etag"]!.GetValue<string>());
        ((UpsertResult.InsertSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        _capturedExecutorRequest.MappingSet.Should().BeSameAs(mappingSet);
        _capturedExecutorRequest.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        _capturedExecutorRequest
            .TargetRequest.Should()
            .BeEquivalentTo(new RelationalWriteTargetRequest.Post(documentInfo.ReferentialId, documentUuid));
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.CreateNew(documentUuid));
        _capturedExecutorRequest
            .WritePlan.Model.Resource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "School"));
        _capturedExecutorRequest
            .ExistingDocumentReadPlan.Should()
            .BeSameAs(mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")]);
        _capturedExecutorRequest.SelectedBody.Should().BeSameAs(requestBody);
        _capturedExecutorRequest.ReferenceResolutionRequest.MappingSet.Should().BeSameAs(mappingSet);
        _capturedExecutorRequest
            .ReferenceResolutionRequest.RequestResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "School"));
        _capturedExecutorRequest
            .ReferenceResolutionRequest.DocumentReferences.Should()
            .ContainSingle()
            .Which.Should()
            .Be(documentReference);
        _capturedExecutorRequest
            .ReferenceResolutionRequest.DescriptorReferences.Should()
            .ContainSingle()
            .Which.Should()
            .Be(descriptorReference);
        _capturedExecutorRequest.TraceId.Should().Be(traceId);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_routes_post_as_update_requests_through_the_executor_with_a_read_plan()
    {
        var committedEtag = CreateCommittedReadbackEtag("Post As Update High");
        var traceId = new TraceId("post-update-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Post As Update High");
        requestBody["_etag"] = "\"stale-request-etag\"";
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var documentInfo = CreateDocumentInfo();
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        _targetLookupService.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpdateSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(traceId);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, committedEtag));
        ((UpsertResult.UpdateSuccess)result).ETag.Should().NotBe(requestBody["_etag"]!.GetValue<string>());
        ((UpsertResult.UpdateSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        _capturedExecutorRequest.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        _capturedExecutorRequest
            .TargetRequest.Should()
            .BeEquivalentTo(new RelationalWriteTargetRequest.Post(documentInfo.ReferentialId, documentUuid));
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
            );
        _capturedExecutorRequest.ExistingDocumentReadPlan.Should().BeSameAs(expectedReadPlan);
        _capturedExecutorRequest.SelectedBody.Should().BeSameAs(requestBody);
        _capturedExecutorRequest.TraceId.Should().Be(traceId);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_routes_put_requests_through_the_executor_with_reference_resolution_inputs()
    {
        var committedEtag = CreateCommittedReadbackEtag("Roosevelt High");
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var traceId = new TraceId("put-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Roosevelt High");
        requestBody["_etag"] = "\"stale-request-etag\"";
        var documentInfo = CreateDocumentInfo([documentReference], [descriptorReference]);

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => updateRequest.TraceId).Returns(traceId);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, committedEtag));
        ((UpdateResult.UpdateSuccess)result).ETag.Should().NotBe(requestBody["_etag"]!.GetValue<string>());
        ((UpdateResult.UpdateSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        _capturedExecutorRequest.MappingSet.Should().BeSameAs(mappingSet);
        _capturedExecutorRequest.OperationKind.Should().Be(RelationalWriteOperationKind.Put);
        _capturedExecutorRequest
            .TargetRequest.Should()
            .BeEquivalentTo(new RelationalWriteTargetRequest.Put(documentUuid));
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L));
        _capturedExecutorRequest.ExistingDocumentReadPlan.Should().BeSameAs(expectedReadPlan);
        _capturedExecutorRequest.SelectedBody.Should().BeSameAs(requestBody);
        _capturedExecutorRequest
            .ReferenceResolutionRequest.DocumentReferences.Should()
            .ContainSingle()
            .Which.Should()
            .Be(documentReference);
        _capturedExecutorRequest
            .ReferenceResolutionRequest.DescriptorReferences.Should()
            .ContainSingle()
            .Which.Should()
            .Be(descriptorReference);
        _capturedExecutorRequest.TraceId.Should().Be(traceId);
        _targetLookupService.ResolveForPutCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_short_circuits_missing_put_targets_before_executor_entry()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        _targetLookupService.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());
        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        _capturedExecutorRequests.Should().BeEmpty();
        _targetLookupService.ResolveForPutCallCount.Should().Be(1);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_retries_stale_put_guarded_no_op_attempts_once_against_fresh_target_state()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var executorCallCount = 0;
        _targetLookupService.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L)
        );
        _targetLookupService.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 45L)
        );
        _targetLookupService.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 45L)
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteExecutorResult>(
                    executorCallCount++ switch
                    {
                        0 => new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureWriteConflict(),
                            RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                        ),
                        1 => new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateSuccess(documentUuid, "\"45\""),
                            RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                        ),
                        _ => throw new InvalidOperationException("Unexpected extra executor attempt."),
                    }
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Fresh retry"));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests
            .Select(request => request.ExistingDocumentReadPlan)
            .Should()
            .OnlyContain(readPlan => ReferenceEquals(readPlan, expectedReadPlan));
        _capturedExecutorRequests
            .Select(request => request.TargetRequest)
            .Should()
            .OnlyContain(targetRequest =>
                targetRequest.Equals(new RelationalWriteTargetRequest.Put(documentUuid))
            );
        _capturedExecutorRequests
            .Select(request => request.TargetContext)
            .Should()
            .BeEquivalentTo([
                new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L),
                new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 45L),
            ]);
        _targetLookupService.ResolveForPutCallCount.Should().Be(2);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_does_not_retry_put_guarded_no_ops_when_the_executor_finishes_after_refreshing_session_loaded_freshness()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        _targetLookupService.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L)
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateSuccess(documentUuid, "\"45\""),
                        RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                    )
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Loaded freshness wins"));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequests[0]
            .TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L));
        _targetLookupService.ResolveForPutCallCount.Should().Be(1);
        _targetLookupService.PutResults.Count.Should().Be(0);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_retries_stale_post_as_update_guarded_no_op_attempts_once_against_fresh_target_state()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var executorCallCount = 0;
        var documentInfo = CreateDocumentInfo();
        _targetLookupService.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L)
        );
        _targetLookupService.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 45L)
        );
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteExecutorResult>(
                    executorCallCount++ switch
                    {
                        0 => new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict(),
                            RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                        ),
                        1 => new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpdateSuccess(documentUuid, "\"45\""),
                            RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                        ),
                        _ => throw new InvalidOperationException("Unexpected extra executor attempt."),
                    }
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post retry"));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests
            .Select(request => request.ExistingDocumentReadPlan)
            .Should()
            .OnlyContain(readPlan => ReferenceEquals(readPlan, expectedReadPlan));
        _capturedExecutorRequests
            .Select(request => request.TargetRequest)
            .Should()
            .OnlyContain(targetRequest =>
                targetRequest.Equals(
                    new RelationalWriteTargetRequest.Post(documentInfo.ReferentialId, documentUuid)
                )
            );
        _capturedExecutorRequests
            .Select(request => request.TargetContext)
            .Should()
            .BeEquivalentTo([
                new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L),
                new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 45L),
            ]);
        _targetLookupService.ResolveForPostCallCount.Should().Be(2);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_returns_write_conflict_when_the_single_stale_no_op_retry_is_also_stale()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var executorCallCount = 0;
        _targetLookupService.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L)
        );
        _targetLookupService.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 45L)
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteExecutorResult>(
                    executorCallCount++ switch
                    {
                        0 => new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureWriteConflict(),
                            RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                        ),
                        1 => new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureWriteConflict(),
                            RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                        ),
                        _ => throw new InvalidOperationException("Unexpected extra executor attempt."),
                    }
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Retry conflict"));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests
            .Select(request => request.TargetRequest)
            .Should()
            .OnlyContain(targetRequest =>
                targetRequest.Equals(new RelationalWriteTargetRequest.Put(documentUuid))
            );
        _targetLookupService.ResolveForPutCallCount.Should().Be(2);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_returns_executor_owned_post_reference_failures_without_remapping()
    {
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var invalidDocumentReference = DocumentReferenceFailure.From(
            documentReference,
            DocumentReferenceFailureReason.Missing
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureReference([invalidDocumentReference], [])
                    )
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo([documentReference]));
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result
            .Should()
            .BeEquivalentTo(new UpsertResult.UpsertFailureReference([invalidDocumentReference], []));
    }

    [Test]
    public async Task It_returns_executor_owned_put_reference_failures_without_remapping()
    {
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var invalidDescriptorReference = DescriptorReferenceFailure.From(
            descriptorReference,
            DescriptorReferenceFailureReason.DescriptorTypeMismatch
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureReference([], [invalidDescriptorReference])
                    )
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo)
            .Returns(CreateDocumentInfo(descriptorReferences: [descriptorReference]));
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result
            .Should()
            .BeEquivalentTo(new UpdateResult.UpdateFailureReference([], [invalidDescriptorReference]));
    }

    [Test]
    public async Task It_preserves_post_validation_results_returned_by_the_executor()
    {
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.schoolYear"),
            "Column 'SchoolYear' expected an integer."
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureValidation([validationFailure])
                    )
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpsertFailureValidation([validationFailure]));
    }

    [Test]
    public async Task It_preserves_put_validation_results_returned_by_the_executor()
    {
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.addresses[1]"),
            "Duplicate submitted semantic identity values are not allowed."
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureValidation([validationFailure])
                    )
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateFailureValidation([validationFailure]));
    }

    [Test]
    public async Task It_routes_descriptor_post_requests_to_the_descriptor_write_handler()
    {
        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UnknownFailure(
                    "Descriptor POST write is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor'."
                )
            );
        _capturedExecutorRequests.Should().BeEmpty();
        _targetLookupService.ResolveForPostCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_routes_descriptor_put_requests_to_the_descriptor_write_handler()
    {
        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UnknownFailure(
                    "Descriptor PUT write is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor'."
                )
            );
        _capturedExecutorRequests.Should().BeEmpty();
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_preserves_descriptor_post_etags_returned_by_the_handler_without_a_follow_up_lookup()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            descriptorHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            A.Fake<IRelationalCommandExecutor>(),
            new NoOpRelationalWriteExceptionClassifier()
        );

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateDescriptorRequestBody();
        var descriptorResponseEtag = CreateDescriptorResponseEtag(requestBody);
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(new UpsertResult.InsertSuccess(documentUuid, descriptorResponseEtag));

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, descriptorResponseEtag));
        ((UpsertResult.InsertSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        _targetLookupService.ResolveForPostCallCount.Should().Be(0);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_preserves_descriptor_put_etags_returned_by_the_handler_without_a_follow_up_lookup()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            descriptorHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            A.Fake<IRelationalCommandExecutor>(),
            new NoOpRelationalWriteExceptionClassifier()
        );

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateDescriptorRequestBody("Updated Charter");
        var descriptorResponseEtag = CreateDescriptorResponseEtag(requestBody);
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(new UpdateResult.UpdateSuccess(documentUuid, descriptorResponseEtag));

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, descriptorResponseEtag));
        ((UpdateResult.UpdateSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        _targetLookupService.ResolveForPostCallCount.Should().Be(0);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_routes_descriptor_delete_requests_to_the_descriptor_write_handler()
    {
        var deleteRequest = A.Fake<IRelationalDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("test-trace"));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.UnknownFailure("Descriptor DELETE write is not implemented."));
        _capturedExecutorRequests.Should().BeEmpty();
    }

    [Test]
    public async Task It_forwards_document_uuid_and_trace_id_to_the_descriptor_delete_handler()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        var expectedDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var expectedTraceId = new TraceId("descriptor-delete-forwarding");
        DocumentUuid capturedUuid = default;
        TraceId capturedTraceId = default!;

        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DocumentUuid>._, A<TraceId>._, A<CancellationToken>._)
            )
            .Invokes(call =>
            {
                capturedUuid = call.GetArgument<DocumentUuid>(0);
                capturedTraceId = call.GetArgument<TraceId>(1)!;
            })
            .Returns(Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess()));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            descriptorHandler,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _commandExecutor,
            _writeExceptionClassifier
        );

        var deleteRequest = A.Fake<IRelationalDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(expectedDocumentUuid);
        A.CallTo(() => deleteRequest.TraceId).Returns(expectedTraceId);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        capturedUuid.Should().Be(expectedDocumentUuid);
        capturedTraceId.Value.Should().Be(expectedTraceId.Value);
        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DocumentUuid>._, A<TraceId>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_throws_when_the_delete_request_does_not_implement_IRelationalDeleteRequest()
    {
        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_schoolResourceInfo);

        Func<Task> act = async () => _ = await _sut.DeleteDocumentById(deleteRequest);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task It_throws_when_a_non_descriptor_delete_request_has_no_mapping_set()
    {
        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet: null);

        Func<Task> act = async () => _ = await _sut.DeleteDocumentById(deleteRequest);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task It_does_not_route_non_descriptor_delete_requests_through_the_descriptor_write_handler()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            descriptorHandler,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _commandExecutor,
            _writeExceptionClassifier
        );
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DocumentUuid>._, A<TraceId>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_not_exists_when_the_document_uuid_is_not_resolvable(
        SqlDialect dialect
    )
    {
        // Default fake returns null from the UUID lookup, which the repository treats
        // as "not found" without executing the second roundtrip.
        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_success_when_both_roundtrips_complete_successfully(SqlDialect dialect)
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_not_exists_when_the_delete_returns_no_rows(SqlDialect dialect)
    {
        // Simulates a concurrent-delete race: the document was present at roundtrip 1
        // but gone by roundtrip 2. RETURNING/OUTPUT yields no rows.
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: false);

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_reference_when_the_classifier_reports_a_foreign_key_violation(
        SqlDialect dialect
    )
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("constraint violation"));
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation("FK_Document_xxx");

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference(["(referenced document)"]));
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_write_conflict_when_the_classifier_reports_a_transient_failure(
        SqlDialect dialect
    )
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("deadlock"));
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_unknown_failure_on_generic_database_error(SqlDialect dialect)
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("boom"));

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeEquivalentTo(
                new DeleteResult.UnknownFailure(
                    "An unexpected error occurred while processing the delete request."
                )
            );
    }

    [Test]
    public async Task It_returns_the_missing_write_plan_guard_rail_for_non_descriptor_post_requests()
    {
        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateMissingWritePlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UnknownFailure(
                    "Write plan lookup failed for resource 'Ed-Fi.School' in mapping set "
                        + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table write plan, "
                        + "but no entry was found. This indicates an internal compilation/selection bug."
                )
            );
    }

    [Test]
    public async Task It_returns_the_missing_read_plan_guard_rail_for_existing_document_put_requests()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        const string expectedFailureMessage =
            "Read plan lookup failed for resource 'Ed-Fi.School' in mapping set "
            + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table read plan, "
            + "but no entry was found. This indicates an internal compilation/selection bug.";

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UnknownFailure(expectedFailureMessage));
        _capturedExecutorRequests.Should().BeEmpty();
        _targetLookupService.ResolveForPutCallCount.Should().Be(1);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_missing_read_plan_guard_rail_for_existing_document_post_as_update_requests()
    {
        var candidateDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var documentInfo = CreateDocumentInfo();
        const string expectedFailureMessage =
            "Read plan lookup failed for resource 'Ed-Fi.School' in mapping set "
            + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table read plan, "
            + "but no entry was found. This indicates an internal compilation/selection bug.";

        _targetLookupService.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(candidateDocumentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UnknownFailure(expectedFailureMessage));
        _capturedExecutorRequests.Should().BeEmpty();
        _targetLookupService.ResolveForPostCallCount.Should().Be(1);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_missing_read_plan_guard_rail_for_create_new_post_requests()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var documentInfo = CreateDocumentInfo();
        const string expectedFailureMessage =
            "Read plan lookup failed for resource 'Ed-Fi.School' in mapping set "
            + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table read plan, "
            + "but no entry was found. This indicates an internal compilation/selection bug.";

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Create without read plan"));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UnknownFailure(expectedFailureMessage));
        _capturedExecutorRequests.Should().BeEmpty();
        _targetLookupService.ResolveForPostCallCount.Should().Be(1);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_does_not_remap_internal_executor_invalid_operation_failures()
    {
        var internalFailure = new InvalidOperationException(
            "Resolved lookup set did not contain a matching 'Ed-Fi.School' entry at '$.schoolReference'."
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Throws(internalFailure);

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        Func<Task> act = async () => _ = await _sut.UpsertDocument(upsertRequest);

        var thrownException = await act.Should().ThrowAsync<InvalidOperationException>();
        thrownException.Which.Message.Should().Be(internalFailure.Message);
    }

    [Test]
    public void It_does_not_remap_missing_mapping_sets_inside_the_repository()
    {
        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(null);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        Func<Task> act = async () => _ = await _sut.UpsertDocument(upsertRequest);

        act.Should().ThrowAsync<ArgumentNullException>().Result.Which.ParamName.Should().Be("mappingSet");
    }

    private sealed class RecordingRelationalWriteTargetLookupService : IRelationalWriteTargetLookupService
    {
        public Queue<RelationalWriteTargetLookupResult> PostResults { get; } = [];

        public Queue<RelationalWriteTargetLookupResult> PutResults { get; } = [];

        public int ResolveForPostCallCount { get; private set; }

        public int ResolveForPutCallCount { get; private set; }

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveForPostCallCount++;

            return Task.FromResult(
                PostResults.Count > 0
                    ? PostResults.Dequeue()
                    : new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
            );
        }

        public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            DocumentUuid documentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveForPutCallCount++;

            return Task.FromResult(
                PutResults.Count > 0
                    ? PutResults.Dequeue()
                    : new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L)
            );
        }
    }

    private sealed class RecordingResourceAuthorizationHandler : IResourceAuthorizationHandler
    {
        public int CallCount { get; private set; }

        public Task<ResourceAuthorizationResult> Authorize(
            DocumentSecurityElements documentSecurityElements,
            OperationType operationType,
            TraceId traceId
        )
        {
            CallCount++;

            return Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
        }
    }

    private static IRelationalDeleteRequest CreateNonDescriptorDeleteRequest(MappingSet? mappingSet)
    {
        var deleteRequest = A.Fake<IRelationalDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("delete-trace"));
        A.CallTo(() => deleteRequest.MappingSet).Returns(mappingSet);
        return deleteRequest;
    }

    private void ConfigureResolvedDocument(long documentId, DocumentUuid documentUuid)
    {
        A.CallTo(_commandExecutor)
            .WithReturnType<Task<RelationalDocumentUuidLookupSupport.ResolvedDocumentByUuid?>>()
            .Returns(
                Task.FromResult<RelationalDocumentUuidLookupSupport.ResolvedDocumentByUuid?>(
                    new RelationalDocumentUuidLookupSupport.ResolvedDocumentByUuid(
                        DocumentId: documentId,
                        DocumentUuid: documentUuid,
                        ResourceKeyId: 1,
                        ContentVersion: 42L
                    )
                )
            );
    }

    private void ConfigureDeleteOutcome(bool deleted)
    {
        A.CallTo(_commandExecutor).WithReturnType<Task<bool>>().Returns(Task.FromResult(deleted));
    }

    private void ConfigureDeleteThrows(DbException exception)
    {
        A.CallTo(_commandExecutor).WithReturnType<Task<bool>>().Throws(exception);
    }

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class ConfigurableRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

        public bool IsTransientFailureToReturn { get; set; }

        public int TryClassifyCallCount { get; private set; }

        public int IsTransientFailureCallCount { get; private set; }

        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            TryClassifyCallCount++;
            classification = ClassificationToReturn;
            return classification is not null;
        }

        public bool IsTransientFailure(DbException exception)
        {
            IsTransientFailureCallCount++;
            return IsTransientFailureToReturn;
        }
    }

    private static IRelationalGetRequest CreateGetRequest(
        DocumentUuid documentUuid,
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        IResourceAuthorizationHandler resourceAuthorizationHandler,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        var getRequest = A.Fake<IRelationalGetRequest>();
        A.CallTo(() => getRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => getRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => getRequest.ResourceInfo)
            .Returns(
                new BaseResourceInfo(
                    resourceInfo.ProjectName,
                    resourceInfo.ResourceName,
                    resourceInfo.IsDescriptor
                )
            );
        A.CallTo(() => getRequest.ResourceAuthorizationHandler).Returns(resourceAuthorizationHandler);
        A.CallTo(() => getRequest.TraceId).Returns(new TraceId("get-trace"));
        A.CallTo(() => getRequest.ReadMode).Returns(readMode);
        A.CallTo(() => getRequest.ReadableProfileProjectionContext).Returns(readableProfileProjectionContext);

        return getRequest;
    }

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        DocumentUuid documentUuid,
        long documentId,
        long contentVersion
    )
    {
        return new DocumentMetadataRow(
            documentId,
            documentUuid.Value,
            contentVersion,
            contentVersion,
            new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.FromHours(-5))
        );
    }

    private static HydratedPage CreateHydratedPage(
        ResourceReadPlan readPlan,
        DocumentMetadataRow documentMetadata,
        params (long DocumentId, string Name)[] rows
    )
    {
        return new HydratedPage(
            null,
            [documentMetadata],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    rows.Select(row => new object?[] { row.DocumentId, row.Name }).ToArray()
                ),
            ],
            []
        );
    }

    private static ResourceInfo CreateResourceInfo(string resourceName, bool isDescriptor = false)
    {
        return new ResourceInfo(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName(resourceName),
            IsDescriptor: isDescriptor,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(
                false,
                default,
                default
            ),
            AuthorizationSecurableInfo: []
        );
    }

    private static DocumentInfo CreateDocumentInfo(
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null
    )
    {
        return new DocumentInfo(
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
            ]),
            ReferentialId: new ReferentialId(Guid.NewGuid()),
            DocumentReferences: documentReferences ?? [],
            DocumentReferenceArrays: [],
            DescriptorReferences: descriptorReferences ?? [],
            SuperclassIdentity: null
        );
    }

    private static JsonNode CreateRequestBody(string name = "Lincoln High")
    {
        return JsonNode.Parse($$"""{"name":"{{name}}"}""")!;
    }

    private static JsonNode CreateDescriptorRequestBody(string description = "Charter")
    {
        return JsonNode.Parse(
            $$"""
            {
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Charter",
              "shortDescription": "Charter",
              "description": "{{description}}",
              "effectiveBeginDate": "2024-01-01"
            }
            """
        )!;
    }

    private static string CreateCommittedReadbackEtag(string name, int schoolId = 255901)
    {
        return RelationalApiMetadataFormatter.FormatEtag(
            JsonNode.Parse($$"""{"schoolId":{{schoolId}},"name":"{{name}}"}""")!
        );
    }

    private static string CreateDescriptorResponseEtag(JsonNode requestBody)
    {
        return RelationalApiMetadataFormatter.FormatEtag(
            DescriptorWriteBodyExtractor.Extract(
                requestBody,
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
            )
        );
    }

    private static DocumentReference CreateDocumentReference(BaseResourceInfo targetResource, string path)
    {
        return new DocumentReference(
            ResourceInfo: targetResource,
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.localEducationAgencyId"), "255901"),
            ]),
            ReferentialId: new ReferentialId(Guid.NewGuid()),
            Path: new JsonPath(path)
        );
    }

    private static DescriptorReference CreateDescriptorReference(BaseResourceInfo targetResource, string path)
    {
        return new DescriptorReference(
            ResourceInfo: targetResource,
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.namespace"), "uri://sample"),
                new DocumentIdentityElement(new JsonPath("$.codeValue"), "SchoolCategory#Charter"),
            ]),
            ReferentialId: new ReferentialId(Guid.NewGuid()),
            Path: new JsonPath(path)
        );
    }

    private static MappingSet CreateSupportedMappingSet(
        ResourceInfo resourceInfo,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootPlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootPlan.TableModel,
            ResourceStorageKind.RelationalTables
        );
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var readPlan = CreateReadPlan(resourceModel, rootPlan.TableModel);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: CreateDerivedModelSet(resourceModel, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resourceKey.Resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [resourceKey.Resource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static MappingSet CreateProfileProjectionOrderSensitiveMappingSet(ResourceInfo resourceInfo)
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
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
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("NameOfInstitution"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
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
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );
        var readPlan = CreateReadPlan(resourceModel, rootTable);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(resourceModel, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [resourceKey.Resource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static MappingSet CreateQuerySupportedMappingSet(
        ResourceInfo resourceInfo,
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        return CreateQuerySupportedMappingSet(
            CreateSupportedMappingSet(resourceInfo),
            resourceInfo,
            supportedFields
        );
    }

    private static MappingSet CreateQuerySupportedMappingSet(
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );

        return mappingSet with
        {
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    supportedFields.ToDictionary(
                        static supportedField => supportedField.QueryFieldName,
                        static supportedField => supportedField,
                        StringComparer.OrdinalIgnoreCase
                    ),
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
    }

    private static MappingSet CreateOmittedQueryCapabilityMappingSet(
        ResourceInfo resourceInfo,
        RelationalQueryCapabilityOmission omission
    )
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );

        return CreateSupportedMappingSet(resourceInfo) with
        {
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Omitted(omission),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
    }

    private static MappingSet CreateDescriptorOnlyMappingSet(ResourceInfo resourceInfo)
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = CreateRootPlan().TableModel;
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.SharedDescriptorTable
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(resourceModel, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static MappingSet CreateMissingWritePlanMappingSet(ResourceInfo resourceInfo)
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = CreateRootPlan().TableModel;
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(resourceModel, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static MappingSet CreateMissingReadPlanMappingSet(ResourceInfo resourceInfo)
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootPlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootPlan.TableModel,
            ResourceStorageKind.RelationalTables
        );
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(resourceModel, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resourceKey.Resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static IRelationalQueryRequest CreateQueryRequest(
        MappingSet mappingSet,
        QueryElement[] queryElements,
        bool totalCount,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        authorizationStrategyEvaluators ??= [];

        var queryRequest = A.Fake<IRelationalQueryRequest>();
        A.CallTo(() => queryRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => queryRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => queryRequest.QueryElements).Returns(queryElements);
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo)
            .Returns(Array.Empty<AuthorizationSecurableInfo>());
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns(authorizationStrategyEvaluators);
        A.CallTo(() => queryRequest.PaginationParameters)
            .Returns(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: totalCount, MaximumPageSize: 500)
            );
        A.CallTo(() => queryRequest.TraceId).Returns(new TraceId("query-trace"));
        A.CallTo(() => queryRequest.ReadableProfileProjectionContext)
            .Returns(readableProfileProjectionContext);
        return queryRequest;
    }

    private static AuthorizationStrategyEvaluator CreateAuthorizationStrategyEvaluator(
        string authorizationStrategyName
    )
    {
        return new AuthorizationStrategyEvaluator(authorizationStrategyName, [], FilterOperator.And);
    }

    private static QueryElement CreateQueryElement(
        string queryFieldName,
        string documentPath,
        string value,
        string type
    )
    {
        return new QueryElement(queryFieldName, [new JsonPath(documentPath)], value, type);
    }

    private static SupportedRelationalQueryField CreateSupportedQueryField(
        string queryFieldName,
        string path,
        string type,
        RelationalQueryFieldTarget target
    )
    {
        return new SupportedRelationalQueryField(
            queryFieldName,
            new RelationalQueryFieldPath(new JsonPathExpression(path, []), type),
            target
        );
    }

    private static ResolvedReferenceSet CreateResolvedReferenceSet(
        IReadOnlyList<ResolvedDescriptorReference>? successfulDescriptorReferences = null,
        IReadOnlyList<DescriptorReferenceFailure>? invalidDescriptorReferences = null
    )
    {
        successfulDescriptorReferences ??= [];
        invalidDescriptorReferences ??= [];

        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: successfulDescriptorReferences.ToDictionary(
                static reference => reference.Reference.Path,
                static reference => reference
            ),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: invalidDescriptorReferences,
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
    }

    private static ResourceKeyEntry CreateResourceKeyEntry(ResourceInfo resourceInfo)
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );

        return new ResourceKeyEntry(
            1,
            resource,
            resourceInfo.ResourceVersion.Value,
            resourceInfo.IsDescriptor
        );
    }

    private static DerivedRelationalModelSet CreateDerivedModelSet(
        RelationalResourceModel resourceModel,
        ResourceKeyEntry resourceKey
    )
    {
        return new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: "schema-hash",
                ResourceKeyCount: 1,
                ResourceKeySeedHash: [1, 2, 3],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                ],
                ResourceKeysInIdOrder: [resourceKey]
            ),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
            ],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(resourceKey, resourceModel.StorageKind, resourceModel),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );
    }

    private static RelationalResourceModel CreateRelationalResourceModel(
        ResourceKeyEntry resourceKey,
        DbTableModel rootTable,
        ResourceStorageKind storageKind
    )
    {
        return new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: storageKind,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
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
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", []),
                    null,
                    new ColumnStorage.Stored()
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

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static ResourceReadPlan CreateReadPlan(
        RelationalResourceModel resourceModel,
        DbTableModel rootTable
    )
    {
        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(rootTable, "select 1")],
            [],
            []
        );
    }
}
