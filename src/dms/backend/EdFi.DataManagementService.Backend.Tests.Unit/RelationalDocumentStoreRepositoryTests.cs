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
    private IRelationalWriteTargetContextResolver _targetContextResolver = null!;
    private IReferenceResolver _referenceResolver = null!;
    private IRelationalWriteFlattener _writeFlattener = null!;
    private IRelationalWriteTerminalStage _terminalStage = null!;
    private FlatteningInput _capturedFlatteningInput = null!;
    private RelationalWriteTerminalStageRequest _capturedTerminalStageRequest = null!;

    [SetUp]
    public void Setup()
    {
        _targetContextResolver = A.Fake<IRelationalWriteTargetContextResolver>();
        _referenceResolver = A.Fake<IReferenceResolver>();
        _writeFlattener = A.Fake<IRelationalWriteFlattener>();
        _terminalStage = A.Fake<IRelationalWriteTerminalStage>();
        A.CallTo(() =>
                _targetContextResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
                Task.FromResult<RelationalWriteTargetContext>(
                    new RelationalWriteTargetContext.CreateNew(call.GetArgument<DocumentUuid>(3))
                )
            );
        A.CallTo(() =>
                _targetContextResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
                Task.FromResult<RelationalWriteTargetContext>(
                    new RelationalWriteTargetContext.CreateNew(call.GetArgument<DocumentUuid>(2))
                )
            );
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(CreateResolvedReferenceSet()));
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
            .Invokes(call => _capturedFlatteningInput = call.GetArgument<FlatteningInput>(0)!)
            .ReturnsLazily(call => CreateFlattenedWriteSet(call.GetArgument<FlatteningInput>(0)!.WritePlan));
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
                _capturedTerminalStageRequest = call.GetArgument<RelationalWriteTerminalStageRequest>(0)!
            )
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteTerminalStageResult>(
                    new RelationalWriteTerminalStageResult.Upsert(
                        new UpsertResult.UnknownFailure("Unexpected terminal-stage test fallback.")
                    )
                )
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _targetContextResolver,
            _referenceResolver,
            _writeFlattener,
            _terminalStage
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
    public async Task It_routes_post_requests_through_flattening_and_the_terminal_stage()
    {
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var resolvedReferences = CreateResolvedReferenceSet();
        var requestBody = CreateRequestBody();
        var traceId = new TraceId("post-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var flattenedWriteSet = CreateFlattenedWriteSet(CreateRootPlan());

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(resolvedReferences));
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
            .Invokes(call => _capturedFlatteningInput = call.GetArgument<FlatteningInput>(0)!)
            .Returns(flattenedWriteSet);
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
                _capturedTerminalStageRequest = call.GetArgument<RelationalWriteTerminalStageRequest>(0)!
            )
            .Returns(
                Task.FromResult<RelationalWriteTerminalStageResult>(
                    new RelationalWriteTerminalStageResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid)
                    )
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo)
            .Returns(CreateDocumentInfo([documentReference], [descriptorReference]));
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(traceId);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid));
        A.CallTo(() =>
                _targetContextResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _referenceResolver.ResolveAsync(
                    A<ReferenceResolverRequest>.That.Matches(request =>
                        ReferenceEquals(request.MappingSet, upsertRequest.MappingSet)
                        && request.RequestResource == new QualifiedResourceName("Ed-Fi", "School")
                        && request.DocumentReferences.Count == 1
                        && request.DocumentReferences[0] == documentReference
                        && request.DescriptorReferences.Count == 1
                        && request.DescriptorReferences[0] == descriptorReference
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();

        _capturedFlatteningInput.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        _capturedFlatteningInput
            .WritePlan.Model.Resource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "School"));
        _capturedFlatteningInput.SelectedBody.Should().BeSameAs(requestBody);
        _capturedFlatteningInput.ResolvedReferences.Should().BeSameAs(resolvedReferences);
        var createNewTargetContext = _capturedFlatteningInput
            .TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.CreateNew>()
            .Subject;
        createNewTargetContext.DocumentUuid.Should().Be(documentUuid);
        _capturedTerminalStageRequest.FlatteningInput.Should().BeSameAs(_capturedFlatteningInput);
        _capturedTerminalStageRequest.FlattenedWriteSet.Should().BeSameAs(flattenedWriteSet);
        _capturedTerminalStageRequest.TraceId.Should().Be(traceId);
        _capturedTerminalStageRequest.DiagnosticIdentifier.Should().BeNull();
    }

    [Test]
    public async Task It_routes_put_requests_through_flattening_and_the_terminal_stage()
    {
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var resolvedReferences = CreateResolvedReferenceSet();
        var traceId = new TraceId("put-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var existingDocumentId = 123L;
        var requestBody = CreateRequestBody("Roosevelt High");
        var flattenedWriteSet = CreateFlattenedWriteSet(CreateRootPlan());

        A.CallTo(() =>
                _targetContextResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetContext>(
                    new RelationalWriteTargetContext.ExistingDocument(existingDocumentId, documentUuid)
                )
            );
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(resolvedReferences));
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
            .Invokes(call => _capturedFlatteningInput = call.GetArgument<FlatteningInput>(0)!)
            .Returns(flattenedWriteSet);
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .Invokes(call =>
                _capturedTerminalStageRequest = call.GetArgument<RelationalWriteTerminalStageRequest>(0)!
            )
            .Returns(
                Task.FromResult<RelationalWriteTerminalStageResult>(
                    new RelationalWriteTerminalStageResult.Update(
                        new UpdateResult.UpdateSuccess(documentUuid)
                    )
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo)
            .Returns(CreateDocumentInfo([documentReference], [descriptorReference]));
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => updateRequest.TraceId).Returns(traceId);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid));
        A.CallTo(() =>
                _targetContextResolver.ResolveForPutAsync(
                    A<MappingSet>._,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _referenceResolver.ResolveAsync(
                    A<ReferenceResolverRequest>.That.Matches(request =>
                        ReferenceEquals(request.MappingSet, updateRequest.MappingSet)
                        && request.RequestResource == new QualifiedResourceName("Ed-Fi", "School")
                        && request.DocumentReferences.Count == 1
                        && request.DocumentReferences[0] == documentReference
                        && request.DescriptorReferences.Count == 1
                        && request.DescriptorReferences[0] == descriptorReference
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();

        _capturedFlatteningInput.OperationKind.Should().Be(RelationalWriteOperationKind.Put);
        _capturedFlatteningInput.SelectedBody.Should().BeSameAs(requestBody);
        _capturedFlatteningInput.ResolvedReferences.Should().BeSameAs(resolvedReferences);
        var existingDocumentTargetContext = _capturedFlatteningInput
            .TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.ExistingDocument>()
            .Subject;
        existingDocumentTargetContext.DocumentId.Should().Be(existingDocumentId);
        existingDocumentTargetContext.DocumentUuid.Should().Be(documentUuid);
        _capturedTerminalStageRequest.FlatteningInput.Should().BeSameAs(_capturedFlatteningInput);
        _capturedTerminalStageRequest.FlattenedWriteSet.Should().BeSameAs(flattenedWriteSet);
        _capturedTerminalStageRequest.TraceId.Should().Be(traceId);
    }

    [Test]
    public async Task It_short_circuits_post_requests_when_document_reference_resolution_fails()
    {
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var invalidDocumentReference = DocumentReferenceFailure.From(
            documentReference,
            DocumentReferenceFailureReason.Missing
        );

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult(
                    CreateResolvedReferenceSet(invalidDocumentReferences: [invalidDocumentReference])
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
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._)).MustNotHaveHappened();
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_short_circuits_post_requests_when_descriptor_reference_resolution_fails()
    {
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var invalidDescriptorReference = DescriptorReferenceFailure.From(
            descriptorReference,
            DescriptorReferenceFailureReason.Missing
        );

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult(
                    CreateResolvedReferenceSet(invalidDescriptorReferences: [invalidDescriptorReference])
                )
            );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo)
            .Returns(CreateDocumentInfo(descriptorReferences: [descriptorReference]));
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result
            .Should()
            .BeEquivalentTo(new UpsertResult.UpsertFailureReference([], [invalidDescriptorReference]));
    }

    [Test]
    public async Task It_short_circuits_put_requests_when_document_reference_resolution_fails()
    {
        var documentReference = CreateDocumentReference(
            _localEducationAgencyResourceInfo,
            "$.localEducationAgencyReference"
        );
        var invalidDocumentReference = DocumentReferenceFailure.From(
            documentReference,
            DocumentReferenceFailureReason.IncompatibleTargetType
        );

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult(
                    CreateResolvedReferenceSet(invalidDocumentReferences: [invalidDocumentReference])
                )
            );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo([documentReference]));
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result
            .Should()
            .BeEquivalentTo(new UpdateResult.UpdateFailureReference([invalidDocumentReference], []));
    }

    [Test]
    public async Task It_short_circuits_put_requests_when_descriptor_reference_resolution_fails()
    {
        var descriptorReference = CreateDescriptorReference(
            _schoolCategoryDescriptorResourceInfo,
            "$.schoolCategoryDescriptor"
        );
        var invalidDescriptorReference = DescriptorReferenceFailure.From(
            descriptorReference,
            DescriptorReferenceFailureReason.DescriptorTypeMismatch
        );

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult(
                    CreateResolvedReferenceSet(invalidDescriptorReferences: [invalidDescriptorReference])
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
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._)).MustNotHaveHappened();
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_post_request_validation_failures_out_of_the_flattener_to_validation_results()
    {
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.schoolYear"),
            "Column 'SchoolYear' expected an integer."
        );

        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
            .Throws(new RelationalWriteRequestValidationException([validationFailure]));

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpsertFailureValidation([validationFailure]));
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_put_request_validation_failures_out_of_the_flattener_to_validation_results()
    {
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.addresses[1]"),
            "Duplicate submitted semantic identity values are not allowed."
        );

        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
            .Throws(new RelationalWriteRequestValidationException([validationFailure]));

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateFailureValidation([validationFailure]));
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
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

    private static ResolvedReferenceSet CreateResolvedReferenceSet(
        DocumentReferenceFailure[]? invalidDocumentReferences = null,
        DescriptorReferenceFailure[]? invalidDescriptorReferences = null
    )
    {
        return new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: invalidDocumentReferences ?? [],
            InvalidDescriptorReferences: invalidDescriptorReferences ?? [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
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

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(resourceModel, resourceKey),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resourceKey.Resource] = new ResourceWritePlan(resourceModel, [rootPlan]),
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            }
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
            }
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
            }
        );
    }

    private static ResourceKeyEntry CreateResourceKeyEntry(ResourceInfo resourceInfo)
    {
        return new ResourceKeyEntry(
            ResourceKeyId: 1,
            Resource: new QualifiedResourceName(
                resourceInfo.ProjectName.Value,
                resourceInfo.ResourceName.Value
            ),
            ResourceVersion: resourceInfo.ResourceVersion.Value,
            IsAbstractResource: false
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
                    null,
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

    private static FlattenedWriteSet CreateFlattenedWriteSet(ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        return CreateFlattenedWriteSet(
            writePlan.TablePlansInDependencyOrder.Single(plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.Root
            )
        );
    }

    private static FlattenedWriteSet CreateFlattenedWriteSet(TableWritePlan tableWritePlan)
    {
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(
                tableWritePlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );
    }
}
