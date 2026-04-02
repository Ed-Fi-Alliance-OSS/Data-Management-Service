// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
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
    private IRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
    private IRelationalWriteExecutor _writeExecutor = null!;
    private RelationalWriteExecutorRequest _capturedExecutorRequest = null!;

    [SetUp]
    public void Setup()
    {
        _targetLookupResolver = A.Fake<IRelationalWriteTargetLookupResolver>();
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();

        A.CallTo(() =>
                _targetLookupResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
                Task.FromResult<RelationalWriteTargetLookupResult>(
                    new RelationalWriteTargetLookupResult.CreateNew(call.GetArgument<DocumentUuid>(3))
                )
            );
        A.CallTo(() =>
                _targetLookupResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
                Task.FromResult<RelationalWriteTargetLookupResult>(
                    new RelationalWriteTargetLookupResult.ExistingDocument(
                        1L,
                        call.GetArgument<DocumentUuid>(2),
                        0L
                    )
                )
            );
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!)
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UnknownFailure("Unexpected write-executor test fallback.")
                    )
                )
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _targetLookupResolver,
            _writeExecutor
        );
    }

    [Test]
    public async Task It_returns_a_precise_unknown_failure_for_get_requests()
    {
        var getRequest = A.Fake<IGetRequest>();
        A.CallTo(() => getRequest.ResourceName).Returns(_schoolResourceInfo.ResourceName);

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeEquivalentTo(
                new GetResult.UnknownFailure("Relational GET by id is not implemented for resource 'School'.")
            );
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
    public async Task It_returns_a_precise_unknown_failure_for_query_requests()
    {
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.ResourceInfo).Returns(_schoolResourceInfo);

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeEquivalentTo(
                new QueryResult.UnknownFailure(
                    "Relational query handling is not implemented for resource 'Ed-Fi.School'."
                )
            );
    }

    [Test]
    public async Task It_routes_post_requests_through_the_executor_with_reference_resolution_inputs()
    {
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

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!)
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(new UpsertResult.InsertSuccess(documentUuid))
                )
            );

        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo)
            .Returns(CreateDocumentInfo([documentReference], [descriptorReference]));
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(traceId);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid));
        _capturedExecutorRequest.MappingSet.Should().BeSameAs(mappingSet);
        _capturedExecutorRequest.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.CreateNew>()
            .Which.DocumentUuid.Should()
            .Be(documentUuid);
        _capturedExecutorRequest
            .WritePlan.Model.Resource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "School"));
        _capturedExecutorRequest.ReadPlan.Should().BeNull();
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
        _capturedExecutorRequest.DiagnosticIdentifier.Should().BeNull();
    }

    [Test]
    public async Task It_routes_post_as_update_requests_through_the_executor_with_a_read_plan()
    {
        var traceId = new TraceId("post-update-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var existingDocumentId = 456L;
        var requestBody = CreateRequestBody("Post As Update High");
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        const long observedContentVersion = 42L;

        A.CallTo(() =>
                _targetLookupResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetLookupResult>(
                    new RelationalWriteTargetLookupResult.ExistingDocument(
                        existingDocumentId,
                        documentUuid,
                        observedContentVersion
                    )
                )
            );
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!)
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpdateSuccess(documentUuid))
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(traceId);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid));
        _capturedExecutorRequest.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    existingDocumentId,
                    documentUuid,
                    observedContentVersion
                )
            );
        _capturedExecutorRequest.ReadPlan.Should().BeSameAs(expectedReadPlan);
        _capturedExecutorRequest.SelectedBody.Should().BeSameAs(requestBody);
        _capturedExecutorRequest.TraceId.Should().Be(traceId);
    }

    [Test]
    public async Task It_routes_put_requests_through_the_executor_with_reference_resolution_inputs()
    {
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
        var existingDocumentId = 123L;
        const long observedContentVersion = 84L;
        var requestBody = CreateRequestBody("Roosevelt High");

        A.CallTo(() =>
                _targetLookupResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetLookupResult>(
                    new RelationalWriteTargetLookupResult.ExistingDocument(
                        existingDocumentId,
                        documentUuid,
                        observedContentVersion
                    )
                )
            );
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorRequest>(0)!)
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateSuccess(documentUuid))
                )
            );

        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo)
            .Returns(CreateDocumentInfo([documentReference], [descriptorReference]));
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => updateRequest.TraceId).Returns(traceId);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid));
        _capturedExecutorRequest.MappingSet.Should().BeSameAs(mappingSet);
        _capturedExecutorRequest.OperationKind.Should().Be(RelationalWriteOperationKind.Put);
        _capturedExecutorRequest
            .TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    existingDocumentId,
                    documentUuid,
                    observedContentVersion
                )
            );
        _capturedExecutorRequest.ReadPlan.Should().BeSameAs(expectedReadPlan);
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
    }

    [Test]
    public async Task It_short_circuits_missing_put_targets_to_not_exists_before_executor_execution()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        A.CallTo(() =>
                _targetLookupResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetLookupResult>(
                    new RelationalWriteTargetLookupResult.NotFound()
                )
            );

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
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
    public async Task It_maps_post_request_validation_failures_out_of_the_executor_to_validation_results()
    {
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.schoolYear"),
            "Column 'SchoolYear' expected an integer."
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Throws(new RelationalWriteRequestValidationException([validationFailure]));

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
    public async Task It_maps_put_request_validation_failures_out_of_the_executor_to_validation_results()
    {
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.addresses[1]"),
            "Duplicate submitted semantic identity values are not allowed."
        );

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .Throws(new RelationalWriteRequestValidationException([validationFailure]));

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
    public async Task It_returns_the_descriptor_write_path_guard_rail_for_post_requests()
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
                    "Write plan for resource 'Ed-Fi.SchoolTypeDescriptor' was intentionally omitted: "
                        + "storage kind 'SharedDescriptorTable' uses the descriptor write path instead of compiled relational-table write plans. "
                        + "Next story: E07-S06 (06-descriptor-writes.md)."
                )
            );
    }

    [Test]
    public async Task It_returns_the_descriptor_write_path_guard_rail_for_put_requests()
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
                    "Write plan for resource 'Ed-Fi.SchoolTypeDescriptor' was intentionally omitted: "
                        + "storage kind 'SharedDescriptorTable' uses the descriptor write path instead of compiled relational-table write plans. "
                        + "Next story: E07-S06 (06-descriptor-writes.md)."
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

        A.CallTo(() =>
                _targetLookupResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetLookupResult>(
                    new RelationalWriteTargetLookupResult.ExistingDocument(123L, documentUuid, 11L)
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UnknownFailure(
                    "Read plan lookup failed for resource 'Ed-Fi.School' in mapping set "
                        + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table read plan, "
                        + "but no entry was found. This indicates an internal compilation/selection bug."
                )
            );
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
    public async Task It_does_not_remap_internal_target_lookup_invalid_operation_failures()
    {
        var internalFailure = new InvalidOperationException(
            "Relational write target lookup returned multiple rows for resource 'Ed-Fi.School'."
        );

        A.CallTo(() =>
                _targetLookupResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Throws(internalFailure);

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        Func<Task> act = async () => _ = await _sut.UpdateDocumentById(updateRequest);

        var thrownException = await act.Should().ThrowAsync<InvalidOperationException>();
        thrownException.Which.Message.Should().Be(internalFailure.Message);
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
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
