// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
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
    private IDocumentHydrator _documentHydrator = null!;
    private IRelationalReadTargetLookupService _readTargetLookupService = null!;
    private IRelationalReadMaterializer _readMaterializer = null!;
    private IReadableProfileProjector _readableProfileProjector = null!;
    private RelationalWriteExecutorRequest _capturedExecutorRequest = null!;
    private List<RelationalWriteExecutorRequest> _capturedExecutorRequests = null!;

    [SetUp]
    public void Setup()
    {
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _targetLookupService = new RecordingRelationalWriteTargetLookupService();
        _documentHydrator = A.Fake<IDocumentHydrator>();
        _readTargetLookupService = A.Fake<IRelationalReadTargetLookupService>();
        _readMaterializer = A.Fake<IRelationalReadMaterializer>();
        _readableProfileProjector = A.Fake<IReadableProfileProjector>();
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
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector
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
    public async Task It_applies_readable_profile_projection_after_external_materialization()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var expectedProjectedEtag = RelationalApiMetadataFormatter.FormatEtag(
            JsonNode.Parse("""{"schoolId":255901,"nameOfInstitution":"Lincoln High"}""")!
        );
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
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
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            new RecordingResourceAuthorizationHandler(),
            readableProfileProjectionContext: projectionContext
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 93L),
            (345L, "Lincoln High")
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
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be(expectedProjectedEtag);
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().NotBe("\"93\"");
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
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.ResourceInfo).Returns(_schoolResourceInfo);

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeEquivalentTo(
                new QueryResult.QueryFailureNotImplemented(
                    "Relational query handling is not implemented for resource 'Ed-Fi.School'."
                )
            );
    }

    [Test]
    public async Task It_routes_post_requests_through_the_executor_with_reference_resolution_inputs()
    {
        const string committedEtag = "\"91\"";
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var requestBody = CreateRequestBody();
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
        const string committedEtag = "\"92\"";
        var traceId = new TraceId("post-update-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Post As Update High");
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
        const string committedEtag = "\"93\"";
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
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector
        );

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(new UpsertResult.InsertSuccess(documentUuid, "\"71\""));

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, "\"71\""));
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
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector
        );

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(new UpdateResult.UpdateSuccess(documentUuid, "\"72\""));

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, "\"72\""));
        _targetLookupService.ResolveForPostCallCount.Should().Be(0);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_routes_descriptor_delete_requests_to_the_descriptor_write_handler()
    {
        var deleteRequest = A.Fake<IDeleteRequest>();
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
    public async Task It_allows_create_new_post_requests_to_bypass_missing_read_plan_guard_rails()
    {
        const string committedEtag = "\"94\"";
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Create without read plan");
        var documentInfo = CreateDocumentInfo();

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

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, committedEtag));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.CreateNew(documentUuid));
        _capturedExecutorRequest.ExistingDocumentReadPlan.Should().BeNull();
        _targetLookupService.ResolveForPostCallCount.Should().Be(1);
        _targetLookupService.ResolveForPutCallCount.Should().Be(0);
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

    private static MappingSet CreateSupportedMappingSet(ResourceInfo resourceInfo)
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
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
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
