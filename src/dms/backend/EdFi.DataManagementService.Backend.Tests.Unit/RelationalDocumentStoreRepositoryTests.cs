// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Unit.Composite;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalDocumentStoreRepositoryTests
{
    public enum RelationshipAuthorizationEndpoint
    {
        GetById,
        Post,
        Put,
        Delete,
    }

    private static IEnumerable<TestCaseData> DescriptorWritePreconditions()
    {
        yield return new TestCaseData(new WritePrecondition.None());
        yield return new TestCaseData(new WritePrecondition.IfMatch("plain-opaque-value"));
        yield return new TestCaseData(new WritePrecondition.IfMatch("\"72\""));
        yield return new TestCaseData(new WritePrecondition.IfMatch("   "));
        yield return new TestCaseData(new WritePrecondition.IfMatch("\"72\", W/\"73\""));
    }

    private static readonly ResourceInfo _schoolResourceInfo = CreateResourceInfo("School");
    private const string StampStyleEtagPattern = "^\"\\d+\"$";
    private static readonly DateTimeOffset _readTargetContentLastModifiedAt = new(
        2026,
        4,
        11,
        12,
        30,
        45,
        TimeSpan.FromHours(-5)
    );

    // A deterministic composed-shaped write-result etag. These tests verify that the repository passes
    // the write handler's/executor's etag through unchanged (and that it is neither the client's stale
    // request etag nor a stamp-style validator), not the etag format, so any stable opaque value that is
    // produced without the etag formatter suffices.
    private const string ComposedWriteResultEtag = "1-a1b2c3d4.j._.l.i";
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
    private IReferenceResolver _referenceResolver = null!;
    private IDescriptorReadHandler _descriptorReadHandler = null!;
    private IDocumentHydrator _documentHydrator = null!;
    private IRelationalReadTargetLookupService _readTargetLookupService = null!;
    private IRelationalReadMaterializer _readMaterializer = null!;
    private IReadableProfileProjector _readableProfileProjector = null!;
    private RecordingRelationalCurrentEtagPreconditionChecker _currentEtagPreconditionChecker = null!;
    private IRelationalCommandExecutor _commandExecutor = null!;
    private ConfigurableRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
    private IRelationalDeleteConstraintResolver _deleteConstraintResolver = null!;
    private RecordingLogger<RelationalDocumentStoreRepository> _logger = null!;
    private RecordingWriteSessionFactory _writeSessionFactory = null!;
    private DeleteCommandResponder? _deleteCommandResponder;
    private ISingleRecordRelationshipAuthorizationExecutor _singleRecordRelationshipAuthorizationExecutor =
        null!;
    private INamespaceAuthorizationExecutor _namespaceAuthorizationExecutor = null!;
    private ICustomViewAuthorizationExecutor _customViewAuthorizationExecutor = null!;
    private RelationalWriteExecutorInput _capturedExecutorRequest = null!;
    private List<RelationalWriteExecutorInput> _capturedExecutorRequests = null!;

    private static RelationalEdOrgAuthorizationSubjectSelector CreateAuthorizationSubjectSelector() =>
        new(new RelationalEdOrgAuthorizationElementResolutionCache());

    [SetUp]
    public void Setup()
    {
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _referenceResolver = A.Fake<IReferenceResolver>();
        _descriptorReadHandler = A.Fake<IDescriptorReadHandler>();
        _documentHydrator = A.Fake<IDocumentHydrator>();
        _readTargetLookupService = A.Fake<IRelationalReadTargetLookupService>();
        _readMaterializer = A.Fake<IRelationalReadMaterializer>();
        _readableProfileProjector = A.Fake<IReadableProfileProjector>();
        _currentEtagPreconditionChecker = new RecordingRelationalCurrentEtagPreconditionChecker();
        _commandExecutor = A.Fake<IRelationalCommandExecutor>();
        _writeExceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier();
        _deleteConstraintResolver = A.Fake<IRelationalDeleteConstraintResolver>();
        _logger = new RecordingLogger<RelationalDocumentStoreRepository>();
        _writeSessionFactory = new RecordingWriteSessionFactory(_commandExecutor);
        _deleteCommandResponder = null;
        _singleRecordRelationshipAuthorizationExecutor =
            A.Fake<ISingleRecordRelationshipAuthorizationExecutor>();
        _namespaceAuthorizationExecutor = A.Fake<INamespaceAuthorizationExecutor>();
        _customViewAuthorizationExecutor = A.Fake<ICustomViewAuthorizationExecutor>();
        _capturedExecutorRequests = [];
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .ReturnsLazily(() =>
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UnknownFailure("Unexpected write-executor test fallback.")
                    )
                )
            );
        A.CallTo(() =>
                _descriptorReadHandler.HandleGetByIdAsync(
                    A<DescriptorGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (DescriptorGetByIdRequest request, CancellationToken _) =>
                    Task.FromResult<GetResult>(
                        new GetResult.GetFailureNotImplemented(
                            $"Relational descriptor GET by id is not implemented for resource '{request.Resource.ProjectName}.{request.Resource.ResourceName}'."
                        )
                    )
            );
        A.CallTo(() =>
                _descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (DescriptorQueryRequest request, CancellationToken _) =>
                    Task.FromResult<QueryResult>(
                        new QueryResult.QueryFailureNotImplemented(
                            $"Relational descriptor GET-many is not implemented for resource '{request.Resource.ProjectName}.{request.Resource.ResourceName}'."
                        )
                    )
            );

        _sut = new RelationalDocumentStoreRepository(
            _logger,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );
    }

    private void UseDescriptorWriteHandler(IDescriptorWriteHandler descriptorWriteHandler)
    {
        _sut = new RelationalDocumentStoreRepository(
            _logger,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorWriteHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );
    }

    private void UseReadAccelerationCoordinator(
        IDocumentCacheReadAccelerationCoordinator readAccelerationCoordinator
    )
    {
        _sut = new RelationalDocumentStoreRepository(
            _logger,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: readAccelerationCoordinator
        );
    }

    private sealed class RecordingReadAccelerationCoordinator : IDocumentCacheReadAccelerationCoordinator
    {
        public int GetByIdAttempts { get; private set; }
        public int QueryAttempts { get; private set; }
        public DocumentCacheReadAccelerationGetByIdRequest? SelectedGetByIdRequest { get; private set; }
        public DocumentCacheReadAccelerationQueryRequest? SelectedQueryRequest { get; private set; }
        public DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate? SelectedGetByIdCandidate
        {
            get;
            private set;
        }
        public DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage? SelectedQueryCandidatePage
        {
            get;
            private set;
        }

        public async Task<GetResult> GetByIdAsync(
            DocumentCacheReadAccelerationGetByIdRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(request);

            GetByIdAttempts++;

            var selection = await request.SelectAuthorizedCandidate(cancellationToken).ConfigureAwait(false);

            switch (selection)
            {
                case DocumentCacheReadAccelerationGetByIdSelectionResult.Complete complete:
                    return complete.Result;

                case DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate candidate:
                    SelectedGetByIdRequest = request;
                    SelectedGetByIdCandidate = candidate;
                    return await candidate.RelationalFallback(cancellationToken).ConfigureAwait(false);

                default:
                    throw new InvalidOperationException(
                        $"Unsupported GET candidate selection result '{selection.GetType().Name}'."
                    );
            }
        }

        public async Task<QueryResult> QueryAsync(
            DocumentCacheReadAccelerationQueryRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(request);

            QueryAttempts++;

            var selection = await request
                .SelectAuthorizedCandidatePage(cancellationToken)
                .ConfigureAwait(false);

            switch (selection)
            {
                case DocumentCacheReadAccelerationQuerySelectionResult.Complete complete:
                    return complete.Result;

                case DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage candidatePage:
                    SelectedQueryRequest = request;
                    SelectedQueryCandidatePage = candidatePage;
                    return await candidatePage.RelationalFallback(cancellationToken).ConfigureAwait(false);

                default:
                    throw new InvalidOperationException(
                        $"Unsupported query candidate selection result '{selection.GetType().Name}'."
                    );
            }
        }
    }

    [Test]
    public async Task It_materializes_successful_get_requests_through_the_single_document_read_path()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(documentUuid, mappingSet, _schoolResourceInfo);
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
        );
        var materializedDocument = JsonNode.Parse("""{"id":"hydrated","name":"Lincoln High"}""")!;
        RelationalReadMaterializationRequest capturedReadRequest = null!;
        HydrationExecutionOptions? capturedExecutionOptions = null;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedExecutionOptions = call.GetArgument<HydrationExecutionOptions>(2))
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
        capturedReadRequest.ReadMode.Should().Be(RelationalReadMaterializationMode.ExternalResponse);
        capturedExecutionOptions.Should().NotBeNull();
        capturedExecutionOptions!
            .UseSingleDocumentFastPath.Should()
            .BeTrue("GET-by-id hydrates a single known document and should bypass temp-table hydration");
        A.CallTo(() =>
                _descriptorReadHandler.HandleGetByIdAsync(
                    A<DescriptorGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_exposes_an_authorized_get_candidate_to_read_acceleration_before_hydration()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc"));
        var contentLastModifiedAt = new DateTimeOffset(2026, 6, 1, 10, 15, 30, TimeSpan.Zero);
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var expectedResourceKeyId = mappingSet.ResourceKeyIdByResource[resource];
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var getRequest = CreateGetRequest(documentUuid, mappingSet, _schoolResourceInfo);
        var materializedDocument = JsonNode.Parse("""{"id":"hydrated","name":"Lincoln High"}""")!;
        HydrationExecutionOptions? capturedExecutionOptions = null;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    contentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedExecutionOptions = call.GetArgument<HydrationExecutionOptions>(2))
            .Returns(
                CreateHydratedPage(
                    readPlan,
                    CreateDocumentMetadataRow(
                        documentUuid,
                        345L,
                        91L,
                        contentLastModifiedAt,
                        expectedResourceKeyId
                    ),
                    (345L, "Lincoln High")
                )
            );
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(materializedDocument);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        readAccelerationCoordinator.GetByIdAttempts.Should().Be(1);
        readAccelerationCoordinator.SelectedGetByIdRequest.Should().NotBeNull();
        readAccelerationCoordinator
            .SelectedGetByIdRequest!.ResourceKind.Should()
            .Be(DocumentCacheReadAccelerationResourceKind.Resource);
        readAccelerationCoordinator
            .SelectedGetByIdCandidate!.AuthorizedCandidate.Should()
            .Be(
                new DocumentCacheReadAccelerationCandidate(
                    345L,
                    documentUuid,
                    expectedResourceKeyId,
                    91L,
                    contentLastModifiedAt
                )
            );
        capturedExecutionOptions.Should().NotBeNull();
        capturedExecutionOptions!
            .UseSingleDocumentFastPath.Should()
            .BeTrue("the cache-miss fallback still hydrates only the selected candidate body");
    }

    [Test]
    public async Task It_hydrates_the_authorized_get_candidate_on_cache_fallback_without_rerunning_lookup_or_authorization()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd"));
        var contentLastModifiedAt = new DateTimeOffset(2026, 6, 1, 11, 15, 30, TimeSpan.Zero);
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var resourceKeyId = mappingSet.ResourceKeyIdByResource[resource];
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L, contentLastModifiedAt, resourceKeyId),
            (345L, "Lincoln High")
        );
        RelationalReadMaterializationRequest materializationRequest = null!;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    contentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<SingleRecordRelationshipAuthorizationExecutionResult>(
                    new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L)
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call =>
                materializationRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!
            )
            .Returns(JsonNode.Parse("""{"id":"authorized-cache-fallback"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        materializationRequest.DocumentMetadata.Should().BeSameAs(hydratedPage.DocumentMetadata[0]);
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_reruns_the_no_cache_get_when_selected_candidate_fallback_metadata_drifts()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee"));
        var selectedContentLastModifiedAt = new DateTimeOffset(2026, 6, 1, 11, 30, 0, TimeSpan.Zero);
        var rerunContentLastModifiedAt = new DateTimeOffset(2026, 6, 1, 11, 45, 0, TimeSpan.Zero);
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var resourceKeyId = mappingSet.ResourceKeyIdByResource[resource];
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var getRequest = CreateGetRequest(documentUuid, mappingSet, _schoolResourceInfo);
        var selectedFallbackHydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 92L, selectedContentLastModifiedAt, resourceKeyId),
            (345L, "Drifted selected row")
        );
        var rerunHydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 678L, 93L, rerunContentLastModifiedAt, resourceKeyId),
            (678L, "No-cache rerun row")
        );
        List<PageKeysetSpec> hydratedKeysets = [];
        RelationalReadMaterializationRequest materializationRequest = null!;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .ReturnsNextFromSequence(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    selectedContentLastModifiedAt
                ),
                new RelationalReadTargetLookupResult.ExistingDocument(
                    678L,
                    documentUuid,
                    93L,
                    rerunContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => hydratedKeysets.Add(call.GetArgument<PageKeysetSpec>(1)!))
            .ReturnsLazily(
                (
                    ResourceReadPlan _,
                    PageKeysetSpec keyset,
                    HydrationExecutionOptions _,
                    CancellationToken _
                ) =>
                    keyset is PageKeysetSpec.Single { DocumentId: 345L }
                        ? selectedFallbackHydratedPage
                        : rerunHydratedPage
            );
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call =>
                materializationRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!
            )
            .Returns(JsonNode.Parse("""{"id":"no-cache-rerun"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be("no-cache-rerun");
        hydratedKeysets.Should().HaveCount(2);
        hydratedKeysets[0].Should().Be(new PageKeysetSpec.Single(345L));
        hydratedKeysets[1].Should().Be(new PageKeysetSpec.Single(678L));
        materializationRequest.DocumentMetadata.Should().BeSameAs(rerunHydratedPage.DocumentMetadata[0]);
        materializationRequest
            .TableRowsInDependencyOrder.Should()
            .BeSameAs(rerunHydratedPage.TableRowsInDependencyOrder);
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_routes_descriptor_get_requests_through_the_descriptor_read_handler_based_on_mapping_set_metadata()
    {
        var descriptorReadHandler = A.Fake<IDescriptorReadHandler>();
        var descriptorResourceInfo = CreateResourceInfo("SchoolTypeDescriptor");
        var mappingSet = CreateDescriptorOnlyMappingSet(descriptorResourceInfo);
        var documentUuid = new DocumentUuid(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var projectionContext = new ReadableProfileProjectionContext(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("description")],
                [],
                [],
                []
            ),
            new HashSet<string> { "id", "namespace", "codeValue", "_etag", "_lastModifiedDate" }
        );
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators =
        [
            CreateAuthorizationStrategyEvaluator(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
            ),
        ];
        var expectedResult = new GetResult.GetFailureNotImplemented("delegated descriptor get result");
        DescriptorGetByIdRequest capturedRequest = null!;

        A.CallTo(() =>
                descriptorReadHandler.HandleGetByIdAsync(
                    A<DescriptorGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorGetByIdRequest>(0)!)
            .Returns(expectedResult);

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            descriptorResourceInfo,
            RelationalGetRequestReadMode.StoredDocument,
            projectionContext,
            authorizationStrategyEvaluators
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeSameAs(expectedResult);
        capturedRequest.MappingSet.Should().BeSameAs(mappingSet);
        capturedRequest.Resource.Should().Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
        capturedRequest.DocumentUuid.Should().Be(documentUuid);
        capturedRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.StoredDocument);
        capturedRequest.AuthorizationStrategyEvaluators.Should().BeSameAs(authorizationStrategyEvaluators);
        capturedRequest.ReadableProfileProjectionContext.Should().BeSameAs(projectionContext);
        capturedRequest.TraceId.Value.Should().Be("get-trace");
        A.CallTo(() =>
                descriptorReadHandler.HandleGetByIdAsync(
                    A<DescriptorGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_precise_not_implemented_failure_for_descriptor_get_requests()
    {
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.NewGuid()),
            CreateDescriptorOnlyMappingSet(_descriptorResourceInfo),
            _descriptorResourceInfo
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
    public async Task It_delegates_descriptor_get_authorization_that_requires_filtering_to_the_descriptor_read_handler()
    {
        var expectedResult = new GetResult.GetFailureNotImplemented(
            "Relational descriptor GET authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or with 'NamespaceBased' and/or 'NoFurtherAuthorizationRequired' are currently supported."
        );

        A.CallTo(() =>
                _descriptorReadHandler.HandleGetByIdAsync(
                    A<DescriptorGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(expectedResult);

        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.NewGuid()),
            CreateDescriptorOnlyMappingSet(_descriptorResourceInfo),
            _descriptorResourceInfo,
            authorizationStrategyEvaluators: [new("RelationshipsWithEdOrgsOnly", [], FilterOperator.And)]
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeSameAs(expectedResult);
        A.CallTo(() =>
                _descriptorReadHandler.HandleGetByIdAsync(
                    A<DescriptorGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
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
    public async Task It_allows_no_further_authorization_required_for_descriptor_get_requests()
    {
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.NewGuid()),
            CreateDescriptorOnlyMappingSet(_descriptorResourceInfo),
            _descriptorResourceInfo,
            authorizationStrategyEvaluators:
            [
                new(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired, [], FilterOperator.Or),
            ]
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
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call => capturedReadRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!)
            .Returns(JsonNode.Parse("""{"name":"Roosevelt High"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        capturedReadRequest.ReadMode.Should().Be(RelationalReadMaterializationMode.StoredDocument);
    }

    [Test]
    public async Task It_opts_out_of_document_reference_lookup_for_stored_document_mode_get_requests()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-eeeeeeeeeeee"));
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            RelationalGetRequestReadMode.StoredDocument
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 346L, 94L),
            (346L, "Eastside Elementary")
        );
        HydrationExecutionOptions? capturedExecutionOptions = null;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    346L,
                    documentUuid,
                    94L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(346L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedExecutionOptions = call.GetArgument<HydrationExecutionOptions>(2))
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"name":"Eastside Elementary"}""")!);

        await _sut.GetDocumentById(getRequest);

        capturedExecutionOptions.Should().NotBeNull();
        capturedExecutionOptions!
            .IncludeDocumentReferenceLookup.Should()
            .BeFalse(
                "StoredDocument-mode GETs do not emit link, so the auxiliary FK lookup must be suppressed at hydration time"
            );
        capturedExecutionOptions
            .IncludeDescriptorProjection.Should()
            .BeTrue("descriptor URIs are still needed for stored-document materialization");
    }

    [Test]
    public async Task It_keeps_document_reference_lookup_on_for_external_response_get_requests()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-ffffffffffff"));
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            RelationalGetRequestReadMode.ExternalResponse
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 347L, 95L),
            (347L, "Northwood High")
        );
        HydrationExecutionOptions? capturedExecutionOptions = null;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    347L,
                    documentUuid,
                    95L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(347L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedExecutionOptions = call.GetArgument<HydrationExecutionOptions>(2))
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"name":"Northwood High"}""")!);

        await _sut.GetDocumentById(getRequest);

        capturedExecutionOptions.Should().NotBeNull();
        capturedExecutionOptions!
            .IncludeDocumentReferenceLookup.Should()
            .BeTrue("ExternalResponse-mode GETs emit link, so the auxiliary FK lookup must run");
    }

    [Test]
    public async Task It_executes_supported_get_by_id_relationship_authorization_before_hydration()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-aaaaaaaaaaaa"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [200L, 100L, 100L]
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
        );
        var order = 0;
        var authorizationOrder = 0;
        var hydrationOrder = 0;
        SingleRecordRelationshipAuthorizationExecutionRequest capturedAuthorizationRequest = null!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                authorizationOrder = ++order;
                capturedAuthorizationRequest =
                    call.GetArgument<SingleRecordRelationshipAuthorizationExecutionRequest>(0)!;
            })
            .Returns(
                Task.FromResult<SingleRecordRelationshipAuthorizationExecutionResult>(
                    new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L)
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => hydrationOrder = ++order)
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"id":"authorized"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        authorizationOrder.Should().Be(1);
        hydrationOrder.Should().Be(2);
        capturedAuthorizationRequest.MappingSet.Should().BeSameAs(mappingSet);
        capturedAuthorizationRequest.DocumentId.Should().Be(345L);
        capturedAuthorizationRequest.EmittedAuth1Index.Should().Be(0);
        capturedAuthorizationRequest.CheckSpecs.Should().ContainSingle();
        capturedAuthorizationRequest
            .ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds.Should()
            .Equal(100L, 200L);
    }

    [Test]
    public async Task It_returns_relationship_not_authorized_and_skips_hydration_when_get_authorization_fails()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-bbbbbbbbbbbb"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var relationshipFailure = CreateRelationshipFailure();
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<SingleRecordRelationshipAuthorizationExecutionResult>(
                    new SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized(
                        relationshipFailure
                    )
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.Should().BeSameAs(relationshipFailure);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_security_configuration_when_get_relationship_authorization_payload_is_invalid()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-bebebebebebe"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<SingleRecordRelationshipAuthorizationExecutionResult>(
                    new SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                        RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError,
                        [
                            new SecurityConfigurationFailureDiagnostic(
                                ProviderOrPlannerFailureKind: "RelationshipAuthorization.Auth1.PayloadParseFailed"
                            ),
                        ]
                    )
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );
        failure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be("RelationshipAuthorization.Auth1.PayloadParseFailed");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_short_circuits_get_by_id_relationship_authorization_with_empty_edorg_claims()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-cccccccccccc"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: []
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Should()
            .AllSatisfy(subject =>
                subject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship)
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_not_implemented_before_get_by_id_target_lookup_when_authorization_includes_known_out_of_scope_strategies()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-cfcfcfcfcfcf"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureNotImplemented>().Subject;
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        AssertSupportedRelationshipStrategyNames(failure.FailureMessage);
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_preserves_missing_get_by_id_target_not_found_for_empty_edorg_claims()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-dededededede"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: []
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
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_document_when_stored_namespace_matches_a_prefix()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-aaaaaaaaaaaa"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "uri://ed-fi.org/Thing")
        );
        NamespaceAuthorizationExecutionRequest capturedNamespaceRequest = null!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
                capturedNamespaceRequest = call.GetArgument<NamespaceAuthorizationExecutionRequest>(0)!
            )
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.Authorized()
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"id":"authorized-by-namespace"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        capturedNamespaceRequest.Should().NotBeNull();
        capturedNamespaceRequest.DocumentId.Should().Be(345L);
        capturedNamespaceRequest.ProposedNamespace.Should().BeNull();
        capturedNamespaceRequest
            .Checks.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(NamespaceAuthorizationCheckValueSource.Stored);
        capturedNamespaceRequest
            .NamespacePrefixParameterization.ConfiguredPrefixesInOrder.Should()
            .Equal("uri://ed-fi.org/");
    }

    [Test]
    public async Task It_executes_people_get_by_id_relationship_authorization_before_hydration()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-cacacacacaca"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(
            _studentResourceInfo
        );
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _studentResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "People authorized")
        );
        SingleRecordRelationshipAuthorizationExecutionRequest capturedAuthorizationRequest = null!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
                capturedAuthorizationRequest =
                    call.GetArgument<SingleRecordRelationshipAuthorizationExecutionRequest>(0)!
            )
            .Returns(
                Task.FromResult<SingleRecordRelationshipAuthorizationExecutionResult>(
                    new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L)
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"id":"authorized-people"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        capturedAuthorizationRequest.CheckSpecs.Should().ContainSingle();
        var checkSpec = capturedAuthorizationRequest.CheckSpecs[0];
        checkSpec
            .ConfiguredStrategy.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        checkSpec.Subjects.Should().Contain(static subject => subject.IsPersonSubject);
    }

    [Test]
    public async Task It_returns_people_no_claims_get_by_id_failure_metadata_without_hydration()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-cdcdcdcdcdcd"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(
            _studentResourceInfo
        );
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _studentResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: []
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Stored);
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        var failedStrategy = failure.RelationshipFailure.FailedStrategies.Should().ContainSingle().Which;
        failedStrategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        failedStrategy
            .Hint.Should()
            .Contain("requires at least one claim EducationOrganizationId")
            .And.Contain("auth.EducationOrganizationIdToStudentDocumentId")
            .And.Contain("StudentSchoolAssociation");

        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Which;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        failedSubject
            .SecurableElements.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationSecurableElement(
                    "Student",
                    "$.studentUniqueId",
                    "StudentUniqueId"
                )
            );
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PersonKind.Should().Be("Student");
        failedSubject.PersonSubject.StoredAnchor.RootTableName.Should().Be("edfi.Student");
        failedSubject.PersonSubject.StoredAnchor.RootDocumentIdColumnName.Should().Be("DocumentId");
        failedSubject
            .PersonSubject.Hint.Should()
            .Be("You may need to create a corresponding 'StudentSchoolAssociation' item.");
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_get_by_id_security_configuration_failure_before_target_lookup_for_invalid_relationship_metadata()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-cbcbcbcbcbcb"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
                ),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("CustomAuthorizationStrategy");
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private const string CustomViewStrategyName = "SchoolWithATag";

    /// <summary>
    /// Records the custom-view validation probes a GET-by-id terminal issues.
    /// </summary>
    private List<string> GivenCustomViewValidationIsRecorded()
    {
        List<string> capturedValidationSql = [];

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        return capturedValidationSql;
    }

    [Test]
    public async Task It_validates_a_get_by_id_custom_view_configured_before_a_namespace_no_prefixes_terminal()
    {
        // The namespace 403 resolves before any target lookup, but a custom view configured ahead of it
        // executes first, so a missing or non-conforming view keeps its own 500.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa01")),
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
        );

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeOfType<GetResult.GetFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    [Test]
    public async Task It_does_not_validate_a_get_by_id_custom_view_configured_after_a_namespace_no_prefixes_terminal()
    {
        // The run would have aborted at the namespace position, so a view configured after it never executes
        // and must not be probed either.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa02")),
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
            ],
            namespacePrefixes: []
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>();
        capturedValidationSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_validates_a_get_by_id_custom_view_configured_before_a_no_usable_root_column_terminal()
    {
        // The no-usable-root-column 500 resolves before any target lookup, but a custom view configured ahead
        // of it executes first, so it is probed before that terminal.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa06")),
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeOfType<GetResult.GetFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no Namespace securable element resolves to a root table column");
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    [Test]
    public async Task It_validates_a_delete_custom_view_configured_before_a_no_usable_root_column_terminal()
    {
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var deleteRequest = CreateCustomViewDeleteRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa07")),
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no Namespace securable element resolves to a root table column");
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    [Test]
    public async Task It_validates_a_delete_custom_view_configured_before_a_namespace_parameterization_terminal()
    {
        // An empty prefix cannot be parameterized into a LIKE predicate, so the plan fails as a configuration
        // error after namespace checks were planned. The view configured ahead of NamespaceBased still runs.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var deleteRequest = CreateCustomViewDeleteRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa08")),
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            namespacePrefixes: [""]
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>();
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    /// <summary>
    /// A DELETE request with a custom view configured ahead of <c>NamespaceBased</c>.
    /// </summary>
    private static IDeleteRequest CreateCustomViewDeleteRequest(
        DocumentUuid documentUuid,
        MappingSet mappingSet,
        IReadOnlyList<string> namespacePrefixes
    )
    {
        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => deleteRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("delete-terminal-trace"));
        A.CallTo(() => deleteRequest.WritePrecondition).Returns(new WritePrecondition.None());
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], namespacePrefixes));

        return deleteRequest;
    }

    [Test]
    public async Task It_validates_a_delete_custom_view_configured_before_a_namespace_no_prefixes_terminal()
    {
        // DELETE has the same terminal shape as GET-by-id and the same obligation: a view configured ahead of
        // the terminal executes first, so it is probed before the 403.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => deleteRequest.MappingSet)
            .Returns(CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo));
        A.CallTo(() => deleteRequest.DocumentUuid)
            .Returns(new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa05")));
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("delete-terminal-trace"));
        A.CallTo(() => deleteRequest.WritePrecondition).Returns(new WritePrecondition.None());
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], []));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    [Test]
    public async Task It_validates_a_get_by_id_custom_view_configured_before_an_ownership_based_terminal()
    {
        // OwnershipBased executes last per auth.md regardless of configured position, so every resolved view
        // runs before its 501.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa03")),
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeOfType<GetResult.GetFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    [Test]
    public async Task It_validates_a_get_by_id_custom_view_configured_before_an_unknown_strategy_terminal()
    {
        // An unrecognized strategy is a 500 from the relationship classifier. A custom view configured ahead of
        // it executes first, so its own configuration failure must not be masked.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var getRequest = CreateGetRequest(
            new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaa04")),
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator("UnknownGetByIdStrategy"),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>();
        capturedValidationSql.Should().ContainSingle().Which.Should().Contain(CustomViewStrategyName);
    }

    private static CustomViewAuthorizationFailure CreateCustomViewFailure(
        CustomViewAuthorizationFailureKind failureKind = CustomViewAuthorizationFailureKind.NoMatchingRow
    ) =>
        new(
            failureKind,
            CustomViewAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            CustomViewStrategyName,
            ReadableSecurableElements: ["School"],
            Hint: "You may need a School with A Tag."
        );

    private void GivenCustomViewAuthorizationReturns(CustomViewAuthorizationExecutionResult result) =>
        A.CallTo(() =>
                _customViewAuthorizationExecutor.ExecuteAsync(
                    A<CustomViewAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(result));

    private void GivenNamespaceAuthorizationReturns(NamespaceAuthorizationExecutionResult result) =>
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(result));

    private void ThenHydrationMustNotHaveHappened() =>
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();

    [Test]
    public async Task It_returns_a_custom_view_403_and_skips_hydration_when_the_stored_basis_is_not_in_the_view()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccc01"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var customViewFailure = CreateCustomViewFailure();
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator(CustomViewStrategyName)]
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid, 91L));
        GivenCustomViewAuthorizationReturns(
            new CustomViewAuthorizationExecutionResult.NotAuthorized(customViewFailure)
        );

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeOfType<GetResult.GetFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure.Should()
            .BeSameAs(customViewFailure);
        // "no reconstitution occurs" is the acceptance criterion, so the denial has to precede hydration.
        ThenHydrationMustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_when_a_custom_view_check_reports_an_unmappable_payload()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccc02"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator(CustomViewStrategyName)]
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid, 91L));
        GivenCustomViewAuthorizationReturns(
            new CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure("bad payload", null)
        );

        var result = await _sut.GetDocumentById(getRequest);

        result
            .Should()
            .BeOfType<GetResult.GetFailureSecurityConfiguration>()
            .Subject.Errors.Should()
            .Equal("bad payload");
        ThenHydrationMustNotHaveHappened();
    }

    [Test]
    public async Task It_reresolves_the_target_and_returns_not_exists_when_a_custom_view_check_reports_a_stale_target()
    {
        // The row was deleted between the unlocked target lookup and the check, so the read boundary
        // re-resolves rather than reporting a denial for a row that no longer exists.
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccc03"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator(CustomViewStrategyName)]
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .ReturnsNextFromSequence(
                new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid, 91L),
                new RelationalReadTargetLookupResult.NotFound()
            );
        GivenCustomViewAuthorizationReturns(new CustomViewAuthorizationExecutionResult.StaleTarget());

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        ThenHydrationMustNotHaveHappened();
    }

    [Test]
    public async Task It_runs_a_custom_view_configured_before_NamespaceBased_first()
    {
        // Both are AND filters executing in CMS-configured order and the first failure is reported, so the
        // namespace check must not run once the earlier custom view has denied.
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccc04"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid, 91L));
        GivenCustomViewAuthorizationReturns(
            new CustomViewAuthorizationExecutionResult.NotAuthorized(CreateCustomViewFailure())
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureCustomViewNotAuthorized>();
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_runs_NamespaceBased_first_when_it_is_configured_before_the_custom_view()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccc05"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var namespaceFailure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(CustomViewStrategyName),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid, 91L));
        GivenNamespaceAuthorizationReturns(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(namespaceFailure)
        );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>();
        A.CallTo(() =>
                _customViewAuthorizationExecutor.ExecuteAsync(
                    A<CustomViewAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_passes_the_resolved_target_document_id_and_a_contiguously_indexed_check_list_to_the_custom_view_executor()
    {
        // The cv1 payload reports only an index and the failure mapper resolves it positionally, so the
        // executor must receive a list whose indexes match their positions.
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccc06"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator(CustomViewStrategyName)]
        );
        CustomViewAuthorizationExecutionRequest? observedRequest = null;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalReadTargetLookupResult.ExistingDocument(345L, documentUuid, 91L));
        A.CallTo(() =>
                _customViewAuthorizationExecutor.ExecuteAsync(
                    A<CustomViewAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => observedRequest = call.GetArgument<CustomViewAuthorizationExecutionRequest>(0))
            .Returns(
                Task.FromResult<CustomViewAuthorizationExecutionResult>(
                    new CustomViewAuthorizationExecutionResult.NotAuthorized(CreateCustomViewFailure())
                )
            );

        await _sut.GetDocumentById(getRequest);

        observedRequest.Should().NotBeNull();
        observedRequest!.DocumentId.Should().Be(345L);
        observedRequest
            .Checks.Select(check => check.Index)
            .Should()
            .Equal(Enumerable.Range(0, observedRequest.Checks.Count));
        observedRequest
            .Checks.Should()
            .AllSatisfy(check =>
                check.ValueSource.Should().Be(CustomViewAuthorizationCheckValueSource.Stored)
            );
    }

    [Test]
    public async Task It_returns_a_namespace_mismatch_403_and_skips_hydration_when_stored_namespace_does_not_match()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-bbbbbbbbbbbb"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var namespaceFailure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/", "uri://gbisd.edu/"]
        );
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/", "uri://gbisd.edu/"]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.NotAuthorized(namespaceFailure)
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Subject;
        failure.NamespaceFailure.Should().BeSameAs(namespaceFailure);
        failure
            .NamespaceFailure.ConfiguredNamespacePrefixes.Should()
            .Equal("uri://ed-fi.org/", "uri://gbisd.edu/");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_retries_and_returns_not_found_when_the_get_by_id_namespace_target_is_stale()
    {
        // The stored namespace check reports the target row vanished after the unlocked target lookup.
        // The read boundary must re-resolve the target rather than treat the missing row as a namespace
        // mismatch; the re-resolved lookup no longer finds the row, so the GET surfaces a 404.
        var documentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-bbbbbbbbbbbb"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .ReturnsNextFromSequence(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                ),
                new RelationalReadTargetLookupResult.NotFound()
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.StaleTarget()
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_namespace_uninitialized_403_when_stored_namespace_is_null()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-cccccccccccc"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var namespaceFailure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.NotAuthorized(namespaceFailure)
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
    }

    [Test]
    public async Task It_returns_a_no_prefixes_403_without_executing_namespace_authorization_when_client_has_no_prefixes()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        failure.NamespaceFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_when_no_usable_root_namespace_column()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-eeeeeeeeeeee"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Ed-Fi.School")
            .And.Contain("NamespaceBased")
            .And.Contain("no Namespace securable element resolves to a root table column");
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_when_mssql_namespace_prefix_cap_is_exceeded()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-ffffffffffff"));
        var pgsqlMappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var mappingSet = pgsqlMappingSet with
        {
            Key = pgsqlMappingSet.Key with { Dialect = SqlDialect.Mssql },
        };
        string[] tooManyPrefixes = [.. Enumerable.Range(0, 2000).Select(index => $"uri://prefix-{index}/")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: tooManyPrefixes
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2000 namespace prefixes")
            .And.Contain("exceeds the SQL Server limit");
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_no_prefixes_403_before_issuing_the_target_lookup_when_client_has_no_prefixes()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-a1a1a1a1a1a1"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
        );

        // A no-prefix client can never be authorized, so the §2.9 denial must not depend on whether
        // the document exists: even with the target absent the result is the 403, not a 404.
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

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
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
    public async Task It_returns_a_security_configuration_500_before_issuing_the_target_lookup_when_no_usable_root_namespace_column()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-b2b2b2b2b2b2"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
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

        result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>();
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
    public async Task It_returns_a_security_configuration_500_before_issuing_the_target_lookup_when_mssql_namespace_prefix_cap_is_exceeded()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-c3c3c3c3c3c3"));
        var pgsqlMappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var mappingSet = pgsqlMappingSet with
        {
            Key = pgsqlMappingSet.Key with { Dialect = SqlDialect.Mssql },
        };
        string[] tooManyPrefixes = [.. Enumerable.Range(0, 2000).Select(index => $"uri://prefix-{index}/")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: tooManyPrefixes
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

        result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>();
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
    public async Task It_runs_namespace_authorization_before_relationship_when_both_are_configured()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaaaa"));
        var mappingSet = CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "uri://ed-fi.org/Thing")
        );
        var order = 0;
        var namespaceOrder = 0;
        var relationshipOrder = 0;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => namespaceOrder = ++order)
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.Authorized()
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => relationshipOrder = ++order)
            .Returns(
                Task.FromResult<SingleRecordRelationshipAuthorizationExecutionResult>(
                    new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L)
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"id":"authorized"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        namespaceOrder.Should().Be(1);
        relationshipOrder.Should().Be(2);
    }

    [Test]
    public async Task It_does_not_run_relationship_authorization_when_namespace_authorization_denies()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-bbbbbbbbbbbb"));
        var mappingSet = CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo);
        var namespaceFailure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L],
            namespacePrefixes: ["uri://ed-fi.org/"]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.NotAuthorized(namespaceFailure)
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_fails_closed_when_ownership_is_configured_alongside_namespace()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-cccccccccccc"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );

        var result = await _sut.GetDocumentById(getRequest);

        var failure = result.Should().BeOfType<GetResult.GetFailureNotImplemented>().Subject;
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_retries_instead_of_materializing_when_namespace_is_authorized_but_hydration_observes_a_different_content_version()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("eeeeeeee-1111-2222-3333-aaaaaaaaaaaa"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        // The target lookup observes content version 5, but hydration returns a row that was mutated
        // to content version 6 after the namespace check authorized the stored value.
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 6L),
            (345L, "uri://ed-fi.org/Thing")
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    5L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _namespaceAuthorizationExecutor.ExecuteAsync(
                    A<NamespaceAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<NamespaceAuthorizationExecutionResult>(
                    new NamespaceAuthorizationExecutionResult.Authorized()
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().NotBeOfType<GetResult.GetSuccess>();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public void It_returns_all_discoverable_security_configuration_failures_when_relationship_planning_has_an_invalid_strategy_and_no_root_edorg_subject()
    {
        var mappingSet = CreateQuerySupportedMappingSetWithChildOnlyEdOrgSubject(_schoolResourceInfo);
        var planner = new RelationshipAuthorizationPlanner(CreateAuthorizationSubjectSelector());

        var result = planner.PlanStoredValues(
            mappingSet,
            new QualifiedResourceName("Ed-Fi", "School"),
            [
                new ConfiguredAuthorizationStrategy(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    0
                ),
                new ConfiguredAuthorizationStrategy("CustomAuthorizationStrategy", 1),
            ],
            new RelationalAuthorizationContext([255901L])
        );

        var failure = result
            .Should()
            .BeOfType<RelationshipAuthorizationResult.SecurityConfigurationError>()
            .Subject;
        failure.Failures.Should().HaveCount(2);
        failure
            .Failures.Select(static planningFailure => planningFailure.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy
            );
        failure
            .Failures[0]
            .ConfiguredStrategy!.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        failure.Failures[0].Location!.JsonPath.Should().Be("$.classPeriods[*].classPeriodReference.schoolId");
        failure.Failures[1].ConfiguredStrategy!.StrategyName.Should().Be("CustomAuthorizationStrategy");
    }

    [Test]
    public async Task It_bypasses_relationship_authorization_for_stored_document_get_requests()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-dddddddddddd"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            RelationalGetRequestReadMode.StoredDocument,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: []
        );
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
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
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"name":"Lincoln High"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_retries_get_hydration_when_authorized_content_version_changes_before_materialization()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-eeeeeeeeeeee"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        Queue<SingleRecordRelationshipAuthorizationExecutionResult> authorizationResults = new([
            new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(90L),
            new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L),
        ]);
        Queue<HydratedPage> hydratedPages = new([
            CreateHydratedPage(
                readPlan,
                CreateDocumentMetadataRow(documentUuid, 345L, 91L),
                (345L, "Updated Lincoln High")
            ),
            CreateHydratedPage(
                readPlan,
                CreateDocumentMetadataRow(documentUuid, 345L, 91L),
                (345L, "Updated Lincoln High")
            ),
        ]);

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    91L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => Task.FromResult(authorizationResults.Dequeue()));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => hydratedPages.Dequeue());
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Returns(JsonNode.Parse("""{"id":"stable"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_retries_get_hydration_when_the_post_hydration_content_version_guard_observes_a_change()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-2222-3333-4444-fefefefefefe"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var getRequest = CreateGetRequest(
            documentUuid,
            mappingSet,
            _schoolResourceInfo,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        Queue<RelationalReadTargetLookupResult> targetLookupResults = new([
            new RelationalReadTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                91L,
                _readTargetContentLastModifiedAt
            ),
            new RelationalReadTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                92L,
                _readTargetContentLastModifiedAt
            ),
            new RelationalReadTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                92L,
                _readTargetContentLastModifiedAt
            ),
            new RelationalReadTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                92L,
                _readTargetContentLastModifiedAt
            ),
        ]);
        Queue<SingleRecordRelationshipAuthorizationExecutionResult> authorizationResults = new([
            new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(91L),
            new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(92L),
        ]);
        Queue<HydratedPage> hydratedPages = new([
            CreateHydratedPage(
                readPlan,
                CreateDocumentMetadataRow(documentUuid, 345L, 91L),
                (345L, "Rows Read After Concurrent Update")
            ),
            CreateHydratedPage(
                readPlan,
                CreateDocumentMetadataRow(documentUuid, 345L, 92L),
                (345L, "Stable Lincoln High")
            ),
        ]);
        RelationalReadMaterializationRequest capturedReadRequest = null!;

        A.CallTo(() =>
                _readTargetLookupService.ResolveForGetByIdAsync(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "School"),
                    documentUuid,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => Task.FromResult(targetLookupResults.Dequeue()));
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => Task.FromResult(authorizationResults.Dequeue()));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => hydratedPages.Dequeue());
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call => capturedReadRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!)
            .Returns(JsonNode.Parse("""{"id":"stable"}""")!);

        var result = await _sut.GetDocumentById(getRequest);

        result.Should().BeOfType<GetResult.GetSuccess>();
        capturedReadRequest.DocumentMetadata.ContentVersion.Should().Be(92L);
        capturedReadRequest.TableRowsInDependencyOrder[0].Rows[0][1].Should().Be("Stable Lincoln High");
        A.CallTo(() =>
                _singleRecordRelationshipAuthorizationExecutor.ExecuteAsync(
                    A<SingleRecordRelationshipAuthorizationExecutionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_preserves_the_full_resource_etag_after_readable_profile_projection()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var mappingSet = CreateProfileProjectionOrderSensitiveMappingSet(_schoolResourceInfo);
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
            .Returns(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    93L,
                    _readTargetContentLastModifiedAt
                )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(345L),
                    A<HydrationExecutionOptions>._,
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
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be("\"93\"");
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
        var getRequest = CreateGetRequest(documentUuid, mappingSet, _schoolResourceInfo);

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
                    A<HydrationExecutionOptions>._,
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
        var getRequest = CreateGetRequest(documentUuid, mappingSet, _schoolResourceInfo);

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
                    A<HydrationExecutionOptions>._,
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
            _schoolResourceInfo
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

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
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
        capturedReadRequest.ReadMode.Should().Be(RelationalReadMaterializationMode.ExternalResponse);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_exposes_an_authorized_query_candidate_page_to_read_acceleration_before_hydration()
    {
        var firstDocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-eeeeeeeeeeee"));
        var secondDocumentUuid = new DocumentUuid(Guid.Parse("eeeeeeee-1111-2222-3333-ffffffffffff"));
        var firstContentLastModifiedAt = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        var secondContentLastModifiedAt = new DateTimeOffset(2026, 6, 2, 9, 5, 0, TimeSpan.Zero);
        var mappingSet = CreateQuerySupportedMappingSet(
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "name",
                "$.name",
                "string",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Name"))
            )
        );
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var expectedResourceKeyId = mappingSet.ResourceKeyIdByResource[resource];
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("name", "$.name", "Lincoln High", "string")],
            totalCount: false
        );
        var hydratedPage = new HydratedPage(
            null,
            [
                CreateDocumentMetadataRow(
                    firstDocumentUuid,
                    345L,
                    91L,
                    firstContentLastModifiedAt,
                    expectedResourceKeyId
                ),
                CreateDocumentMetadataRow(
                    secondDocumentUuid,
                    678L,
                    92L,
                    secondContentLastModifiedAt,
                    expectedResourceKeyId
                ),
            ],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, "Lincoln High"],
                        [678L, "Lincoln High Annex"],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.SelectedPage capturedSelectedPage = null!;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DocumentCacheReadAccelerationCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                    readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(
                                    ("DocumentId", 345L),
                                    ("DocumentUuid", firstDocumentUuid.Value),
                                    ("ContentVersion", 91L),
                                    ("ContentLastModifiedAt", firstContentLastModifiedAt)
                                ),
                                RelationalAccessTestData.CreateRow(
                                    ("DocumentId", 678L),
                                    ("DocumentUuid", secondDocumentUuid.Value),
                                    ("ContentVersion", 92L),
                                    ("ContentLastModifiedAt", secondContentLastModifiedAt)
                                )
                            ),
                        ]),
                        cancellationToken
                    )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedSelectedPage =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.SelectedPage
                    ?? throw new AssertionException(
                        "Cache-miss fallback should hydrate the already selected candidate page."
                    );
            })
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
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

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().HaveCount(2);
        success.TotalCount.Should().BeNull();
        success.HighestSelectedDocumentId.Should().BeNull();
        capturedSelectedPage.DocumentIds.Should().Equal(345L, 678L);
        readAccelerationCoordinator.QueryAttempts.Should().Be(1);
        readAccelerationCoordinator.SelectedQueryRequest.Should().NotBeNull();
        readAccelerationCoordinator
            .SelectedQueryRequest!.ResourceKind.Should()
            .Be(DocumentCacheReadAccelerationResourceKind.Resource);
        readAccelerationCoordinator
            .SelectedQueryCandidatePage!.AuthorizedCandidatePage.Should()
            .BeEquivalentTo(
                new DocumentCacheReadAccelerationCandidatePage(
                    [
                        new DocumentCacheReadAccelerationCandidate(
                            345L,
                            firstDocumentUuid,
                            expectedResourceKeyId,
                            91L,
                            firstContentLastModifiedAt
                        ),
                        new DocumentCacheReadAccelerationCandidate(
                            678L,
                            secondDocumentUuid,
                            expectedResourceKeyId,
                            92L,
                            secondContentLastModifiedAt
                        ),
                    ],
                    TotalCount: null,
                    HighestSelectedDocumentId: null,
                    IncludesTotalCount: false
                )
            );
    }

    [Test]
    public async Task It_wraps_a_provider_error_raised_by_custom_view_selected_page_fallback_hydration()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-aaaaaaaaaaaa"));
        var contentLastModifiedAt = new DateTimeOffset(2026, 6, 2, 10, 30, 0, TimeSpan.Zero);
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var databaseException = new StubDbException("custom view does not exist during fallback");
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DocumentCacheReadAccelerationCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                    readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(
                                    ("DocumentId", 345L),
                                    ("DocumentUuid", documentUuid.Value),
                                    ("ContentVersion", 91L),
                                    ("ContentLastModifiedAt", contentLastModifiedAt)
                                )
                            ),
                        ]),
                        cancellationToken
                    )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec.SelectedPage>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);

        var action = () => _sut.QueryDocuments(queryRequest);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
        readAccelerationCoordinator.QueryAttempts.Should().Be(1);
        readAccelerationCoordinator.SelectedQueryCandidatePage.Should().NotBeNull();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_propagates_query_preparation_cancellation_from_reference_resolution_for_relational_queries()
    {
        var mappingSet = CreateQuerySupportedMappingSet(
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
        );
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [
                CreateQueryElement(
                    "schoolCategoryDescriptor",
                    "$.schoolCategoryDescriptor",
                    "uri://ed-fi.org/SchoolCategoryDescriptor#High School",
                    "string"
                ),
            ],
            totalCount: true
        );
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken capturedCancellationToken = default;

        await cancellationTokenSource.CancelAsync();

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedCancellationToken = call.GetArgument<CancellationToken>(1))
            .ReturnsLazily(
                (ReferenceResolverRequest _, CancellationToken cancellationToken) =>
                    cancellationToken.IsCancellationRequested
                        ? Task.FromCanceled<ResolvedReferenceSet>(cancellationToken)
                        : Task.FromException<ResolvedReferenceSet>(
                            new AssertionException("Expected query preparation to pass the request token.")
                        )
            );

        Func<Task> action = () => _sut.QueryDocuments(queryRequest, cancellationTokenSource.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        capturedCancellationToken.Should().Be(cancellationTokenSource.Token);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_propagates_query_preparation_cancellation_from_custom_view_validation_for_cache_candidate_selection()
    {
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken capturedValidationToken = default;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);
        await cancellationTokenSource.CancelAsync();

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationToken = call.GetArgument<CancellationToken>(2))
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<IRelationalCommandReader, CancellationToken, Task<bool>> _,
                    CancellationToken cancellationToken
                ) =>
                    cancellationToken.IsCancellationRequested
                        ? Task.FromCanceled<bool>(cancellationToken)
                        : Task.FromException<bool>(
                            new AssertionException(
                                "Expected custom-view validation to pass the request token."
                            )
                        )
            );

        Func<Task> action = () => _sut.QueryDocuments(queryRequest, cancellationTokenSource.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        capturedValidationToken.Should().Be(cancellationTokenSource.Token);
        readAccelerationCoordinator.QueryAttempts.Should().Be(1);
        readAccelerationCoordinator.SelectedQueryRequest.Should().BeNull();
        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_uses_live_query_ordering_for_read_acceleration_candidate_selection_with_max_change_version()
    {
        var contentVersionSelectedDocumentUuid = new DocumentUuid(
            Guid.Parse("abababab-1111-2222-3333-cdcdcdcdcdcd")
        );
        var documentIdSelectedDocumentUuid = new DocumentUuid(
            Guid.Parse("cdcdcdcd-1111-2222-3333-abababababab")
        );
        var mappingSet = CreateChangeVersionQuerySupportedMappingSet(_schoolResourceInfo);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var resourceKeyId = mappingSet.ResourceKeyIdByResource[resource];
        var contentVersionSelectedMetadata = CreateDocumentMetadataRow(
            contentVersionSelectedDocumentUuid,
            300L,
            20L,
            resourceKeyId: resourceKeyId
        );
        var documentIdSelectedMetadata = CreateDocumentMetadataRow(
            documentIdSelectedDocumentUuid,
            200L,
            10L,
            resourceKeyId: resourceKeyId
        );
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: true,
            changeVersionRange: new ChangeVersionRange(5L, 30L),
            paginationParameters: new PaginationParameters(
                Limit: 1,
                Offset: 1,
                TotalCount: true,
                MaximumPageSize: 500
            )
        );
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        RelationalCommand candidateSelectionCommand = null!;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    ResourceReadPlan _,
                    PageKeysetSpec keyset,
                    HydrationExecutionOptions _,
                    CancellationToken _
                ) =>
                {
                    DocumentMetadataRow selectedMetadata =
                        keyset is PageKeysetSpec.SelectedPage selectedPage
                        && selectedPage.DocumentIds.Contains(documentIdSelectedMetadata.DocumentId)
                            ? documentIdSelectedMetadata
                            : contentVersionSelectedMetadata;

                    return CreateHydratedPage(
                        readPlan,
                        selectedMetadata,
                        (selectedMetadata.DocumentId, "Selected")
                    );
                }
            );
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .ReturnsLazily(
                (RelationalReadPageMaterializationRequest request) =>
                    request
                        .HydratedPage.DocumentMetadata.Select(metadata => new MaterializedDocument(
                            metadata,
                            JsonNode.Parse($$"""{"id":"{{metadata.DocumentUuid}}"}""")!
                        ))
                        .ToArray()
            );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => candidateSelectionCommand = call.GetArgument<RelationalCommand>(0)!)
            .ReturnsLazily(
                (
                    RelationalCommand command,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DocumentCacheReadAccelerationCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                {
                    DocumentMetadataRow selectedMetadata = command.CommandText.Contains(
                        "ORDER BY r.\"ContentVersion\" ASC",
                        StringComparison.Ordinal
                    )
                        ? contentVersionSelectedMetadata
                        : documentIdSelectedMetadata;

                    return readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(("TotalCount", 3L))
                            ),
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(
                                    ("DocumentId", selectedMetadata.DocumentId),
                                    ("DocumentUuid", selectedMetadata.DocumentUuid),
                                    ("ContentVersion", selectedMetadata.ContentVersion),
                                    ("ContentLastModifiedAt", selectedMetadata.ContentLastModifiedAt)
                                )
                            ),
                        ]),
                        cancellationToken
                    );
                }
            );

        var acceleratedResult = await _sut.QueryDocuments(queryRequest);

        candidateSelectionCommand.CommandText.Should().Contain("ORDER BY r.\"ContentVersion\" ASC");
        ParameterValue(candidateSelectionCommand, "@minChangeVersion").Should().Be(5L);
        ParameterValue(candidateSelectionCommand, "@maxChangeVersion").Should().Be(30L);
        ParameterValue(candidateSelectionCommand, "@offset").Should().Be(1L);
        ParameterValue(candidateSelectionCommand, "@limit").Should().Be(1L);

        var selectedCandidatePage =
            readAccelerationCoordinator.SelectedQueryCandidatePage
            ?? throw new AssertionException("Read acceleration should select an authorized query page.");
        DocumentCacheReadAccelerationCandidatePage authorizedCandidatePage =
            selectedCandidatePage.AuthorizedCandidatePage;

        authorizedCandidatePage
            .Candidates.Select(static candidate => candidate.DocumentId)
            .Should()
            .Equal(contentVersionSelectedMetadata.DocumentId);
        authorizedCandidatePage.IncludesTotalCount.Should().BeTrue();
        authorizedCandidatePage.TotalCount.Should().Be(3);

        var success = acceleratedResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);
        success.EdfiDocs.Single()!["id"]!
            .GetValue<string>()
            .Should()
            .Be(contentVersionSelectedDocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_reruns_the_no_cache_query_when_selected_page_fallback_metadata_drifts()
    {
        var selectedDocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-eeeeeeeeeeee"));
        var rerunDocumentUuid = new DocumentUuid(Guid.Parse("ffffffff-1111-2222-3333-eeeeeeeeeeee"));
        var selectedContentLastModifiedAt = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        var mappingSet = CreateQuerySupportedMappingSet(
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "name",
                "$.name",
                "string",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("Name"))
            )
        );
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var readPlan = mappingSet.ReadPlansByResource[resource];
        var resourceKeyId = mappingSet.ResourceKeyIdByResource[resource];
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("name", "$.name", "Lincoln High", "string")],
            totalCount: true
        );
        var selectedFallbackHydratedPage = new HydratedPage(
            null,
            [
                CreateDocumentMetadataRow(
                    selectedDocumentUuid,
                    345L,
                    92L,
                    selectedContentLastModifiedAt,
                    resourceKeyId
                ),
            ],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, "Drifted selected row"],
                    ]
                ),
            ],
            []
        );
        var rerunHydratedPage = new HydratedPage(
            9,
            [CreateDocumentMetadataRow(rerunDocumentUuid, 999L, 105L, resourceKeyId: resourceKeyId)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [999L, "No-cache rerun row"],
                    ]
                ),
            ],
            []
        );
        List<PageKeysetSpec> hydratedKeysets = [];
        RelationalReadPageMaterializationRequest materializationRequest = null!;

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DocumentCacheReadAccelerationCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                    readAsync(
                        new InMemoryRelationalCommandReader([
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(("TotalCount", 7L))
                            ),
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(
                                    ("DocumentId", 345L),
                                    ("DocumentUuid", selectedDocumentUuid.Value),
                                    ("ContentVersion", 91L),
                                    ("ContentLastModifiedAt", selectedContentLastModifiedAt)
                                )
                            ),
                        ]),
                        cancellationToken
                    )
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => hydratedKeysets.Add(call.GetArgument<PageKeysetSpec>(1)!))
            .ReturnsLazily(
                (
                    ResourceReadPlan _,
                    PageKeysetSpec keyset,
                    HydrationExecutionOptions _,
                    CancellationToken _
                ) => keyset is PageKeysetSpec.SelectedPage ? selectedFallbackHydratedPage : rerunHydratedPage
            );
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Invokes(call =>
                materializationRequest = call.GetArgument<RelationalReadPageMaterializationRequest>(0)!
            )
            .Returns([
                new MaterializedDocument(
                    rerunHydratedPage.DocumentMetadata[0],
                    JsonNode.Parse($$"""{"id":"{{rerunDocumentUuid.Value}}"}""")!
                ),
            ]);

        var result = await _sut.QueryDocuments(queryRequest);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(9);
        success.EdfiDocs.Should().ContainSingle();
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(rerunDocumentUuid.Value.ToString());
        hydratedKeysets.Should().HaveCount(2);
        hydratedKeysets[0].Should().BeOfType<PageKeysetSpec.SelectedPage>();
        hydratedKeysets[1].Should().BeOfType<PageKeysetSpec.Query>();
        materializationRequest.HydratedPage.Should().BeSameAs(rerunHydratedPage);
    }

    [Test]
    public async Task It_routes_descriptor_queries_through_the_descriptor_read_handler_based_on_mapping_set_metadata()
    {
        var descriptorReadHandler = A.Fake<IDescriptorReadHandler>();
        var descriptorResourceInfo = CreateResourceInfo("SchoolTypeDescriptor");
        var mappingSet = CreateDescriptorOnlyMappingSet(descriptorResourceInfo);
        QueryElement[] queryElements =
        [
            CreateQueryElement("namespace", "$.namespace", "uri://ed-fi.org", "string"),
        ];
        var projectionContext = new ReadableProfileProjectionContext(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("description")],
                [],
                [],
                []
            ),
            new HashSet<string> { "id", "namespace", "codeValue", "_etag", "_lastModifiedDate" }
        );
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators =
        [
            CreateAuthorizationStrategyEvaluator(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
            ),
        ];
        var expectedResult = new QueryResult.QuerySuccess([], 0);
        DescriptorQueryRequest capturedRequest = null!;

        A.CallTo(() =>
                descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorQueryRequest>(0)!)
            .Returns(expectedResult);

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var queryRequest = CreateQueryRequest(
            mappingSet,
            queryElements,
            totalCount: true,
            authorizationStrategyEvaluators: authorizationStrategyEvaluators,
            readableProfileProjectionContext: projectionContext,
            resourceInfo: descriptorResourceInfo
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeSameAs(expectedResult);
        capturedRequest.MappingSet.Should().BeSameAs(mappingSet);
        capturedRequest.Resource.Should().Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
        capturedRequest.QueryElements.Should().BeSameAs(queryElements);
        capturedRequest
            .PaginationParameters.Should()
            .Be(((CollectionPaging.Traditional)queryRequest.Paging).Parameters);
        capturedRequest.AuthorizationStrategyEvaluators.Should().BeSameAs(authorizationStrategyEvaluators);
        capturedRequest.ReadableProfileProjectionContext.Should().BeSameAs(projectionContext);
        capturedRequest.TraceId.Value.Should().Be("query-trace");
        A.CallTo(() =>
                descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
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
    public async Task It_declines_a_cursor_paged_query_until_cursor_page_selection_is_implemented()
    {
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.Paging)
            .Returns(new CollectionPaging.Cursor(new CursorRange(10, 2509), new PageSize(100)));

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Be("Cursor paging is not yet supported for relational queries.");
    }

    [Test]
    public async Task It_declines_a_cursor_paged_query_before_resolving_any_read_dependency()
    {
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.Paging)
            .Returns(new CollectionPaging.Cursor(CursorRange.From(1), new PageSize(25)));

        await _sut.QueryDocuments(queryRequest);

        A.CallTo(() =>
                _descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_propagates_descriptor_query_not_implemented_failures_from_the_descriptor_read_handler()
    {
        var descriptorReadHandler = A.Fake<IDescriptorReadHandler>();
        var descriptorResourceInfo = CreateResourceInfo("SchoolTypeDescriptor");
        var mappingSet = CreateDescriptorOnlyMappingSet(descriptorResourceInfo);
        var expectedResult = new QueryResult.QueryFailureNotImplemented(
            "Descriptor query capability for resource 'Ed-Fi.SchoolTypeDescriptor' was intentionally omitted: "
                + "descriptor query support was intentionally omitted for the test fixture."
        );

        A.CallTo(() =>
                descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .Returns(expectedResult);

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
            ],
            resourceInfo: descriptorResourceInfo
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeSameAs(expectedResult);
        A.CallTo(() =>
                descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
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
    public async Task It_delegates_descriptor_query_authorization_that_requires_filtering_to_the_descriptor_read_handler()
    {
        var descriptorReadHandler = A.Fake<IDescriptorReadHandler>();
        var descriptorResourceInfo = CreateResourceInfo("SchoolTypeDescriptor");
        var mappingSet = CreateDescriptorOnlyMappingSet(descriptorResourceInfo);
        var expectedResult = new QueryResult.QueryFailureNotImplemented(
            "Relational descriptor query authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET-many authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or with 'NamespaceBased' and/or 'NoFurtherAuthorizationRequired' are currently supported."
        );

        A.CallTo(() =>
                descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .Returns(expectedResult);

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            new ThrowingDescriptorWriteHandler(),
            descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            resourceInfo: descriptorResourceInfo
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeSameAs(expectedResult);
        A.CallTo(() =>
                descriptorReadHandler.HandleQueryAsync(A<DescriptorQueryRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
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

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
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

    [Test]
    public async Task It_carries_the_hydrated_selected_keyset_boundary_into_the_query_success()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("beefbeef-1111-2222-3333-cdcdcdcdcdcd"));
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
        var queryRequest = CreateQueryRequest(mappingSet, [], totalCount: false);
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
        ) with
        {
            HighestSelectedDocumentId = 2509L,
        };

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([
                new MaterializedDocument(
                    hydratedPage.DocumentMetadata[0],
                    JsonNode.Parse($$"""{"id":"{{documentUuid.Value}}"}""")!
                ),
            ]);

        var result = await _sut.QueryDocuments(queryRequest);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().Be(2509L);
        success.EdfiDocs.Should().ContainSingle();
    }

    // The boundary comes from the selected keyset, not the hydrated body, so it must survive a page
    // whose every selected row was deleted before hydration completed. A body-derived boundary would
    // stop a cursor walk here instead of advancing past the deleted rows.
    [Test]
    public async Task It_carries_the_hydrated_boundary_into_the_query_success_with_an_empty_body()
    {
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
        var queryRequest = CreateQueryRequest(mappingSet, [], totalCount: false);
        var hydratedPage = new HydratedPage(null, [], [new HydratedTableRows(readPlan.Model.Root, [])], [])
        {
            HighestSelectedDocumentId = 2509L,
        };

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([]);

        var result = await _sut.QueryDocuments(queryRequest);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().Be(2509L);
        success.EdfiDocs.Should().BeEmpty();
    }

    [Test]
    public async Task It_leaves_the_query_success_boundary_null_when_hydration_reports_none()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("beefbeef-4444-5555-6666-cdcdcdcdcdcd"));
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
        var queryRequest = CreateQueryRequest(mappingSet, [], totalCount: false);
        var hydratedPage = CreateHydratedPage(
            readPlan,
            CreateDocumentMetadataRow(documentUuid, 345L, 91L),
            (345L, "Lincoln High")
        );

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(hydratedPage);
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([
                new MaterializedDocument(
                    hydratedPage.DocumentMetadata[0],
                    JsonNode.Parse($$"""{"id":"{{documentUuid.Value}}"}""")!
                ),
            ]);

        var result = await _sut.QueryDocuments(queryRequest);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.HighestSelectedDocumentId.Should().BeNull();
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
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_applies_readable_profile_projection_to_each_query_result_without_recomputing_etags()
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

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
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
        success.EdfiDocs[0]!["_etag"]!.GetValue<string>().Should().Be("\"91\"");
        success.EdfiDocs[1]!["_etag"]!.GetValue<string>().Should().Be("\"92\"");
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
                    A<HydrationExecutionOptions>._,
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
                    A<HydrationExecutionOptions>._,
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
    public async Task It_executes_supported_query_authorization_with_root_edorg_filtering()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("91919191-1111-2222-3333-444444444444"));
        var mappingSet = CreateQuerySupportedMappingSet(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "schoolId",
                "$.localEducationAgencyId",
                "number",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("LocalEducationAgencyId"))
            )
        );
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("schoolId", "$.localEducationAgencyId", "255901", "number")],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [200L, 100L, 100L]
        );
        var hydratedPage = new HydratedPage(
            7,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Authorized relational query execution should hydrate through PageKeysetSpec.Query."
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
        var success = (QueryResult.QuerySuccess)result;
        success.TotalCount.Should().Be(7);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(documentUuid.Value.ToString());
        capturedKeyset.ParameterValues["schoolId"].Should().Be(255901L);
        capturedKeyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(100L, 200L);
        capturedKeyset
            .Plan.PageParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(
                "schoolId",
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds,
                "offset",
                "limit"
            );
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("LocalEducationAgencyId");
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain(
                "r.\"LocalEducationAgencyId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"LocalEducationAgencyId\" IN (SELECT"
            );
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("TargetEducationOrganizationId");
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("SourceEducationOrganizationId")
            .And.Contain("@ClaimEducationOrganizationIds");
        capturedKeyset.Plan.TotalCountSql.Should().NotBeNull();
        capturedKeyset
            .Plan.TotalCountSql.Should()
            .Contain(
                "r.\"LocalEducationAgencyId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"LocalEducationAgencyId\" IN (SELECT"
            );
        capturedKeyset.Plan.TotalCountSql.Should().Contain("@ClaimEducationOrganizationIds");
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_executes_supported_inverted_query_authorization_with_root_edorg_filtering()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("92929292-1111-2222-3333-555555555555"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted
                ),
            ],
            claimEducationOrganizationIds: [300L]
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Authorized inverted relational query execution should hydrate through PageKeysetSpec.Query."
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
        capturedKeyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(300L);
        capturedKeyset
            .Plan.PageParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds,
                "offset",
                "limit"
            );
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("LocalEducationAgencyId");
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain(
                "r.\"LocalEducationAgencyId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"LocalEducationAgencyId\" IN (SELECT"
            );
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("SourceEducationOrganizationId");
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("TargetEducationOrganizationId")
            .And.Contain("@ClaimEducationOrganizationIds");
        capturedKeyset.Plan.TotalCountSql.Should().BeNull();
    }

    [Test]
    public async Task It_preserves_duplicate_supported_query_authorization_strategies_as_distinct_or_branches()
    {
        static int CountOrdinalOccurrences(string value, string text) =>
            value.Split(text, StringSplitOptions.None).Length - 1;

        var documentUuid = new DocumentUuid(Guid.Parse("93939393-1111-2222-3333-555555555555"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [300L]
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Duplicate supported EdOrg strategies should hydrate through PageKeysetSpec.Query."
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
        var pageDocumentIdSql = capturedKeyset.Plan.PageDocumentIdSql;

        CountOrdinalOccurrences(
                pageDocumentIdSql,
                "\"auth\".\"EducationOrganizationIdToEducationOrganizationId\""
            )
            .Should()
            .Be(2);
        CountOrdinalOccurrences(pageDocumentIdSql, "r.\"LocalEducationAgencyId\" IN (SELECT").Should().Be(2);
        CountOrdinalOccurrences(
                pageDocumentIdSql,
                "r.\"LocalEducationAgencyId\" = ANY(@ClaimEducationOrganizationIds)"
            )
            .Should()
            .Be(2);
        pageDocumentIdSql.Should().Contain(" OR ");
    }

    [Test]
    public async Task It_deduplicates_duplicate_physical_root_edorg_subjects_before_compiling_query_authorization()
    {
        static int CountOrdinalOccurrences(string value, string text) =>
            value.Split(text, StringSplitOptions.None).Length - 1;

        var resourceInfo = CreateResourceInfo("TestResource");
        var documentUuid = new DocumentUuid(Guid.Parse("72727272-1111-2222-3333-444444444444"));
        var mappingSet = CreateQuerySupportedMappingSetWithDuplicatePhysicalRootEdOrgSubjects(resourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "TestResource")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [300L],
            resourceInfo: resourceInfo
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Duplicate physical EdOrg subjects should hydrate through PageKeysetSpec.Query."
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
        var pageDocumentIdSql = capturedKeyset.Plan.PageDocumentIdSql;

        CountOrdinalOccurrences(
                pageDocumentIdSql,
                "\"auth\".\"EducationOrganizationIdToEducationOrganizationId\""
            )
            .Should()
            .Be(1);
        CountOrdinalOccurrences(pageDocumentIdSql, "r.\"SchoolId\" IN (SELECT").Should().Be(1);
        CountOrdinalOccurrences(pageDocumentIdSql, "r.\"SchoolId\" = ANY(@ClaimEducationOrganizationIds)")
            .Should()
            .Be(1);
        pageDocumentIdSql
            .Should()
            .Contain("WHERE t0.\"SourceEducationOrganizationId\" = ANY(@ClaimEducationOrganizationIds)");
    }

    [TestCase(
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        "TargetEducationOrganizationId",
        "SourceEducationOrganizationId"
    )]
    [TestCase(
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
        "SourceEducationOrganizationId",
        "TargetEducationOrganizationId"
    )]
    public async Task It_requires_authorization_on_both_same_name_root_edorg_subjects_for_course_offering_queries(
        string authorizationStrategyName,
        string expectedSubjectFragment,
        string expectedClaimFilterFragment
    )
    {
        var resourceInfo = CreateResourceInfo("CourseOffering");
        var documentUuid = new DocumentUuid(Guid.Parse("83838383-1111-2222-3333-666666666666"));
        var mappingSet = CreateQuerySupportedMappingSetWithSameNameRootEdOrgSubjects(resourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "CourseOffering")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(authorizationStrategyName),
            ],
            claimEducationOrganizationIds: [300L],
            resourceInfo: resourceInfo
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L, 255902L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Same-name root EdOrg authorization should hydrate through PageKeysetSpec.Query."
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
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain(" AND ");
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"CourseOffering_SchoolReferenceSchoolId\" = ANY(@ClaimEducationOrganizationIds) OR ")
            .And.Contain(
                $"r.\"CourseOffering_SchoolReferenceSchoolId\" IN (SELECT t0.\"{expectedSubjectFragment}\""
            )
            .And.Contain($"WHERE t0.\"{expectedClaimFilterFragment}\" = ANY(@ClaimEducationOrganizationIds)");
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain(
                "r.\"CourseOffering_SessionReferenceSchoolId\" = ANY(@ClaimEducationOrganizationIds) OR "
            )
            .And.Contain(
                $"r.\"CourseOffering_SessionReferenceSchoolId\" IN (SELECT t1.\"{expectedSubjectFragment}\""
            )
            .And.Contain($"WHERE t1.\"{expectedClaimFilterFragment}\" = ANY(@ClaimEducationOrganizationIds)");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_short_circuits_supported_query_authorization_with_empty_edorg_claims(bool totalCount)
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: totalCount,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: []
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], totalCount ? 0 : null));
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_validates_supported_query_authorization_subjects_before_short_circuiting_empty_edorg_claims(
        bool totalCount
    )
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithChildOnlyEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: totalCount,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: []
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        result.As<QueryResult.QueryFailureSecurityConfiguration>().Errors.Should().ContainSingle();
        result
            .As<QueryResult.QueryFailureSecurityConfiguration>()
            .Errors[0]
            .Should()
            .Contain("$.classPeriods[*].classPeriodReference.schoolId");
        result.As<QueryResult.QueryFailureSecurityConfiguration>().Errors[0].Should().Contain("ClassPeriod");
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_applies_readable_profile_projection_to_authorized_query_results()
    {
        var firstDocumentUuid = new DocumentUuid(Guid.Parse("17171717-1111-2222-3333-444444444444"));
        var secondDocumentUuid = new DocumentUuid(Guid.Parse("18181818-1111-2222-3333-555555555555"));
        var mappingSet = CreateQuerySupportedMappingSet(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "schoolId",
                "$.localEducationAgencyId",
                "number",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("LocalEducationAgencyId"))
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
            [CreateQueryElement("schoolId", "$.localEducationAgencyId", "255901", "number")],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            readableProfileProjectionContext: projectionContext,
            claimEducationOrganizationIds: [500L]
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
                        [345L, 255901L],
                        [678L, 255902L],
                    ]
                ),
            ],
            []
        );
        var materializedFirst = JsonNode.Parse(
            """
            {
              "id": "17171717-1111-2222-3333-444444444444",
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
              "id": "18181818-1111-2222-3333-555555555555",
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
              "id": "17171717-1111-2222-3333-444444444444",
              "_etag": "\"authorized-projected-etag-1\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High"
            }
            """
        )!;
        var projectedSecond = JsonNode.Parse(
            """
            {
              "id": "18181818-1111-2222-3333-555555555555",
              "_etag": "\"authorized-projected-etag-2\"",
              "_lastModifiedDate": "2026-04-11T17:30:45Z",
              "schoolId": 255902,
              "nameOfInstitution": "Roosevelt High"
            }
            """
        )!;
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Authorized readable-profile query execution should hydrate through PageKeysetSpec.Query."
                    );
            })
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
                        "17171717-1111-2222-3333-444444444444" => projectedFirst,
                        "18181818-1111-2222-3333-555555555555" => projectedSecond,
                        _ => throw new AssertionException(
                            "Unexpected authorized readable profile projection request."
                        ),
                    }
            );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        var success = (QueryResult.QuerySuccess)result;
        success.TotalCount.Should().BeNull();
        success.EdfiDocs.Should().HaveCount(2);
        success.EdfiDocs[0].Should().BeSameAs(projectedFirst);
        success.EdfiDocs[1].Should().BeSameAs(projectedSecond);
        success.EdfiDocs[0]!["_etag"]!.GetValue<string>().Should().Be("\"authorized-projected-etag-1\"");
        success.EdfiDocs[1]!["_etag"]!.GetValue<string>().Should().Be("\"authorized-projected-etag-2\"");
        capturedKeyset.ParameterValues["schoolId"].Should().Be(255901L);
        capturedKeyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(500L);
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
    }

    [Test]
    public async Task It_returns_security_configuration_failure_when_supported_query_authorization_has_only_child_table_edorg_subjects()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithChildOnlyEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [500L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        result.As<QueryResult.QueryFailureSecurityConfiguration>().Errors.Should().ContainSingle();
        result
            .As<QueryResult.QueryFailureSecurityConfiguration>()
            .Errors[0]
            .Should()
            .Contain("$.classPeriods[*].classPeriodReference.schoolId");
        result.As<QueryResult.QueryFailureSecurityConfiguration>().Errors[0].Should().Contain("ClassPeriod");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_relationship_security_configuration_terminals()
    {
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithChildOnlyEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [500L]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        capturedValidationSql
            .Should()
            .ContainSingle(sql => sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_fails_closed_when_a_custom_view_is_composed_with_OwnershipBased()
    {
        // OwnershipBased belongs to DMS-1060. Even alongside a resolved custom view and a relationship
        // strategy, GET-many must fail closed rather than emit an ownership filter: nothing stamps
        // CreatedByOwnershipTokenId yet, so a filter would silently drop every row.
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_executing_ordinary_query_authorization()
    {
        List<string> capturedValidationSql = [];
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Custom-view query execution should hydrate through PageKeysetSpec.Query."
                    );
            })
            .Returns(new HydratedPage(null, [], [], []));
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([]);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("SchoolWithCustomAuthorization");
        capturedValidationSql
            .Should()
            .ContainSingle(sql => sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_ands_two_resolving_custom_views_and_validates_them_in_one_round_trip()
    {
        // Two custom views configured at indexes 0 and 1: both are AND filters, so both predicates must
        // reach the page SQL, and both auth views must be validated by a single batched command rather than
        // one round trip per view.
        List<string> capturedValidationSql = [];
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator("SchoolWithSecondCustomAuthorization"),
            ],
            claimEducationOrganizationIds: [255901L]
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Custom-view query execution should hydrate through PageKeysetSpec.Query."
                    );
            })
            .Returns(new HydratedPage(null, [], [], []));
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([]);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("SchoolWithCustomAuthorization")
            .And.Contain("SchoolWithSecondCustomAuthorization");
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("SchoolWithSecondCustomAuthorization", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_fails_closed_for_ownership_only_queries()
    {
        // OwnershipBased belongs to DMS-1060, so GET-many keeps the known-but-not-enabled 501 rather
        // than silently returning an empty page.
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(_schoolResourceInfo),
            [],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_a_custom_view_configured_before_the_ownership_terminal()
    {
        // Custom view first: it executes ahead of the OwnershipBased 501, so a missing or non-conforming
        // auth view must surface its own error rather than being masked by the terminal.
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureNotImplemented>();
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_a_custom_view_configured_after_the_ownership_terminal()
    {
        // The inverse configured order of the sibling above, with the same outcome: OwnershipBased executes
        // last per auth.md "Execution order" no matter where the CMS placed it, so the custom view is still
        // validated ahead of the 501.
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_the_security_configuration_terminal_when_ownership_precedes_an_invalid_strategy()
    {
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_executes_people_query_relationship_authorization_through_page_keyset_planning()
    {
        var resourceInfo = _studentResourceInfo;
        var documentUuid = new DocumentUuid(Guid.Parse("94949494-1111-2222-3333-444444444444"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(resourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "Student")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L],
            resourceInfo: resourceInfo
        );
        var hydratedPage = new HydratedPage(
            1,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "People relationship query authorization should hydrate through PageKeysetSpec.Query."
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
        var success = (QueryResult.QuerySuccess)result;
        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().ContainSingle();
        capturedKeyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(255901L);
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"DocumentId\" IN (SELECT")
            .And.Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentId\"")
            .And.Contain("\"Student_DocumentId\"");
        capturedKeyset
            .Plan.TotalCountSql.Should()
            .Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentId\"");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_ignores_no_further_authorization_required_when_combined_with_people_query_relationship_authorization()
    {
        var resourceInfo = _studentResourceInfo;
        var documentUuid = new DocumentUuid(Guid.Parse("96969696-1111-2222-3333-444444444444"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(resourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "Student")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L],
            resourceInfo: resourceInfo
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "People relationship query authorization should still plan when NoFurtherAuthorizationRequired is present."
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

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().BeNull();
        success.EdfiDocs.Should().ContainSingle();
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentId\"")
            .And.Contain("\"Student_DocumentId\"")
            .And.NotContain(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_validates_custom_views_before_short_circuiting_relationship_queries_with_empty_edorg_claims(
        bool totalCount
    )
    {
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: totalCount,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ],
            claimEducationOrganizationIds: []
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], totalCount ? 0 : null));
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_short_circuits_supported_people_query_authorization_with_empty_edorg_claims(
        bool totalCount
    )
    {
        var resourceInfo = _studentResourceInfo;
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(resourceInfo),
            [],
            totalCount: totalCount,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: [],
            resourceInfo: resourceInfo
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], totalCount ? 0 : null));
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_validates_people_auth_views_before_short_circuiting_empty_edorg_claims(
        bool totalCount
    )
    {
        var resourceInfo = _studentResourceInfo;
        var queryRequest = CreateQueryRequest(
            CreateAuthorizationAwareMappingSetWithSelfStudentSubject(resourceInfo),
            [],
            totalCount: totalCount,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: [],
            resourceInfo: resourceInfo
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure
            .Errors[0]
            .Should()
            .Contain("people auth views were not emitted")
            .And.Contain("auth.EducationOrganizationIdToStudentDocumentId")
            .And.Contain(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_combines_edorg_only_and_people_query_relationship_strategies_as_or_branches()
    {
        var resourceInfo = _studentResourceInfo;
        var documentUuid = new DocumentUuid(Guid.Parse("95959595-1111-2222-3333-444444444444"));
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(resourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "Student")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L],
            resourceInfo: resourceInfo
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Mixed relationship query authorization should hydrate through PageKeysetSpec.Query."
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
        capturedKeyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("\"SchoolId_Unified\"")
            .And.Contain("\"auth\".\"EducationOrganizationIdToEducationOrganizationId\"")
            .And.Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentId\"")
            .And.Contain(" OR ");
        capturedKeyset.Plan.TotalCountSql.Should().BeNull();
    }

    [TestCase("CustomAuthorizationStrategy")]
    [TestCase("UnknownBasisWithStudent")]
    public async Task It_returns_query_security_configuration_failure_for_invalid_metadata_mixed_with_people_relationship_authorization(
        string invalidStrategyName
    )
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
                ),
                CreateAuthorizationStrategyEvaluator(invalidStrategyName),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        result.As<QueryResult.QueryFailureSecurityConfiguration>().Errors.Should().ContainSingle();
        result
            .As<QueryResult.QueryFailureSecurityConfiguration>()
            .Errors[0]
            .Should()
            .Be(SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([invalidStrategyName]));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_not_implemented_when_OwnershipBased_is_mixed_with_people_relationship_authorization()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
                ),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    [Test]
    public async Task It_allows_a_custom_view_strategy_when_mixed_with_people_relationship_authorization()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople
                ),
                CreateAuthorizationStrategyEvaluator("SchoolWithResponsibility"),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
    }

    [Test]
    public async Task It_returns_security_configuration_when_custom_view_basis_resource_has_no_join_path()
    {
        var resourceInfo = _schoolResourceInfo;
        var mappingSet = CreateQuerySupportedMappingSetWithCustomViewBasisButNoJoinPath(resourceInfo);
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators: [CreateAuthorizationStrategyEvaluator("StudentWithNoJoinPath")],
            resourceInfo: resourceInfo
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("StudentWithNoJoinPath");
        failure.Errors[0].Should().Contain("auth.StudentWithNoJoinPath");
        failure.Errors[0].Should().Contain("No DocumentId join path");
        failure.Errors[0].Should().EndWith("Should a different authorization strategy be used?");
        failure.Diagnostics.Should().ContainSingle();
        failure
            .Diagnostics![0]
            .ProviderOrPlannerFailureKind.Should()
            .Be("RelationshipAuthorization.NoCustomViewJoinPath");
        failure.Diagnostics![0].ConfiguredStrategyNames!.Should().Equal("StudentWithNoJoinPath");
        failure.Diagnostics![0].ConfiguredStrategyIndexes!.Should().Equal(0);
        failure.Diagnostics![0].TargetResourceFullName!.Should().Be("Ed-Fi.School");
        Fake.GetCalls(_commandExecutor).Should().BeEmpty();
    }

    [Test]
    public async Task It_validates_an_earlier_custom_view_before_a_later_no_join_path_failure()
    {
        // SchoolWithCustomAuthorization (index 0) plans; StudentWithNoJoinPath (index 1) does not. The
        // earlier auth view executes first, so it must still be probed — otherwise a missing earlier view
        // is masked by the later planning failure — and the reported terminal stays the no-join-path 500.
        List<string> capturedValidationSql = [];
        var resourceInfo = _schoolResourceInfo;
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithCustomViewBasisButNoJoinPath(resourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator("StudentWithNoJoinPath"),
            ],
            resourceInfo: resourceInfo
        );
        CaptureCustomViewValidationSql(capturedValidationSql);

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("No DocumentId join path");
        failure.Errors[0].Should().EndWith("Should a different authorization strategy be used?");
        failure.Errors[0].Should().Contain("StudentWithNoJoinPath");
        capturedValidationSql
            .Should()
            .ContainSingle(sql => sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal));
        capturedValidationSql
            .Should()
            .NotContain(sql => sql.Contains("StudentWithNoJoinPath", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_does_not_validate_a_later_custom_view_after_an_earlier_no_join_path_failure()
    {
        // Reverse configured order: the failure is at index 0, so the custom view at index 1 executes after
        // the terminal and must not be probed at all.
        List<string> capturedValidationSql = [];
        var resourceInfo = _schoolResourceInfo;
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithCustomViewBasisButNoJoinPath(resourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("StudentWithNoJoinPath"),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ],
            resourceInfo: resourceInfo
        );
        CaptureCustomViewValidationSql(capturedValidationSql);

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors[0].Should().Contain("No DocumentId join path");
        failure.Errors[0].Should().EndWith("Should a different authorization strategy be used?");
        capturedValidationSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_validate_a_later_custom_view_after_an_earlier_classifier_failure()
    {
        // CustomAuthorizationStrategy (index 0) is an unrecognized strategy, so the classifier fails there.
        // The resolved custom view at index 1 executes after that terminal, so probing it would let a
        // missing or non-conforming auth view mask the earlier unknown-strategy 500.
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );
        CaptureCustomViewValidationSql(capturedValidationSql);

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Contain(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
                    "CustomAuthorizationStrategy",
                ])
            );
        capturedValidationSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_validates_an_earlier_custom_view_before_a_later_classifier_failure()
    {
        // The mirror case: the resolved custom view is configured first, so it is probed before the
        // classifier's unknown-strategy terminal at index 1 is reported.
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]
        );
        CaptureCustomViewValidationSql(capturedValidationSql);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        capturedValidationSql
            .Should()
            .ContainSingle(sql => sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal));
    }

    private void CaptureCustomViewValidationSql(List<string> capturedValidationSql)
    {
        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));
    }

    [Test]
    public void It_orders_custom_view_before_namespace_no_prefixes_when_custom_view_has_lower_configured_index()
    {
        var customViewStrategies = new[]
        {
            new SupportedCustomViewAuthorizationStrategy(
                new ConfiguredAuthorizationStrategy("StudentWithCustomViewProviderTest", 0),
                AuthorizationLocalOrder: 0,
                BasisResource: new QualifiedResourceName("Ed-Fi", "Student")
            ),
        };

        var strategiesBeforeTerminal = CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
            customViewStrategies,
            terminalRawConfiguredIndex: 1
        );

        strategiesBeforeTerminal.Should().Equal(customViewStrategies);
    }

    [Test]
    public void It_orders_namespace_no_prefixes_before_custom_view_when_namespace_has_lower_configured_index()
    {
        var customViewStrategies = new[]
        {
            new SupportedCustomViewAuthorizationStrategy(
                new ConfiguredAuthorizationStrategy("StudentWithCustomViewProviderTest", 1),
                AuthorizationLocalOrder: 0,
                BasisResource: new QualifiedResourceName("Ed-Fi", "Student")
            ),
        };

        var strategiesBeforeTerminal = CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
            customViewStrategies,
            terminalRawConfiguredIndex: 0
        );

        strategiesBeforeTerminal.Should().BeEmpty();
    }

    [Test]
    public async Task It_formats_people_unresolved_security_configuration_failures_with_people_wording()
    {
        const string studentPath = "$.studentReference.studentUniqueId";
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithUnresolvedStudentSubject(_schoolResourceInfo, studentPath),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().HaveCount(2);
        var peopleError = failure.Errors.Single(error =>
            error.Contains(studentPath, StringComparison.Ordinal)
        );
        peopleError
            .Should()
            .Contain("Student securable elements")
            .And.Contain("DocumentId-based relational path")
            .And.Contain("auth.EducationOrganizationIdToStudentDocumentId")
            .And.NotContain("EducationOrganization securable elements");
    }

    [Test]
    public async Task It_formats_people_child_collection_security_configuration_failures_with_skipped_metadata()
    {
        const string studentPath = "$.studentReferences[*].studentReference.studentUniqueId";
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithChildOnlyStudentSubject(_schoolResourceInfo, studentPath),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ],
            claimEducationOrganizationIds: [255901L]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().HaveCount(2);
        var peopleError = failure.Errors.Single(error =>
            error.Contains(studentPath, StringComparison.Ordinal)
        );
        peopleError
            .Should()
            .Contain("Student relationship authorization subject")
            .And.Contain("ChildCollectionPersonPathOutsideSubjectScope")
            .And.Contain("StudentUniqueId")
            .And.Contain("auth.EducationOrganizationIdToStudentDocumentId")
            .And.NotContain("concrete root-table EducationOrganization authorization subject");
    }

    [Test]
    public async Task It_formats_people_missing_proposed_binding_security_configuration_failures_with_people_wording()
    {
        const string studentPath = "$.studentReference.studentUniqueId";
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithUnboundStudentSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Missing People binding"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901L]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().HaveCount(2);
        var peopleError = failure.Errors.Single(error =>
            error.Contains(studentPath, StringComparison.Ordinal)
        );
        peopleError
            .Should()
            .Contain("proposed-value Student relationship authorization subject")
            .And.Contain("auth.EducationOrganizationIdToStudentDocumentId")
            .And.Contain("anchor column")
            .And.NotContain("EducationOrganization subject");
        _capturedExecutorRequests.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_security_configuration_failure_when_query_authorization_includes_an_invalid_strategy_after_OwnershipBased()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        // Two independent configuration problems are reported: OwnershipBased is known but not enabled,
        // and CustomAuthorizationStrategy is not a recognized strategy at all.
        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().HaveCount(2);
        failure
            .Errors.Should()
            .ContainSingle(error => error.Contains("OwnershipBased", StringComparison.Ordinal));
        failure
            .Errors.Should()
            .Contain(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
                    "CustomAuthorizationStrategy",
                ])
            );
        failure.Diagnostics.Should().NotBeNull().And.HaveCount(2);
        failure
            .Diagnostics!.SelectMany(static diagnostic => diagnostic.ConfiguredStrategyNames ?? [])
            .Should()
            .BeEquivalentTo(AuthorizationStrategyNameConstants.OwnershipBased, "CustomAuthorizationStrategy");
        failure
            .Diagnostics!.SelectMany(static diagnostic => diagnostic.ConfiguredStrategyIndexes ?? [])
            .Order()
            .Should()
            .Equal(0, 1);
        failure
            .Diagnostics!.Select(static diagnostic => diagnostic.ProviderOrPlannerFailureKind)
            .Should()
            .Contain(
                $"RelationshipAuthorization.{RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy}"
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_namespace_no_prefixes_403_for_query_without_executing_a_db_query()
    {
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        failure.NamespaceFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_returning_namespace_no_prefixes_for_queries()
    {
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: []
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        failure.NamespaceFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_only_custom_views_before_namespace_no_prefixes_for_queries()
    {
        List<string> capturedValidationSql = [];
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator("SchoolWithLaterCustomAuthorization"),
            ],
            namespacePrefixes: []
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>();
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && !sql.Contains("SchoolWithLaterCustomAuthorization", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_returns_namespace_no_prefixes_without_custom_view_validation_when_namespace_is_configured_first()
    {
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ],
            namespacePrefixes: []
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>();
        Fake.GetCalls(_commandExecutor)
            .Where(call => call.Method.Name == nameof(IRelationalCommandExecutor.ExecuteReaderAsync))
            .Select(call => ((RelationalCommand)call.Arguments[0]!).CommandText)
            .Should()
            .NotContain(sql => sql.Contains("AND 1 = 0", StringComparison.Ordinal));
    }

    [TestCase("CustomAuthorizationStrategy")]
    [TestCase("MissingBasisWithCustomAuthorization")]
    public async Task It_returns_namespace_no_prefixes_for_queries_when_the_failing_strategy_is_configured_after_namespace(
        string failingStrategyName
    )
    {
        // NamespaceBased is configured first, so its no-prefixes 403 is the terminal even though a
        // later strategy is a security-configuration failure (unrecognized, or a custom-view name
        // whose basis resource does not resolve).
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(failingStrategyName),
            ],
            namespacePrefixes: []
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        failure.NamespaceFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        Fake.GetCalls(_commandExecutor)
            .Where(call => call.Method.Name == nameof(IRelationalCommandExecutor.ExecuteReaderAsync))
            .Should()
            .BeEmpty();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_empty_page_preprocessing_success_for_queries()
    {
        List<string> capturedValidationSql = [];
        var mappingSet = CreateQuerySupportedMappingSet(
            _schoolResourceInfo,
            CreateSupportedQueryField("id", "$.id", "string", new RelationalQueryFieldTarget.DocumentUuid())
        );
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_empty_page_planning_success_for_queries()
    {
        List<string> capturedValidationSql = [];
        var mappingSet = CreateQuerySupportedMappingSet(
            CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo),
            _schoolResourceInfo,
            CreateSupportedQueryField(
                "schoolId",
                "$.localEducationAgencyId",
                "number",
                new RelationalQueryFieldTarget.RootColumn(new DbColumnName("LocalEducationAgencyId"))
            )
        );
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("schoolId", "$.localEducationAgencyId", "1.5", "number")],
            totalCount: true,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_executing_the_ordinary_query_page_and_hydration()
    {
        // Validation is unconditional: it is what turns a missing or non-conforming custom view into
        // the controlled system-error response, so it runs on the ordinary page path too, not only
        // ahead of short-circuit terminals.
        List<string> capturedValidationSql = [];
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => capturedValidationSql.Add(call.GetArgument<RelationalCommand>(0)!.CommandText))
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new HydratedPage(null, [], [], []));
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .Returns([]);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        capturedValidationSql
            .Should()
            .ContainSingle(sql =>
                sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal)
                && sql.Contains("DocumentId", StringComparison.Ordinal)
                && sql.Contains("LIMIT 0", StringComparison.Ordinal)
                && !sql.Contains("LIMIT 1", StringComparison.Ordinal)
            );
        // Validation runs on the read executor — a separate read connection and round trip — so a
        // GET-many never opens a write transaction just to probe the configured auth views.
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_wraps_a_provider_error_raised_by_the_custom_view_page_query()
    {
        // Validation and the page query are separate round trips against the same views, so a view that
        // is dropped, revoked, or broken in between raises only at execution. That failure must keep the
        // custom-view validation contract instead of escaping as an unhandled provider error.
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var databaseException = new StubDbException("custom view does not exist");
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]
        );

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(true));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);

        var action = () => _sut.QueryDocuments(queryRequest);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
    }

    [Test]
    public async Task It_returns_a_namespace_security_configuration_500_for_query_when_no_usable_root_column()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Ed-Fi.School")
            .And.Contain("NamespaceBased")
            .And.Contain("no Namespace securable element resolves to a root table column");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_namespace_no_usable_root_column_500_for_queries_when_the_failing_strategy_is_configured_after_namespace()
    {
        // NamespaceBased is configured first, so its no-usable-root-column terminal is reported even
        // though a later custom-view strategy has an unresolvable basis resource.
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator("MissingBasisWithCustomAuthorization"),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Ed-Fi.School")
            .And.Contain("NamespaceBased")
            .And.Contain("no Namespace securable element resolves to a root table column");
        Fake.GetCalls(_commandExecutor)
            .Where(call => call.Method.Name == nameof(IRelationalCommandExecutor.ExecuteReaderAsync))
            .Should()
            .BeEmpty();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_fails_closed_for_query_when_ownership_is_configured_alongside_namespace()
    {
        // The namespace prefixes are authorized, so no namespace terminal precedes Ownership; the
        // unsupported Ownership term is what fails the request closed.
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNotImplemented>()
            .Which.FailureMessage.Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_namespace_terminal_when_it_precedes_ownership()
    {
        // A namespace terminal configured ahead of Ownership still wins: the Ownership 501 must not
        // displace the no-prefixes 403.
        var queryRequest = CreateQueryRequest(
            CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ],
            namespacePrefixes: []
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_applies_the_namespace_filter_to_the_page_query_when_namespace_is_authorized()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("a1a1a1a1-1111-2222-3333-444444444444"));
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, "uri://ed-fi.org/School"],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Namespace-authorized relational query execution should hydrate through PageKeysetSpec.Query."
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
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("\"Namespace\"");
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("IS NOT NULL");
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("LIKE ANY(@namespacePrefixes)");
        capturedKeyset
            .ParameterValues["namespacePrefixes"]
            .Should()
            .BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should()
            .Equal("uri://ed-fi.org/%");
        capturedKeyset
            .Plan.PageParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Contain("namespacePrefixes");
    }

    [Test]
    public async Task It_and_composes_the_namespace_filter_with_the_relationship_or_group_for_query()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("a2a2a2a2-1111-2222-3333-555555555555"));
        var mappingSet = CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo);
        var readPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: [255901L],
            namespacePrefixes: ["uri://ed-fi.org/"]
        );
        var hydratedPage = new HydratedPage(
            null,
            [CreateDocumentMetadataRow(documentUuid, 345L, 91L)],
            [
                new HydratedTableRows(
                    readPlan.Model.Root,
                    [
                        [345L, 255901L, "uri://ed-fi.org/School"],
                    ]
                ),
            ],
            []
        );
        PageKeysetSpec.Query capturedKeyset = null!;

        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    readPlan,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
            {
                capturedKeyset =
                    call.GetArgument<PageKeysetSpec>(1) as PageKeysetSpec.Query
                    ?? throw new AssertionException(
                        "Mixed-strategy relational query execution should hydrate through PageKeysetSpec.Query."
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
        // Namespace AND-filter and relationship OR group are both present, with their own parameters.
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("LIKE ANY(@namespacePrefixes)");
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("@ClaimEducationOrganizationIds");
        capturedKeyset.Plan.PageDocumentIdSql.Should().Contain("LocalEducationAgencyId");
        capturedKeyset
            .ParameterValues["namespacePrefixes"]
            .Should()
            .BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should()
            .Equal("uri://ed-fi.org/%");
        capturedKeyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(255901L);
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_when_combined_mssql_namespace_and_relationship_parameters_exceed_the_limit()
    {
        var pgsqlMappingSet = CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo);
        var mappingSet = pgsqlMappingSet with
        {
            Key = pgsqlMappingSet.Key with { Dialect = SqlDialect.Mssql },
        };
        // Each list stays below its own 2,000 SQL Server cap, but together they exceed the per-command
        // parameter budget, so the request must fail closed instead of failing at execution.
        string[] namespacePrefixes = [.. Enumerable.Range(0, 1500).Select(index => $"uri://prefix-{index}/")];
        long[] claimEducationOrganizationIds =
        [
            .. Enumerable.Range(0, 1500).Select(index => 100000L + index),
        ];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: claimEducationOrganizationIds,
            namespacePrefixes: namespacePrefixes
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("1500 namespace prefixes")
            .And.Contain("1500 authorization education organization ids")
            .And.Contain("exceed the SQL Server parameter limit");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_custom_views_before_the_mssql_relationship_parameter_budget_terminal()
    {
        var baseMappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var mappingSet = baseMappingSet with { Key = baseMappingSet.Key with { Dialect = SqlDialect.Mssql } };
        long[] claimEducationOrganizationIds =
        [
            .. Enumerable.Range(0, 2096).Select(index => 100000L + index),
        ];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: claimEducationOrganizationIds
        );
        var databaseException = new StubDbException("custom view does not exist");

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<Func<IRelationalCommandReader, CancellationToken, Task<bool>>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(databaseException);

        var action = () => _sut.QueryDocuments(queryRequest);

        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();

        assertion.Which.InnerException.Should().BeSameAs(databaseException);
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_an_empty_page_rather_than_a_parameter_budget_failure_when_preprocessing_short_circuits()
    {
        // A filter that resolves to no matches (an invalid UUID) short-circuits to an empty page during
        // preprocessing. The SQL Server parameter-budget guard must run after that short-circuit, so the
        // empty page wins over a security-configuration failure for a command that never executes — even
        // though the composed authorization lists would otherwise exceed the per-command parameter ceiling.
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var baseMappingSet = CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo);
        var mappingSet = baseMappingSet with
        {
            Key = baseMappingSet.Key with { Dialect = SqlDialect.Mssql },
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = CreateSupportedQueryField(
                            "id",
                            "$.id",
                            "string",
                            new RelationalQueryFieldTarget.DocumentUuid()
                        ),
                    },
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
        string[] namespacePrefixes = [.. Enumerable.Range(0, 1500).Select(index => $"uri://prefix-{index}/")];
        long[] claimEducationOrganizationIds =
        [
            .. Enumerable.Range(0, 1500).Select(index => 100000L + index),
        ];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: claimEducationOrganizationIds,
            namespacePrefixes: namespacePrefixes
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], null));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_an_empty_page_rather_than_a_parameter_budget_failure_when_planning_short_circuits()
    {
        // A scalar root-column filter whose value cannot convert to the column type (1.5 for an integer
        // column) short-circuits to an empty page during planning, not preprocessing. The SQL Server
        // parameter-budget guard runs off the final planned parameter count, after planning, so this empty
        // page also wins over a security-configuration failure for a command that never executes — even
        // though the composed authorization lists would otherwise exceed the per-command parameter ceiling.
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var baseMappingSet = CreateNamespaceAndRootEdOrgMappingSet(_schoolResourceInfo);
        var mappingSet = baseMappingSet with
        {
            Key = baseMappingSet.Key with { Dialect = SqlDialect.Mssql },
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["schoolId"] = CreateSupportedQueryField(
                            "schoolId",
                            "$.localEducationAgencyId",
                            "number",
                            new RelationalQueryFieldTarget.RootColumn(
                                new DbColumnName("LocalEducationAgencyId")
                            )
                        ),
                    },
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
        string[] namespacePrefixes = [.. Enumerable.Range(0, 1500).Select(index => $"uri://prefix-{index}/")];
        long[] claimEducationOrganizationIds =
        [
            .. Enumerable.Range(0, 1500).Select(index => 100000L + index),
        ];
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [CreateQueryElement("schoolId", "$.localEducationAgencyId", "1.5", "number")],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ],
            claimEducationOrganizationIds: claimEducationOrganizationIds,
            namespacePrefixes: namespacePrefixes
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], null));
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_security_configuration_failure_when_query_authorization_strategy_metadata_is_invalid()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        result.As<QueryResult.QueryFailureSecurityConfiguration>().Errors.Should().ContainSingle();
        result
            .As<QueryResult.QueryFailureSecurityConfiguration>()
            .Errors[0]
            .Should()
            .Be(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
                    "CustomAuthorizationStrategy",
                ])
            );
        result
            .As<QueryResult.QueryFailureSecurityConfiguration>()
            .Errors[0]
            .Should()
            .NotContain("{BasisResource}With...");
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_aggregates_unknown_query_authorization_strategy_names_in_the_canonical_security_configuration_message()
    {
        var queryRequest = CreateQueryRequest(
            CreateQuerySupportedMappingSet(_schoolResourceInfo),
            [],
            totalCount: false,
            authorizationStrategyEvaluators:
            [
                CreateAuthorizationStrategyEvaluator("ZCustomAuthorizationStrategy"),
                CreateAuthorizationStrategyEvaluator("ACustomAuthorizationStrategy"),
                CreateAuthorizationStrategyEvaluator("ZCustomAuthorizationStrategy"),
            ]
        );

        var result = await _sut.QueryDocuments(queryRequest);

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "Could not find authorization strategy implementations for the following strategy names: 'ZCustomAuthorizationStrategy', 'ACustomAuthorizationStrategy'."
            );
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
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

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_short_circuits_invalid_id_queries_to_an_empty_page_without_hydration(bool totalCount)
    {
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
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
            totalCount: totalCount
        );

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], totalCount ? 0 : null));
        readAccelerationCoordinator.QueryAttempts.Should().Be(1);
        readAccelerationCoordinator.SelectedQueryRequest.Should().BeNull();
        readAccelerationCoordinator.SelectedQueryCandidatePage.Should().BeNull();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.MaterializePage(A<RelationalReadPageMaterializationRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .MustNotHaveHappened();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_returns_zero_size_query_pages_before_exposing_cache_candidate_pages(bool totalCount)
    {
        var readAccelerationCoordinator = new RecordingReadAccelerationCoordinator();
        var mappingSet = CreateQuerySupportedMappingSet(_schoolResourceInfo);
        var queryRequest = CreateQueryRequest(
            mappingSet,
            [],
            totalCount,
            paginationParameters: new PaginationParameters(
                Limit: 0,
                Offset: 0,
                TotalCount: totalCount,
                MaximumPageSize: 500
            )
        );

        UseReadAccelerationCoordinator(readAccelerationCoordinator);

        A.CallTo(() =>
                _commandExecutor.ExecuteReaderAsync(
                    A<RelationalCommand>._,
                    A<
                        Func<
                            IRelationalCommandReader,
                            CancellationToken,
                            Task<DocumentCacheReadAccelerationCandidatePage>
                        >
                    >._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    RelationalCommand _,
                    Func<
                        IRelationalCommandReader,
                        CancellationToken,
                        Task<DocumentCacheReadAccelerationCandidatePage>
                    > readAsync,
                    CancellationToken cancellationToken
                ) =>
                    readAsync(
                        new InMemoryRelationalCommandReader(
                            totalCount
                                ?
                                [
                                    InMemoryRelationalResultSet.Create(
                                        RelationalAccessTestData.CreateRow(("TotalCount", 7L))
                                    ),
                                    InMemoryRelationalResultSet.Create(),
                                ]
                                : [InMemoryRelationalResultSet.Create()]
                        ),
                        cancellationToken
                    )
            );

        var result = await _sut.QueryDocuments(queryRequest);

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], totalCount ? 7 : null));
        readAccelerationCoordinator.QueryAttempts.Should().Be(1);
        readAccelerationCoordinator.SelectedQueryRequest.Should().BeNull();
        readAccelerationCoordinator.SelectedQueryCandidatePage.Should().BeNull();
        A.CallTo(() =>
                _documentHydrator.HydrateAsync(
                    A<ResourceReadPlan>._,
                    A<PageKeysetSpec>._,
                    A<HydrationExecutionOptions>._,
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
                    A<HydrationExecutionOptions>._,
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
        var committedEtag = ComposedWriteResultEtag;
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

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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
        var upsertRequest = A.Fake<IUpsertRequest>();
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
    }

    [Test]
    public async Task It_routes_post_as_update_requests_through_the_executor_with_a_read_plan()
    {
        var committedEtag = ComposedWriteResultEtag;
        var traceId = new TraceId("post-update-trace");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Post As Update High");
        requestBody["_etag"] = "\"stale-request-etag\"";
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var documentInfo = CreateDocumentInfo();

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpdateSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
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
        _capturedExecutorRequest.ExistingDocumentReadPlan.Should().BeSameAs(expectedReadPlan);
        _capturedExecutorRequest.SelectedBody.Should().BeSameAs(requestBody);
        _capturedExecutorRequest.TraceId.Should().Be(traceId);
    }

    [Test]
    public async Task It_routes_put_requests_through_the_executor_with_reference_resolution_inputs()
    {
        var committedEtag = ComposedWriteResultEtag;
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

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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
        var updateRequest = A.Fake<IUpdateRequest>();
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
    }

    [Test]
    public async Task It_returns_post_not_implemented_for_known_but_not_enabled_relationship_authorization_before_target_lookup()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Roosevelt High");
        var documentInfo = CreateDocumentInfo();
        var mappingSet = CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var writePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"");

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("post-auth-deferred"));
        A.CallTo(() => upsertRequest.WritePrecondition).Returns(writePrecondition);
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureNotImplemented>().Subject;
        failure.Reason.Should().Be(UpsertFailureNotImplementedReason.StrategyNotEnabled);
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        AssertSupportedRelationshipStrategyNames(failure.FailureMessage);
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_plans_a_custom_view_alongside_relationship_authorization_for_a_post()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Roosevelt High");
        var documentInfo = CreateDocumentInfo();
        var mappingSet = CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo);

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("post-custom-auth"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        await _sut.UpsertDocument(upsertRequest);

        // The custom view is an AND filter that composes with the relationship OR group rather than
        // disabling the write, so both are planned and handed to the executor.
        var customViewAuthorization = _capturedExecutorRequest.CustomViewAuthorization;
        customViewAuthorization.Should().NotBeNull();
        customViewAuthorization!
            .Checks.Should()
            .OnlyContain(check => check.ConfiguredStrategy.StrategyName == "SchoolWithCustomAuthorization");
        customViewAuthorization.StoredChecks.Should().ContainSingle();
        customViewAuthorization.ProposedChecks.Should().ContainSingle();
        // The relationship strategy is planned as usual: the custom view is excluded from that bucket, so the
        // relationship planner cannot downgrade it back to unsupported.
        _capturedExecutorRequest.PostRelationshipAuthorizationPlans.Should().NotBeNull();
    }

    [Test]
    public async Task It_validates_a_post_custom_view_before_a_relationship_security_configuration_terminal()
    {
        // The unknown relationship strategy makes this a 500, but the custom view AND-composes ahead of the
        // relationship bucket, so it executes — and is validated — before that terminal is reported.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post relationship 500"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>();
        capturedValidationSql
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("SchoolWithCustomAuthorization");
    }

    [Test]
    public async Task It_defers_a_put_no_claims_denial_into_the_proposed_slot_when_a_custom_view_is_pending()
    {
        // A custom view AND-composes before the relationship OR group, and its proposed checks run in the
        // second command. Keeping NoClaims in the stored slot ends the write at the first phase, so those
        // proposed checks never execute; the denial has to wait in the proposed slot instead.
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa09"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Put no claims with custom view"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        await _sut.UpdateDocumentById(updateRequest);

        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest
            .ProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.NoClaims>();
        var customViewAuthorization = _capturedExecutorRequest.CustomViewAuthorization;
        customViewAuthorization.Should().NotBeNull();
        customViewAuthorization!.ProposedChecks.Should().NotBeEmpty();
    }

    [Test]
    public async Task It_validates_a_post_custom_view_when_an_unknown_strategy_stops_before_planning()
    {
        // An unknown strategy makes the strategy-level outcome SecurityConfigurationError, the sibling entry
        // arm to StillUnsupported. It enters the bucket with nothing planned either, so it owes the same.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post unknown strategy"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>();
        capturedValidationSql
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("SchoolWithCustomAuthorization");
    }

    [Test]
    public async Task It_validates_a_post_custom_view_when_an_unsupported_strategy_stops_before_planning()
    {
        // OwnershipBased makes the strategy-level outcome StillUnsupported, which enters the relationship
        // bucket before any custom view has been planned. The view still AND-composes ahead of that 501, so
        // the bucket has to plan and validate it rather than reporting the terminal over an unchecked view.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post unsupported strategy"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeOfType<UpsertResult.UpsertFailureNotImplemented>();
        capturedValidationSql
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("SchoolWithCustomAuthorization");
    }

    [Test]
    public async Task It_validates_a_put_custom_view_when_an_unsupported_strategy_stops_before_planning()
    {
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Put unsupported strategy"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotImplemented>();
        capturedValidationSql
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("SchoolWithCustomAuthorization");
    }

    [Test]
    public async Task It_validates_a_post_custom_view_configured_before_a_no_usable_root_column_terminal()
    {
        // The namespace no-usable-root-column 500 resolves in preflight, but a custom view configured ahead of
        // it executes first, so it is probed before that terminal is reported.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post no usable root"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no Namespace securable element resolves to a root table column");
        capturedValidationSql
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("SchoolWithCustomAuthorization");
    }

    [Test]
    public async Task It_validates_a_put_custom_view_configured_before_a_no_usable_root_column_terminal()
    {
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Put no usable root"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("no Namespace securable element resolves to a root table column");
        capturedValidationSql
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("SchoolWithCustomAuthorization");
    }

    [Test]
    public async Task It_validates_an_earlier_post_custom_view_before_a_later_no_join_path_failure()
    {
        // SchoolWithCustomAuthorization plans; StudentWithNoJoinPath does not. The earlier view executes
        // first, so it is probed even though the later one cannot plan, and only it is probed.
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAwareMappingSetWithCustomViewBasisButNoJoinPath(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post no join path"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator("StudentWithNoJoinPath"),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("No DocumentId join path");
        failure.Errors[0].Should().Contain("StudentWithNoJoinPath");
        capturedValidationSql
            .Should()
            .ContainSingle(sql => sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal));
        capturedValidationSql
            .Should()
            .NotContain(sql => sql.Contains("StudentWithNoJoinPath", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_validates_an_earlier_put_custom_view_before_a_later_no_join_path_failure()
    {
        var capturedValidationSql = GivenCustomViewValidationIsRecorded();
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAwareMappingSetWithCustomViewBasisButNoJoinPath(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Put no join path"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator("SchoolWithCustomAuthorization"),
                CreateAuthorizationStrategyEvaluator("StudentWithNoJoinPath"),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("No DocumentId join path");
        failure.Errors[0].Should().Contain("StudentWithNoJoinPath");
        capturedValidationSql
            .Should()
            .ContainSingle(sql => sql.Contains("SchoolWithCustomAuthorization", StringComparison.Ordinal));
        capturedValidationSql
            .Should()
            .NotContain(sql => sql.Contains("StudentWithNoJoinPath", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_returns_post_security_configuration_failure_before_known_but_not_enabled_result()
    {
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Security config"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().Contain(error => error.Contains("Relational POST authorization metadata"));
        failure.Errors.Should().Contain(error => error.Contains("CustomAuthorizationStrategy"));
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_post_relationship_not_authorized_for_empty_edorg_claims_before_target_lookup()
    {
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("No claims"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        var failedStrategy = failure.RelationshipFailure.FailedStrategies.Should().ContainSingle().Which;
        failedStrategy.ConfiguredStrategyIndex.Should().Be(0);
        failedStrategy.RelationshipLocalOrder.Should().Be(0);
        failedStrategy
            .StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        failedStrategy
            .StrategyKind.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);
        failedStrategy.AuthObject.Should().NotBeNull();
        failedStrategy.AuthObject!.Name.Should().Be("auth.EducationOrganizationIdToEducationOrganizationId");
        failedStrategy
            .Hint.Should()
            .Be("Relationship authorization requires at least one claim EducationOrganizationId.");
        var failedSubject = failedStrategy.FailedSubjects.Should().ContainSingle().Which;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.RootBinding.ColumnName.Should().Be("SchoolId");
        failedSubject
            .Hint.Should()
            .Be("Relationship authorization requires at least one claim EducationOrganizationId.");
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_plans_both_custom_view_value_sources_for_a_post()
    {
        // A POST resolves to create or upsert-as-update only in-session, so both sources are planned up front
        // and the executor applies the stored ones only when a target was captured.
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa01"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );

        await _sut.UpsertDocument(CreateCustomViewUpsertRequest(documentUuid));

        var customViewAuthorization = _capturedExecutorRequest.CustomViewAuthorization;
        customViewAuthorization.Should().NotBeNull();
        customViewAuthorization!.StoredChecks.Select(check => check.Index).Should().Equal(0);
        // The proposed slice starts above zero, which is what keeps the two sources' cv1 payloads distinct.
        customViewAuthorization.ProposedChecks.Select(check => check.Index).Should().Equal(1);
        customViewAuthorization
            .Checks.Should()
            .OnlyContain(check => check.ConfiguredStrategy.StrategyName == CustomViewStrategyName);
    }

    [Test]
    public async Task It_plans_a_self_basis_proposed_check_carrying_both_execution_branches_for_a_post()
    {
        // School is its own basis here, so SQL cannot decide the proposed check: the target's DocumentId does
        // not exist yet on a create and is immutable on an update. The planner records that rather than
        // guessing which branch applies.
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa02"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );

        await _sut.UpsertDocument(CreateCustomViewUpsertRequest(documentUuid));

        _capturedExecutorRequest
            .CustomViewAuthorization!.ProposedChecks.Should()
            .ContainSingle()
            .Which.CheckTarget.Should()
            .BeOfType<CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable>();
    }

    [Test]
    public async Task It_plans_both_custom_view_value_sources_for_a_put()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa03"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );

        await _sut.UpdateDocumentById(CreateCustomViewUpdateRequest(documentUuid));

        var customViewAuthorization = _capturedExecutorRequest.CustomViewAuthorization;
        customViewAuthorization.Should().NotBeNull();
        customViewAuthorization!.StoredChecks.Select(check => check.Index).Should().Equal(0);
        customViewAuthorization.ProposedChecks.Select(check => check.Index).Should().Equal(1);
    }

    [Test]
    public async Task It_plans_no_custom_view_authorization_for_a_write_without_a_configured_custom_view()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa04"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );

        await _sut.UpdateDocumentById(
            CreateCustomViewUpdateRequest(
                documentUuid,
                strategyNames: [AuthorizationStrategyNameConstants.NamespaceBased]
            )
        );

        _capturedExecutorRequest.CustomViewAuthorization.Should().BeNull();
    }

    [Test]
    public async Task It_surfaces_a_custom_view_denial_the_write_executor_reports_for_a_put()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa05"));
        var customViewFailure = CreateCustomViewFailure();
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureCustomViewNotAuthorized(customViewFailure)
            )
        );

        var result = await _sut.UpdateDocumentById(CreateCustomViewUpdateRequest(documentUuid));

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure.Should()
            .BeSameAs(customViewFailure);
    }

    [Test]
    public async Task It_defers_a_post_relationship_no_claims_denial_when_a_custom_view_is_still_pending()
    {
        // A custom view is an AND filter that composes ahead of the relationship OR group, so it has to run
        // before this denial is reported. Short-circuiting here would skip it entirely: there is no
        // NamespaceBased strategy to keep the request going to the executor.
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa06"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );

        var upsertRequest = CreateCustomViewUpsertRequest(
            documentUuid,
            strategyNames:
            [
                CustomViewStrategyName,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            ],
            namespacePrefixes: [],
            claimEducationOrganizationIds: []
        );

        await _sut.UpsertDocument(upsertRequest);

        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.CustomViewAuthorization.Should().NotBeNull();
        _capturedExecutorRequest
            .ProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.NoClaims>();
    }

    [Test]
    public async Task It_carries_the_custom_view_plan_through_the_post_create_new_relationship_branch()
    {
        // With claims present the existing-resource plan authorizes, which routes through the create-new
        // proposed-value planning. Every result that method defers to the executor has to carry the plan, or
        // the checks are dropped on the way.
        var documentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-aaaaaaaaaa07"));
        GivenWriteExecutorCaptures(
            new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(documentUuid, ComposedWriteResultEtag)
            )
        );

        var upsertRequest = CreateCustomViewUpsertRequest(
            documentUuid,
            strategyNames:
            [
                CustomViewStrategyName,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            ],
            claimEducationOrganizationIds: [255901L]
        );

        await _sut.UpsertDocument(upsertRequest);

        _capturedExecutorRequest.PostRelationshipAuthorizationPlans.Should().NotBeNull();
        var customViewAuthorization = _capturedExecutorRequest.CustomViewAuthorization;
        customViewAuthorization.Should().NotBeNull();
        customViewAuthorization!.StoredChecks.Should().ContainSingle();
        customViewAuthorization.ProposedChecks.Should().ContainSingle();
    }

    private void GivenWriteExecutorCaptures(RelationalWriteExecutorResult result) =>
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(Task.FromResult(result));

    private static IUpsertRequest CreateCustomViewUpsertRequest(
        DocumentUuid documentUuid,
        string[]? strategyNames = null,
        string[]? namespacePrefixes = null,
        long[]? claimEducationOrganizationIds = null
    )
    {
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateNamespaceAndRelationshipWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Custom View"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                .. (strategyNames ?? [CustomViewStrategyName]).Select(CreateAuthorizationStrategyEvaluator),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(
                new RelationalAuthorizationContext(
                    claimEducationOrganizationIds ?? [],
                    namespacePrefixes ?? ["uri://ed-fi.org/"]
                )
            );

        return upsertRequest;
    }

    private static IUpdateRequest CreateCustomViewUpdateRequest(
        DocumentUuid documentUuid,
        string[]? strategyNames = null
    )
    {
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateNamespaceAndRelationshipWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Custom View"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                .. (strategyNames ?? [CustomViewStrategyName]).Select(CreateAuthorizationStrategyEvaluator),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        return updateRequest;
    }

    [Test]
    public async Task It_forwards_relationship_no_claims_with_proposed_namespace_authorization_to_the_write_executor_for_a_mixed_strategy_post()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, ComposedWriteResultEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateNamespaceAndRelationshipWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Mixed"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        // Empty claim EducationOrganizationIds make the relationship strategy resolve to NoClaims; the
        // configured namespace prefixes make a proposed namespace check participate. With both planned,
        // preflight must defer the NoClaims denial to the executor instead of short-circuiting, so the
        // namespace check (AND-composed ahead of the relationship OR-group) gets to deny first.
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        await _sut.UpsertDocument(upsertRequest);

        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest
            .ProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.NoClaims>();
        var proposedNamespaceAuthorization = _capturedExecutorRequest.ProposedNamespaceAuthorization;
        proposedNamespaceAuthorization.Should().NotBeNull();
        proposedNamespaceAuthorization!
            .Checks.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(NamespaceAuthorizationCheckValueSource.Proposed);
    }

    [Test]
    public async Task It_forwards_relationship_no_claims_with_proposed_namespace_authorization_to_the_write_executor_for_a_mixed_strategy_put()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateSuccess(documentUuid, ComposedWriteResultEtag)
                    )
                )
            );

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateNamespaceAndRelationshipWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Mixed"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        // Empty claim EducationOrganizationIds make the relationship strategy resolve to NoClaims; the
        // configured namespace prefixes make the stored and proposed namespace checks participate. PUT
        // must defer the stored relationship NoClaims into the proposed-relationship slot so the
        // namespace checks (AND-composed ahead of the relationship OR-group) get to deny first — routing
        // NoClaims through the stored slot would surface the relationship denial before the proposed
        // namespace check runs.
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        await _sut.UpdateDocumentById(updateRequest);

        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest
            .ProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.NoClaims>();
        _capturedExecutorRequest.StoredNamespaceAuthorization.Should().NotBeNull();
        _capturedExecutorRequest.ProposedNamespaceAuthorization.Should().NotBeNull();
    }

    [Test]
    public async Task It_forwards_authorized_post_relationship_plans_to_the_write_executor_after_target_lookup()
    {
        var committedEtag = ComposedWriteResultEtag;
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Authorized High"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, committedEtag));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().BeNull();
        var postPlans = _capturedExecutorRequest.PostRelationshipAuthorizationPlans;
        postPlans.Should().NotBeNull();
        postPlans!
            .ExistingResourcePlan.StoredValues.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Stored);
        postPlans
            .ExistingResourcePlan.ProposedValues.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Proposed);
        var createNewProposedAuthorization = postPlans
            .CreateNewProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Subject;
        createNewProposedAuthorization
            .CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Proposed);
    }

    [Test]
    public async Task It_forwards_authorized_post_as_update_relationship_plans_to_the_write_executor_after_target_lookup()
    {
        var committedEtag = ComposedWriteResultEtag;
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpdateSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Authorized Existing High"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, committedEtag));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().BeNull();
        var postPlans = _capturedExecutorRequest.PostRelationshipAuthorizationPlans;
        postPlans.Should().NotBeNull();
        postPlans!
            .ExistingResourcePlan.StoredValues.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Stored);
        postPlans
            .ExistingResourcePlan.ProposedValues.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Proposed);
        var createNewProposedAuthorization = postPlans
            .CreateNewProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Subject;
        createNewProposedAuthorization
            .CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Proposed);
    }

    [Test]
    public async Task It_forwards_authorized_people_post_relationship_plans_to_the_write_executor_after_target_lookup()
    {
        var committedEtag = ComposedWriteResultEtag;
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithDirectStudentSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc)
            .Returns(
                JsonNode.Parse(
                    """{"studentReference":{"studentUniqueId":"604822"},"name":"Authorized People"}"""
                )!
            );
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901L]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var peopleInsertSuccess = result.Should().BeOfType<UpsertResult.InsertSuccess>().Subject;
        peopleInsertSuccess.NewDocumentUuid.Should().Be(documentUuid);
        peopleInsertSuccess.ETag.Should().Be(committedEtag);
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().BeNull();
        var postPlans = _capturedExecutorRequest.PostRelationshipAuthorizationPlans;
        postPlans.Should().NotBeNull();
        postPlans!
            .ExistingResourcePlan.StoredValues.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.Subjects.Should()
            .ContainSingle(static subject => subject.IsPersonSubject);
        var createNewProposedAuthorization = postPlans
            .CreateNewProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Subject;
        var proposedCheck = createNewProposedAuthorization.CheckSpecs.Should().ContainSingle().Subject;
        proposedCheck.ValueSource.Should().Be(RelationshipAuthorizationValueSource.Proposed);
        proposedCheck
            .ConfiguredStrategy.StrategyName.Should()
            .Be(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly);
        proposedCheck.Subjects.Should().ContainSingle(static subject => subject.IsPersonSubject);
        proposedCheck
            .CheckTarget.Should()
            .BeOfType<RelationshipAuthorizationCheckTarget.Proposed>()
            .Which.SubjectBindingsInOrder.Should()
            .ContainSingle()
            .Which.Column.Should()
            .Be(AuthNames.StudentDocumentId);
    }

    [Test]
    public async Task It_builds_create_new_people_post_plan_with_self_person_subjects_ineligible()
    {
        var committedEtag = ComposedWriteResultEtag;
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_studentResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithSelfStudentSubjectAndPeopleAuthViews());
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc)
            .Returns(JsonNode.Parse("""{"studentUniqueId":"604822","firstName":"Self"}""")!);
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901L]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, committedEtag));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().BeNull();
        var postPlans = _capturedExecutorRequest.PostRelationshipAuthorizationPlans;
        postPlans.Should().NotBeNull();
        var createNewFailure = postPlans!
            .CreateNewImmediateResult.Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Subject.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Subject;
        createNewFailure.Errors.Should().ContainSingle();
        createNewFailure
            .Errors[0]
            .Should()
            .Contain("no executable Student relationship authorization subjects")
            .And.Contain("SelfPersonDocumentIdUnavailableForCreateNew");
        var existingResourceProposed = postPlans
            .ExistingResourcePlan.ProposedValues.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Subject;
        existingResourceProposed
            .CheckSpecs.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject.PersonMetadata!.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId);
    }

    [Test]
    public async Task It_treats_no_further_authorization_required_as_a_post_preflight_no_op()
    {
        var committedEtag = ComposedWriteResultEtag;
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, committedEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("No Further High"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, committedEtag));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.StoredNamespaceAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedNamespaceAuthorization.Should().BeNull();
    }

    [Test]
    public async Task It_threads_proposed_namespace_authorization_into_the_post_write_executor_request()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid, ComposedWriteResultEtag)
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateNamespaceWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Namespaced"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        await _sut.UpsertDocument(upsertRequest);

        _capturedExecutorRequests.Should().ContainSingle();

        // A POST plans both checks; the proposed check is applied for create, the stored check only
        // when the write resolves to an existing target. Each is a single check re-indexed to 0.
        var proposedAuthorization = _capturedExecutorRequest.ProposedNamespaceAuthorization;
        proposedAuthorization.Should().NotBeNull();
        var proposedCheck = proposedAuthorization!.Checks.Should().ContainSingle().Subject;
        proposedCheck.Index.Should().Be(0);
        proposedCheck.ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Proposed);
        proposedAuthorization
            .NamespacePrefixParameterization.ConfiguredPrefixesInOrder.Should()
            .Equal("uri://ed-fi.org/");

        var storedAuthorization = _capturedExecutorRequest.StoredNamespaceAuthorization;
        storedAuthorization.Should().NotBeNull();
        var storedCheck = storedAuthorization!.Checks.Should().ContainSingle().Subject;
        storedCheck.Index.Should().Be(0);
        storedCheck.ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);

        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().BeNull();
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().BeNull();
    }

    [Test]
    public async Task It_returns_a_namespace_no_prefixes_403_for_post_without_calling_the_write_executor()
    {
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateNamespaceWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("No prefixes"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], []));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        failure.NamespaceFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_for_post_when_no_usable_root_namespace_column()
    {
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("No usable column"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Ed-Fi.School")
            .And.Contain("NamespaceBased")
            .And.Contain("no Namespace securable element resolves to a root table column");
        _capturedExecutorRequests.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_for_post_when_mssql_namespace_prefix_cap_is_exceeded()
    {
        var mappingSet = CreateNamespaceWriteMappingSet(_schoolResourceInfo, SqlDialect.Mssql);
        string[] tooManyPrefixes = [.. Enumerable.Range(0, 2000).Select(index => $"uri://prefix-{index}/")];
        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Prefix cap"));
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => upsertRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], tooManyPrefixes));

        var result = await _sut.UpsertDocument(upsertRequest);

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2000 namespace prefixes")
            .And.Contain("exceeds the SQL Server limit");
        _capturedExecutorRequests.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_put_not_implemented_for_known_but_not_enabled_relationship_authorization_before_target_lookup()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateRequestBody("Roosevelt High");
        var documentInfo = CreateDocumentInfo();
        var mappingSet = CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        var writePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"");

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => updateRequest.TraceId).Returns(new TraceId("put-auth-deferred"));
        A.CallTo(() => updateRequest.WritePrecondition).Returns(writePrecondition);
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureNotImplemented>().Subject;
        failure.Reason.Should().Be(UpdateFailureNotImplementedReason.StrategyNotEnabled);
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        AssertSupportedRelationshipStrategyNames(failure.FailureMessage);
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_put_security_configuration_failure_before_known_but_not_enabled_result()
    {
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Security config"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                CreateAuthorizationStrategyEvaluator("CustomAuthorizationStrategy"),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().Contain(error => error.Contains("Relational PUT authorization metadata"));
        failure.Errors.Should().Contain(error => error.Contains("CustomAuthorizationStrategy"));
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_threads_stored_and_proposed_namespace_authorization_into_the_put_write_executor_request()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateSuccess(documentUuid, ComposedWriteResultEtag)
                    )
                )
            );

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateNamespaceWriteMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Namespaced"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        await _sut.UpdateDocumentById(updateRequest);

        _capturedExecutorRequests.Should().ContainSingle();

        // PUT always targets an existing document, so both the stored and proposed namespace checks
        // are threaded; each is a single check at index 0.
        var storedAuthorization = _capturedExecutorRequest.StoredNamespaceAuthorization;
        storedAuthorization.Should().NotBeNull();
        var storedCheck = storedAuthorization!.Checks.Should().ContainSingle().Subject;
        storedCheck.Index.Should().Be(0);
        storedCheck.ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);

        var proposedAuthorization = _capturedExecutorRequest.ProposedNamespaceAuthorization;
        proposedAuthorization.Should().NotBeNull();
        var proposedCheck = proposedAuthorization!.Checks.Should().ContainSingle().Subject;
        proposedCheck.Index.Should().Be(0);
        proposedCheck.ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Proposed);
    }

    [Test]
    public async Task It_forwards_put_empty_edorg_claims_to_the_write_executor_after_existing_target_lookup()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureRelationshipNotAuthorized(
                            CreateNoClaimsStoredRelationshipFailure()
                        )
                    )
                )
            );
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("No claims"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Stored);
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        failure
            .RelationshipFailure.FailedStrategies.Should()
            .ContainSingle()
            .Which.FailedSubjects.Should()
            .ContainSingle()
            .Which.FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest
            .StoredRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.NoClaims>();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_forwards_missing_put_target_not_found_for_empty_edorg_claims()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        ArrangeExecutorResult(
            new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
        );
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Missing no claims"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        _capturedExecutorRequests.Should().ContainSingle();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_returns_etag_mismatch_for_a_missing_put_target_when_if_match_is_a_wildcard()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        ArrangeExecutorResult(
            new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureETagMisMatch(ETagPreconditionFailureReason.TargetDoesNotExist)
            )
        );
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Missing wildcard"));
        A.CallTo(() => updateRequest.WritePrecondition)
            .Returns(new WritePrecondition.IfMatch("*", IsWildcard: true));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        // The PUT target does not exist at all, so the reason is TargetDoesNotExist rather than a
        // Concurrency mismatch.
        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _capturedExecutorRequests.Should().ContainSingle();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_forwards_authorized_put_relationship_plans_to_the_write_executor_after_target_lookup()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var committedEtag = ComposedWriteResultEtag;
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateSuccess(documentUuid, committedEtag)
                    )
                )
            );
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Authorized PUT High"));
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => updateRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901]));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, committedEtag));
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequest.StoredRelationshipAuthorization.Should().NotBeNull();
        _capturedExecutorRequest
            .StoredRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Stored);
        _capturedExecutorRequest.ProposedRelationshipAuthorization.Should().NotBeNull();
        _capturedExecutorRequest
            .ProposedRelationshipAuthorization.Should()
            .BeOfType<RelationshipAuthorizationResult.Authorized>()
            .Which.CheckSpecs.Should()
            .ContainSingle()
            .Which.ValueSource.Should()
            .Be(RelationshipAuthorizationValueSource.Proposed);
    }

    [Test]
    public async Task It_forwards_missing_put_targets_observed_inside_the_write_session()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        ArrangeExecutorResult(
            new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
        );
        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        _capturedExecutorRequests.Should().ContainSingle();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_retries_stale_put_guarded_no_op_attempts_once_against_fresh_target_state()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);
        var expectedReadPlan = mappingSet.ReadPlansByResource[new QualifiedResourceName("Ed-Fi", "School")];
        var executorCallCount = 0;

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Fresh retry"));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests[1].Should().NotBeSameAs(_capturedExecutorRequests[0]);
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
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_does_not_retry_put_guarded_no_ops_when_the_executor_finishes_after_refreshing_session_loaded_freshness()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo);

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Loaded freshness wins"));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().ContainSingle();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
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
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Post retry"));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests[1].Should().NotBeSameAs(_capturedExecutorRequests[0]);
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
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_does_not_retry_stale_put_guarded_no_op_attempts_when_if_match_is_present()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"current-etag\"");

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureETagMisMatch(),
                        RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                    )
                )
            );

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Retry mismatch"));
        A.CallTo(() => updateRequest.WritePrecondition).Returns(writePrecondition);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequests[0].WritePrecondition.Should().Be(writePrecondition);
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_retries_stale_put_guarded_no_op_attempts_once_when_if_match_is_a_wildcard()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        // A wildcard If-Match (*) is an existence precondition, not a concurrency check, so a stale
        // guarded no-op against a still-existing row follows the no-precondition retry path rather
        // than short-circuiting to a 412 (unlike the specific-tag case above).
        var writePrecondition = new WritePrecondition.IfMatch("*", IsWildcard: true);
        var executorCallCount = 0;

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Wildcard retry"));
        A.CallTo(() => updateRequest.WritePrecondition).Returns(writePrecondition);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, "\"45\""));
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests[1].Should().NotBeSameAs(_capturedExecutorRequests[0]);
        _capturedExecutorRequests
            .Select(request => request.WritePrecondition)
            .Should()
            .OnlyContain(precondition => precondition == writePrecondition);
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_does_not_retry_stale_post_as_update_guarded_no_op_attempts_when_if_match_is_present()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"current-etag\"");
        var documentInfo = CreateDocumentInfo();

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureETagMisMatch(),
                        RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Retry mismatch"));
        A.CallTo(() => upsertRequest.WritePrecondition).Returns(writePrecondition);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        _capturedExecutorRequests.Should().ContainSingle();
        _capturedExecutorRequests[0].WritePrecondition.Should().Be(writePrecondition);
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_returns_write_conflict_when_the_single_stale_no_op_retry_is_also_stale()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var executorCallCount = 0;

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
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

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody("Retry conflict"));

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        _capturedExecutorRequests.Should().HaveCount(2);
        _capturedExecutorRequests[1].Should().NotBeSameAs(_capturedExecutorRequests[0]);
        _capturedExecutorRequests
            .Select(request => request.TargetRequest)
            .Should()
            .OnlyContain(targetRequest =>
                targetRequest.Equals(new RelationalWriteTargetRequest.Put(documentUuid))
            );
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
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

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureReference([invalidDocumentReference], [])
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
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

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureReference([], [invalidDescriptorReference])
                    )
                )
            );

        var updateRequest = A.Fake<IUpdateRequest>();
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

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureValidation([validationFailure])
                    )
                )
            );

        var upsertRequest = A.Fake<IUpsertRequest>();
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

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Returns(
                Task.FromResult<RelationalWriteExecutorResult>(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureValidation([validationFailure])
                    )
                )
            );

        var updateRequest = A.Fake<IUpdateRequest>();
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
        var expectedResult = new UpsertResult.UnknownFailure(
            "Descriptor POST write is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor'."
        );
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(expectedResult);
        UseDescriptorWriteHandler(descriptorHandler);

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(expectedResult);
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        _capturedExecutorRequests.Should().BeEmpty();
    }

    [Test]
    public async Task It_routes_descriptor_put_requests_to_the_descriptor_write_handler()
    {
        var expectedResult = new UpdateResult.UnknownFailure(
            "Descriptor PUT write is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor'."
        );
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(expectedResult);
        UseDescriptorWriteHandler(descriptorHandler);

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(expectedResult);
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        _capturedExecutorRequests.Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_descriptor_post_etags_returned_by_the_handler_without_a_follow_up_lookup()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            new NoOpRelationalWriteExceptionClassifier(),
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateDescriptorRequestBody();
        var descriptorResponseEtag = ComposedWriteResultEtag;
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(new UpsertResult.InsertSuccess(documentUuid, descriptorResponseEtag));

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(documentUuid, descriptorResponseEtag));
        ((UpsertResult.InsertSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [TestCaseSource(nameof(DescriptorWritePreconditions))]
    public async Task It_forwards_write_preconditions_to_the_descriptor_post_handler(
        WritePrecondition expectedWritePrecondition
    )
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        DescriptorWriteRequest capturedRequest = null!;
        var documentUuid = new DocumentUuid(Guid.NewGuid());

        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorWriteRequest>(0)!)
            .Returns(new UpsertResult.InsertSuccess(documentUuid, "\"descriptor-etag\""));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateDescriptorRequestBody());
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("descriptor-post-precondition"));
        A.CallTo(() => upsertRequest.WritePrecondition).Returns(expectedWritePrecondition);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        capturedRequest.WritePrecondition.Should().Be(expectedWritePrecondition);
    }

    [Test]
    public async Task It_preserves_descriptor_put_etags_returned_by_the_handler_without_a_follow_up_lookup()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            new NoOpRelationalWriteExceptionClassifier(),
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var requestBody = CreateDescriptorRequestBody("Updated Charter");
        var descriptorResponseEtag = ComposedWriteResultEtag;
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Returns(new UpdateResult.UpdateSuccess(documentUuid, descriptorResponseEtag));

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, descriptorResponseEtag));
        ((UpdateResult.UpdateSuccess)result).ETag.Should().NotMatchRegex(StampStyleEtagPattern);
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [TestCaseSource(nameof(DescriptorWritePreconditions))]
    public async Task It_forwards_write_preconditions_to_the_descriptor_put_handler(
        WritePrecondition expectedWritePrecondition
    )
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        DescriptorWriteRequest capturedRequest = null!;
        var documentUuid = new DocumentUuid(Guid.NewGuid());

        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorWriteRequest>(0)!)
            .Returns(new UpdateResult.UpdateSuccess(documentUuid, "\"descriptor-etag\""));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateDescriptorRequestBody("Updated Charter"));
        A.CallTo(() => updateRequest.TraceId).Returns(new TraceId("descriptor-put-precondition"));
        A.CallTo(() => updateRequest.WritePrecondition).Returns(expectedWritePrecondition);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        capturedRequest.WritePrecondition.Should().Be(expectedWritePrecondition);
    }

    [Test]
    public async Task It_forwards_authorization_strategies_and_relational_authorization_context_to_the_descriptor_post_handler()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        var expectedDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var expectedTraceId = new TraceId("descriptor-post-forwarding");
        var expectedMappingSet = CreateDescriptorOnlyMappingSet(_descriptorResourceInfo);
        AuthorizationStrategyEvaluator[] expectedAuthorizationStrategyEvaluators =
        [
            CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
        ];
        var expectedAuthorizationContext = new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]);
        DescriptorWriteRequest capturedRequest = null!;

        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorWriteRequest>(0)!)
            .Returns(new UpsertResult.InsertSuccess(expectedDocumentUuid, "\"descriptor-etag\""));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(expectedMappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(expectedDocumentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateDescriptorRequestBody());
        A.CallTo(() => upsertRequest.TraceId).Returns(expectedTraceId);
        A.CallTo(() => upsertRequest.WritePrecondition).Returns(new WritePrecondition.None());
        A.CallTo(() => upsertRequest.AuthorizationStrategyEvaluators)
            .Returns(expectedAuthorizationStrategyEvaluators);
        A.CallTo(() => upsertRequest.AuthorizationContext).Returns(expectedAuthorizationContext);

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        capturedRequest.MappingSet.Should().BeSameAs(expectedMappingSet);
        capturedRequest.DocumentUuid.Should().Be(expectedDocumentUuid);
        capturedRequest.TraceId.Value.Should().Be(expectedTraceId.Value);
        capturedRequest
            .AuthorizationStrategyEvaluators.Should()
            .BeSameAs(expectedAuthorizationStrategyEvaluators);
        capturedRequest.RelationalAuthorizationContext.Should().BeSameAs(expectedAuthorizationContext);
        A.CallTo(() => descriptorHandler.HandlePostAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_forwards_authorization_strategies_and_relational_authorization_context_to_the_descriptor_put_handler()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        var expectedDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var expectedTraceId = new TraceId("descriptor-put-forwarding");
        var expectedMappingSet = CreateDescriptorOnlyMappingSet(_descriptorResourceInfo);
        AuthorizationStrategyEvaluator[] expectedAuthorizationStrategyEvaluators =
        [
            CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
        ];
        var expectedAuthorizationContext = new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]);
        DescriptorWriteRequest capturedRequest = null!;

        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorWriteRequest>(0)!)
            .Returns(new UpdateResult.UpdateSuccess(expectedDocumentUuid, "\"descriptor-etag\""));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(expectedMappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(expectedDocumentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateDescriptorRequestBody("Updated Charter"));
        A.CallTo(() => updateRequest.TraceId).Returns(expectedTraceId);
        A.CallTo(() => updateRequest.WritePrecondition).Returns(new WritePrecondition.None());
        A.CallTo(() => updateRequest.AuthorizationStrategyEvaluators)
            .Returns(expectedAuthorizationStrategyEvaluators);
        A.CallTo(() => updateRequest.AuthorizationContext).Returns(expectedAuthorizationContext);

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        capturedRequest.MappingSet.Should().BeSameAs(expectedMappingSet);
        capturedRequest.DocumentUuid.Should().Be(expectedDocumentUuid);
        capturedRequest.TraceId.Value.Should().Be(expectedTraceId.Value);
        capturedRequest
            .AuthorizationStrategyEvaluators.Should()
            .BeSameAs(expectedAuthorizationStrategyEvaluators);
        capturedRequest.RelationalAuthorizationContext.Should().BeSameAs(expectedAuthorizationContext);
        A.CallTo(() => descriptorHandler.HandlePutAsync(A<DescriptorWriteRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_routes_descriptor_delete_requests_to_the_descriptor_write_handler()
    {
        var expectedResult = new DeleteResult.UnknownFailure("Descriptor DELETE write is not implemented.");
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DescriptorDeleteRequest>._, A<CancellationToken>._)
            )
            .Returns(expectedResult);
        UseDescriptorWriteHandler(descriptorHandler);

        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => deleteRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("test-trace"));
        A.CallTo(() => deleteRequest.WritePrecondition).Returns(new WritePrecondition.None());

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeEquivalentTo(expectedResult);
        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DescriptorDeleteRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        _capturedExecutorRequests.Should().BeEmpty();
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_forwards_resource_document_uuid_trace_id_and_authorization_strategies_to_the_descriptor_delete_handler()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        var expectedDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var expectedTraceId = new TraceId("descriptor-delete-forwarding");
        var expectedMappingSet = CreateDescriptorOnlyMappingSet(_descriptorResourceInfo);
        AuthorizationStrategyEvaluator[] expectedAuthorizationStrategyEvaluators =
        [
            CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
        ];
        var expectedResource = new QualifiedResourceName(
            _descriptorResourceInfo.ProjectName.Value,
            _descriptorResourceInfo.ResourceName.Value
        );
        DescriptorDeleteRequest capturedRequest = null!;

        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DescriptorDeleteRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorDeleteRequest>(0)!)
            .Returns(Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess()));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => deleteRequest.MappingSet).Returns(expectedMappingSet);
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(expectedDocumentUuid);
        A.CallTo(() => deleteRequest.TraceId).Returns(expectedTraceId);
        A.CallTo(() => deleteRequest.WritePrecondition).Returns(new WritePrecondition.None());
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns(expectedAuthorizationStrategyEvaluators);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        capturedRequest.MappingSet.Should().BeSameAs(expectedMappingSet);
        capturedRequest.Resource.Should().Be(expectedResource);
        capturedRequest.DocumentUuid.Should().Be(expectedDocumentUuid);
        capturedRequest.TraceId.Value.Should().Be(expectedTraceId.Value);
        capturedRequest
            .AuthorizationStrategyEvaluators.Should()
            .BeSameAs(expectedAuthorizationStrategyEvaluators);
        capturedRequest.WritePrecondition.Should().BeOfType<WritePrecondition.None>();
        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DescriptorDeleteRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [TestCaseSource(nameof(DescriptorWritePreconditions))]
    public async Task It_forwards_write_preconditions_to_the_descriptor_delete_handler(
        WritePrecondition expectedWritePrecondition
    )
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        DescriptorDeleteRequest capturedRequest = null!;

        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DescriptorDeleteRequest>._, A<CancellationToken>._)
            )
            .Invokes(call => capturedRequest = call.GetArgument<DescriptorDeleteRequest>(0)!)
            .Returns(Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess()));

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );

        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(_descriptorResourceInfo);
        A.CallTo(() => deleteRequest.MappingSet)
            .Returns(CreateDescriptorOnlyMappingSet(_descriptorResourceInfo));
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("descriptor-delete-precondition"));
        A.CallTo(() => deleteRequest.WritePrecondition).Returns(expectedWritePrecondition);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        capturedRequest.WritePrecondition.Should().Be(expectedWritePrecondition);
    }

    [Test]
    public async Task It_does_not_route_non_descriptor_delete_requests_through_the_descriptor_write_handler()
    {
        var descriptorHandler = A.Fake<IDescriptorWriteHandler>();
        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _currentEtagPreconditionChecker,
            descriptorHandler,
            _descriptorReadHandler,
            _referenceResolver,
            _documentHydrator,
            _readTargetLookupService,
            _readMaterializer,
            _readableProfileProjector,
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            _writeSessionFactory,
            CreateAuthorizationSubjectSelector(),
            _singleRecordRelationshipAuthorizationExecutor,
            _namespaceAuthorizationExecutor,
            _customViewAuthorizationExecutor,
            _commandExecutor,
            readAccelerationCoordinator: PassthroughDocumentCacheReadAccelerationCoordinator.Instance
        );
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        A.CallTo(() =>
                descriptorHandler.HandleDeleteAsync(A<DescriptorDeleteRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_not_exists_when_the_document_uuid_is_not_resolvable(
        SqlDialect dialect
    )
    {
        // The capture statement observes no target row, which the repository answers as not-found.
        ConfigureMissingDeleteTarget();

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    [Test]
    public async Task It_deletes_when_stored_namespace_matches_a_prefix_without_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_deletes_when_stored_namespace_matches_a_prefix_with_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"current-etag\"");
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        ConfigureDeleteOutcome(deleted: true);
        _currentEtagPreconditionChecker.ResultToReturn = CreateDeletePreconditionCheckResult(
            documentUuid,
            345L,
            isMatch: true
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, writePrecondition, documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _currentEtagPreconditionChecker.CallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_ignores_if_none_match_on_a_delete_and_still_deletes()
    {
        // Decision 6: DELETE + If-None-Match is not a meaningful idiom. The delete precondition path
        // keys on If-Match, so a sibling If-None-Match falls through to "no precondition" — the delete
        // proceeds and the etag precondition checker is never invoked.
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            mappingSet,
            new WritePrecondition.IfNoneMatch("*", IsWildcard: true),
            documentUuid
        );
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_mismatch_403_and_does_not_delete_without_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = AsMssql(CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo));
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        ConfigureDeleteAuth1Failure(
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    0,
                    NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
                )
            )
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/", "uri://gbisd.edu/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>().Subject;
        failure.NamespaceFailure.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        failure
            .NamespaceFailure.ConfiguredNamespacePrefixes.Should()
            .Equal("uri://ed-fi.org/", "uri://gbisd.edu/");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_uninitialized_403_and_does_not_delete()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = AsMssql(CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo));
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        ConfigureDeleteAuth1Failure(
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    0,
                    NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized
                )
            )
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_403_not_412_when_stored_namespace_denies_even_with_stale_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"");
        var mappingSet = AsMssql(CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo));
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        ConfigureDeleteAuth1Failure(
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    0,
                    NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
                )
            )
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreateDeletePreconditionCheckResult(
            documentUuid,
            345L,
            isMatch: false
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, writePrecondition, documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_no_prefixes_403_without_creating_the_write_session_for_delete()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], []));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>().Subject;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        failure.NamespaceFailure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_security_configuration_500_when_no_usable_root_namespace_column_for_delete()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Ed-Fi.School")
            .And.Contain("NamespaceBased")
            .And.Contain("no Namespace securable element resolves to a root table column");
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_security_configuration_500_when_mssql_namespace_prefix_cap_is_exceeded_for_delete()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var pgsqlMappingSet = CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo);
        var mappingSet = pgsqlMappingSet with
        {
            Key = pgsqlMappingSet.Key with { Dialect = SqlDialect.Mssql },
        };
        string[] tooManyPrefixes = [.. Enumerable.Range(0, 2000).Select(index => $"uri://prefix-{index}/")];

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], tooManyPrefixes));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2000 namespace prefixes")
            .And.Contain("exceeds the SQL Server limit");
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_fails_closed_with_security_configuration_when_namespace_auth1_is_malformed_and_does_not_delete()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = AsMssql(CreateNamespaceAuthorizationMappingSet(_schoolResourceInfo));
        ConfigureResolvedDocument(documentId: 345L, documentUuid);
        ConfigureDeleteNamespaceCheckCount(1);
        // A well-formed payload naming a check the command never planned: the denial is real but its
        // metadata cannot be mapped, so the request fails closed rather than deleting.
        ConfigureDeleteAuth1Failure(
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    5,
                    NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
                )
            )
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.NamespaceBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_commits_a_delete_whose_stored_relationship_authorization_authorizes()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        ConfigureResolvedDocument(documentId: 123L, documentUuid);
        ConfigureDeleteRelationshipAuthorizationRow(authorizationResult: 1);
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901L]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        // The repository's remaining stake in an authorized delete is the session lifecycle: the command
        // stream and the check's own statement order are asserted against the delete command itself.
        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_short_circuits_delete_relationship_authorization_with_empty_edorg_claims()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        ConfigureResolvedDocument(documentId: 123L, documentUuid);
        ConfigureDeleteThrows(new InvalidOperationException("DELETE should not execute on auth failure."));

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureRelationshipNotAuthorized>().Subject;
        failure.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        failure
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Should()
            .AllSatisfy(subject =>
                subject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship)
            );
        A.CallTo(_commandExecutor)
            .WithReturnType<Task<SingleRecordRelationshipAuthorizationExecutionResult>>()
            .MustNotHaveHappened();
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_preserves_missing_delete_target_not_found_for_empty_edorg_claims()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        ConfigureMissingDeleteTarget();

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
        A.CallTo(_commandExecutor)
            .WithReturnType<Task<SingleRecordRelationshipAuthorizationExecutionResult>>()
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_not_implemented_before_delete_target_lookup_when_authorization_includes_a_still_unsupported_strategy()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
                CreateAuthorizationStrategyEvaluator(AuthorizationStrategyNameConstants.OwnershipBased),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901L]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotImplemented>();
        var failure = result.As<DeleteResult.DeleteFailureNotImplemented>();
        failure.FailureMessage.Should().Contain(AuthorizationStrategyNameConstants.OwnershipBased);
        AssertSupportedRelationshipStrategyNames(failure.FailureMessage);
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
        A.CallTo(_commandExecutor)
            .WithReturnType<Task<SingleRecordRelationshipAuthorizationExecutionResult>>()
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_security_configuration_failure_before_delete_target_lookup_when_authorization_has_only_child_table_edorg_subjects()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithChildOnlyEdOrgSubject(_schoolResourceInfo);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                ),
            ]);
        A.CallTo(() => deleteRequest.AuthorizationContext)
            .Returns(new RelationalAuthorizationContext([255901L]));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>();
        result.As<DeleteResult.DeleteFailureSecurityConfiguration>().Errors.Should().ContainSingle();
        result
            .As<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Errors[0]
            .Should()
            .Contain("$.classPeriods[*].classPeriodReference.schoolId");
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_bypasses_delete_relationship_sql_when_only_no_further_authorization_required_applies()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateQuerySupportedMappingSetWithRootEdOrgSubject(_schoolResourceInfo);
        ConfigureResolvedDocument(documentId: 123L, documentUuid);
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, documentUuid: documentUuid);
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators)
            .Returns([
                CreateAuthorizationStrategyEvaluator(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                ),
            ]);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        A.CallTo(_commandExecutor)
            .WithReturnType<Task<SingleRecordRelationshipAuthorizationExecutionResult>>()
            .MustNotHaveHappened();
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
    public async Task It_returns_delete_failure_reference_with_the_resolved_resource_name_when_the_resolver_finds_the_owning_resource(
        SqlDialect dialect
    )
    {
        const string constraintName = "FK_Calendar_SchoolRef";
        var referencingResource = new QualifiedResourceName("Ed-Fi", "Calendar");
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo, dialect);
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("constraint violation"));
        _writeExceptionClassifier.IsForeignKeyViolationToReturn = true;
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(constraintName);
        A.CallTo(() =>
                _deleteConstraintResolver.TryResolveReferencingResource(mappingSet.Model, constraintName)
            )
            .Returns(referencingResource);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([referencingResource.ResourceName]));
        _writeExceptionClassifier.IsForeignKeyViolationCallCount.Should().Be(1);
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
        // Match on the exact MappingSet.Model reference — a narrowing of the any-matcher that
        // catches a regression where the repository stops forwarding mappingSet.Model to the
        // resolver (e.g., accidentally wires a stale or null model set). The fake would
        // otherwise accept any DerivedRelationalModelSet and hide the wire-through bug.
        A.CallTo(() =>
                _deleteConstraintResolver.TryResolveReferencingResource(mappingSet.Model, constraintName)
            )
            .MustHaveHappenedOnceExactly();
        // Match on the log payload so the assertion fails if the FK-resolution Debug log is
        // removed or demoted — an unrelated "Entering..." Debug log is always emitted by
        // DeleteDocumentById, so a bare `r.Level == Debug` check would pass even without the new
        // line.
        _logger
            .Records.Should()
            .ContainSingle(r =>
                r.Level == LogLevel.Debug
                && r.Message.Contains(constraintName, StringComparison.Ordinal)
                && r.Message.Contains(referencingResource.ResourceName, StringComparison.Ordinal)
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_an_empty_reference_failure_and_logs_information_when_the_classifier_cannot_extract_a_constraint_name(
        SqlDialect dialect
    )
    {
        // IsForeignKeyViolation is true but TryClassify reports UnrecognizedWriteFailure —
        // pgsql 23503 with a null ConstraintName, or mssql 547 with a localized / unparseable
        // message. Resolver must NOT be called; log level is Information.
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("constraint violation"));
        _writeExceptionClassifier.IsForeignKeyViolationToReturn = true;
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference([]));
        A.CallTo(() =>
                _deleteConstraintResolver.TryResolveReferencingResource(
                    A<DerivedRelationalModelSet>._,
                    A<string>._
                )
            )
            .MustNotHaveHappened();
        _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Information);
        // Decisions #4 splits Information (missing constraint name) from Warning (unresolved
        // constraint name). A bare Contain(Information) would pass even if a refactor accidentally
        // fired both branches for the same failure — assert Warning is absent to pin the split.
        _logger.Records.Should().NotContain(r => r.Level == LogLevel.Warning);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_an_empty_reference_failure_and_logs_warning_when_the_resolver_cannot_map_the_constraint_name(
        SqlDialect dialect
    )
    {
        // Classifier hands off a real constraint name, but the resolver cannot find it in the
        // compiled model — drift between deployed DDL and runtime model. Log level is Warning.
        const string constraintName = "FK_Unknown_To_Model";
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo, dialect);
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("constraint violation"));
        _writeExceptionClassifier.IsForeignKeyViolationToReturn = true;
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(constraintName);
        A.CallTo(() =>
                _deleteConstraintResolver.TryResolveReferencingResource(mappingSet.Model, constraintName)
            )
            .Returns((QualifiedResourceName?)null);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference([]));
        A.CallTo(() =>
                _deleteConstraintResolver.TryResolveReferencingResource(mappingSet.Model, constraintName)
            )
            .MustHaveHappenedOnceExactly();
        _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning);
        // Decisions #4 splits Warning (unresolved constraint name) from Information (missing
        // constraint name). A bare Contain(Warning) would pass even if a refactor accidentally
        // fired both branches for the same failure — assert Information is absent to pin the split.
        _logger.Records.Should().NotContain(r => r.Level == LogLevel.Information);
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
    public async Task It_commits_the_write_session_when_the_delete_succeeds()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_delete_failure_write_conflict_when_commit_throws_a_transient_failure()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);
        _writeSessionFactory.Session.CommitExceptionToThrow = new StubDbException("deadlock on commit");
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_unknown_failure_when_commit_throws_a_non_transient_database_error()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);
        _writeSessionFactory.Session.CommitExceptionToThrow = new StubDbException("boom on commit");

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.UnknownFailure>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_write_session_when_the_document_uuid_is_not_resolvable()
    {
        ConfigureMissingDeleteTarget();

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_write_session_when_the_delete_yields_no_rows()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: false);

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_write_session_when_a_foreign_key_violation_is_classified()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("constraint violation"));
        _writeExceptionClassifier.IsForeignKeyViolationToReturn = true;

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureReference>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_write_session_when_a_transient_failure_is_classified()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("deadlock"));
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_write_session_on_generic_database_failure()
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteThrows(new StubDbException("boom"));

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.UnknownFailure>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_write_conflict_when_the_lookup_reports_a_transient_failure(
        SqlDialect dialect
    )
    {
        ConfigureLookupThrows(new StubDbException("deadlock on lookup"));
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_unknown_failure_when_the_lookup_throws_a_generic_database_exception(
        SqlDialect dialect
    )
    {
        ConfigureLookupThrows(new StubDbException("lookup boom"));

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
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_delete_failure_write_conflict_when_session_creation_reports_a_transient_failure()
    {
        _writeSessionFactory.CreateAsyncExceptionToThrow = new StubDbException("deadlock at session create");
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_unknown_failure_when_session_creation_throws_a_generic_database_exception()
    {
        _writeSessionFactory.CreateAsyncExceptionToThrow = new StubDbException("session create boom");

        var deleteRequest = CreateNonDescriptorDeleteRequest(CreateSupportedMappingSet(_schoolResourceInfo));

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeEquivalentTo(
                new DeleteResult.UnknownFailure(
                    "An unexpected error occurred while processing the delete request."
                )
            );
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(0);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_skips_the_current_etag_check_for_relational_delete_requests_without_if_match_precondition(
        SqlDialect dialect
    )
    {
        ConfigureResolvedDocument(documentId: 123L, documentUuid: new DocumentUuid(Guid.NewGuid()));
        ConfigureDeleteOutcome(deleted: true);

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect)
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _currentEtagPreconditionChecker.CallCount.Should().Be(0);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_allows_relational_delete_when_if_match_precondition_exactly_matches_the_current_representation(
        SqlDialect dialect
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo, dialect);
        var writePrecondition = new WritePrecondition.IfMatch("\"current-etag\"");
        ConfigureResolvedDocument(documentId: 123L, documentUuid);
        ConfigureDeleteOutcome(deleted: true);
        _currentEtagPreconditionChecker.ResultToReturn = CreateDeletePreconditionCheckResult(
            documentUuid,
            123L,
            isMatch: true
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, writePrecondition, documentUuid);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        _currentEtagPreconditionChecker.CallCount.Should().Be(1);
        _currentEtagPreconditionChecker.CapturedRequest.Should().NotBeNull();
        _currentEtagPreconditionChecker.CapturedRequest!.MappingSet.Should().BeSameAs(mappingSet);
        _currentEtagPreconditionChecker.CapturedRequest.TargetContext.DocumentId.Should().Be(123L);
        _currentEtagPreconditionChecker.CapturedRequest.TargetContext.DocumentUuid.Should().Be(documentUuid);
        _currentEtagPreconditionChecker.CapturedRequest.Precondition.Should().Be(writePrecondition);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_relational_delete_failure_etag_mismatch_and_does_not_issue_the_delete_when_if_match_precondition_does_not_match(
        SqlDialect dialect
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"stale-etag\"");
        ConfigureResolvedDocument(documentId: 123L, documentUuid);
        ConfigureDeleteThrows(new InvalidOperationException("DELETE should not execute on ETag mismatch."));
        _currentEtagPreconditionChecker.ResultToReturn = CreateDeletePreconditionCheckResult(
            documentUuid,
            123L,
            isMatch: false
        );

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect),
            writePrecondition,
            documentUuid
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        // The target exists but its current etag does not match the specific-tag If-Match precondition,
        // so the reason is Concurrency rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        _currentEtagPreconditionChecker.CallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_relational_delete_failure_etag_mismatch_when_a_wildcard_if_match_document_uuid_is_not_resolvable(
        SqlDialect dialect
    )
    {
        // RFC 9110 §13.1.1 If-Match: * requires the target to exist; against an unresolvable DELETE target
        // the wildcard yields 412 rather than 404.
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("some-wrong-value", IsWildcard: true);
        ConfigureMissingDeleteTarget();

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect),
            writePrecondition,
            documentUuid
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        // The DELETE target is entirely unresolvable, so there is no current representation and the
        // reason is TargetDoesNotExist rather than Concurrency.
        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_relational_delete_failure_not_exists_when_a_non_wildcard_if_match_document_uuid_is_not_resolvable(
        SqlDialect dialect
    )
    {
        // Regression guard: a non-wildcard If-Match against an unresolvable DELETE target still
        // returns 404.
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"current-etag\"");
        ConfigureMissingDeleteTarget();

        var deleteRequest = CreateNonDescriptorDeleteRequest(
            CreateSupportedMappingSet(_schoolResourceInfo, dialect),
            writePrecondition,
            documentUuid
        );

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_preserves_foreign_key_conflict_mapping_when_relational_delete_if_match_precondition_succeeds(
        SqlDialect dialect
    )
    {
        const string constraintName = "FK_Calendar_SchoolRef";
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePrecondition = new WritePrecondition.IfMatch("\"current-etag\"");
        var referencingResource = new QualifiedResourceName("Ed-Fi", "Calendar");
        var mappingSet = CreateSupportedMappingSet(_schoolResourceInfo, dialect);
        ConfigureResolvedDocument(documentId: 123L, documentUuid);
        ConfigureDeleteThrows(new StubDbException("constraint violation"));
        _currentEtagPreconditionChecker.ResultToReturn = CreateDeletePreconditionCheckResult(
            documentUuid,
            123L,
            isMatch: true
        );
        _writeExceptionClassifier.IsForeignKeyViolationToReturn = true;
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(constraintName);
        A.CallTo(() =>
                _deleteConstraintResolver.TryResolveReferencingResource(mappingSet.Model, constraintName)
            )
            .Returns(referencingResource);

        var deleteRequest = CreateNonDescriptorDeleteRequest(mappingSet, writePrecondition, documentUuid);

        var result = await _sut.DeleteDocumentById(deleteRequest);

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([referencingResource.ResourceName]));
        _currentEtagPreconditionChecker.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_the_missing_write_plan_guard_rail_for_non_descriptor_post_requests()
    {
        var upsertRequest = A.Fake<IUpsertRequest>();
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

        var updateRequest = A.Fake<IUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => updateRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => updateRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpdateDocumentById(updateRequest);

        result.Should().BeEquivalentTo(new UpdateResult.UnknownFailure(expectedFailureMessage));
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_missing_read_plan_guard_rail_for_existing_document_post_as_update_requests()
    {
        var candidateDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var documentInfo = CreateDocumentInfo();
        const string expectedFailureMessage =
            "Read plan lookup failed for resource 'Ed-Fi.School' in mapping set "
            + "'schema-hash/Pgsql/v1': resource storage kind 'RelationalTables' should always have a compiled relational-table read plan, "
            + "but no entry was found. This indicates an internal compilation/selection bug.";

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(candidateDocumentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UnknownFailure(expectedFailureMessage));
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
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

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet)
            .Returns(CreateMissingReadPlanMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(documentInfo);
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody("Create without read plan"));

        var result = await _sut.UpsertDocument(upsertRequest);

        result.Should().BeEquivalentTo(new UpsertResult.UnknownFailure(expectedFailureMessage));
        _capturedExecutorRequests.Should().BeEmpty();
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_does_not_remap_internal_executor_invalid_operation_failures()
    {
        var internalFailure = new InvalidOperationException(
            "Resolved lookup set did not contain a matching 'Ed-Fi.School' entry at '$.schoolReference'."
        );

        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Throws(internalFailure);

        var upsertRequest = A.Fake<IUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(_schoolResourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(CreateSupportedMappingSet(_schoolResourceInfo));
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(CreateRequestBody());

        Func<Task> act = async () => _ = await _sut.UpsertDocument(upsertRequest);

        var thrownException = await act.Should().ThrowAsync<InvalidOperationException>();
        thrownException.Which.Message.Should().Be(internalFailure.Message);
    }

    /// <summary>
    /// Arranges the result the faked executor returns for every attempt, capturing each input it
    /// receives. Used by tests whose outcome the executor now owns because it resolves the target
    /// inside its own write session.
    /// </summary>
    private void ArrangeExecutorResult(RelationalWriteExecutorResult executorResult)
    {
        A.CallTo(() => _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorInput>._, A<CancellationToken>._))
            .Invokes(call =>
            {
                _capturedExecutorRequest = call.GetArgument<RelationalWriteExecutorInput>(0)!;
                _capturedExecutorRequests.Add(_capturedExecutorRequest);
            })
            .Returns(Task.FromResult(executorResult));
    }

    private static IDeleteRequest CreateNonDescriptorDeleteRequest(
        MappingSet mappingSet,
        WritePrecondition? writePrecondition = null,
        DocumentUuid? documentUuid = null,
        ResourceInfo? resourceInfo = null
    )
    {
        resourceInfo ??= _schoolResourceInfo;

        var deleteRequest = A.Fake<IDeleteRequest>();
        A.CallTo(() => deleteRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => deleteRequest.DocumentUuid).Returns(documentUuid ?? new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => deleteRequest.TraceId).Returns(new TraceId("delete-trace"));
        A.CallTo(() => deleteRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => deleteRequest.WritePrecondition)
            .Returns(writePrecondition ?? new WritePrecondition.None());
        A.CallTo(() => deleteRequest.AuthorizationStrategyEvaluators).Returns([]);
        A.CallTo(() => deleteRequest.AuthorizationContext).Returns(new RelationalAuthorizationContext([]));
        return deleteRequest;
    }

    private static RelationalDeleteEtagPreconditionCheckResult CreateDeletePreconditionCheckResult(
        DocumentUuid documentUuid,
        long documentId,
        bool isMatch
    )
    {
        var targetContext = new RelationalWriteTargetContext.ExistingDocument(documentId, documentUuid, 42L);

        return new RelationalDeleteEtagPreconditionCheckResult(targetContext, isMatch);
    }

    /// <summary>
    /// Arranges the relational DELETE's capture statement to observe a target. The composite delete emits its
    /// statements as raw commands, so every delete arrangement feeds one responder the recording session
    /// consults when a command arrives.
    /// </summary>
    private void ConfigureResolvedDocument(long documentId, DocumentUuid documentUuid)
    {
        var responder = InstallDeleteResponder();
        responder.TargetExists = true;
        responder.DocumentId = documentId;
        responder.DocumentUuid = documentUuid.Value;
    }

    /// <summary>Arranges the delete capture statement to observe no target row.</summary>
    private void ConfigureMissingDeleteTarget() => InstallDeleteResponder().TargetExists = false;

    /// <summary>
    /// Arranges what the deletes answer, on both transports: a specific-tag <c>If-Match</c> or an
    /// authorization check that cannot be co-batched routes them into an ordered segment, which runs through
    /// the session's command executor rather than as a raw composite command.
    /// </summary>
    private void ConfigureDeleteOutcome(bool deleted, Action<RelationalCommand>? callback = null)
    {
        var responder = InstallDeleteResponder();
        responder.Deleted = deleted;

        if (callback is not null)
        {
            responder.OnDeleteCommand = callback;
        }

        var call = A.CallTo(_commandExecutor).WithReturnType<Task<bool>>();

        if (callback is not null)
        {
            call.Invokes(fakeCall => callback(fakeCall.GetArgument<RelationalCommand>(0)!));
        }

        call.Returns(Task.FromResult(deleted));
    }

    private void ConfigureDeleteThrows(Exception exception)
    {
        InstallDeleteResponder().DeleteExceptionToThrow = exception;
        A.CallTo(_commandExecutor).WithReturnType<Task<bool>>().Throws(exception);
    }

    private void ConfigureLookupThrows(DbException exception) =>
        InstallDeleteResponder().CaptureExceptionToThrow = exception;

    /// <summary>
    /// Arranges the stored relationship check's decoded row. A denial arrives as a provider AUTH1 failure
    /// rather than a row, so only the authorized and unexpected-result outcomes are expressible here.
    /// </summary>
    private void ConfigureDeleteRelationshipAuthorizationRow(int authorizationResult) =>
        InstallDeleteResponder().RelationshipAuthorizationResult = authorizationResult;

    /// <summary>Arranges how many stored namespace checks the capture command carries.</summary>
    private void ConfigureDeleteNamespaceCheckCount(int checkCount) =>
        InstallDeleteResponder().NamespaceCheckCount = checkCount;

    private DeleteCommandResponder InstallDeleteResponder()
    {
        if (_deleteCommandResponder is null)
        {
            _deleteCommandResponder = new DeleteCommandResponder();
            _writeSessionFactory.Session.RawCommandResponder = _deleteCommandResponder.Respond;
        }

        return _deleteCommandResponder;
    }

    /// <summary>
    /// Arranges the opening delete command to abort through the <c>AUTH1</c> device, the way a provider does
    /// when a co-batched authorization statement denies.
    /// </summary>
    /// <remarks>
    /// SQL Server carries the payload in the message text, which is what the production failure extractor
    /// reads, so a denial is arrangeable with an ordinary <see cref="DbException"/> and no extractor seam.
    /// The mapping set must therefore be a SQL Server one.
    /// </remarks>
    private void ConfigureDeleteAuth1Failure(string payload) =>
        ConfigureLookupThrows(new StubDbException($"AUTH1 - {payload}"));

    private static MappingSet AsMssql(MappingSet mappingSet) =>
        mappingSet with
        {
            Key = mappingSet.Key with { Dialect = SqlDialect.Mssql },
        };

    private sealed record CapturedDeleteEtagPreconditionRequest(
        MappingSet MappingSet,
        RelationalWriteTargetContext.ExistingDocument TargetContext,
        WritePrecondition.IfMatch Precondition
    );

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class RecordingRelationalCurrentEtagPreconditionChecker
        : IRelationalDeleteEtagPreconditionChecker
    {
        public int CallCount { get; private set; }

        public CapturedDeleteEtagPreconditionRequest? CapturedRequest { get; private set; }

        public RelationalDeleteEtagPreconditionCheckResult? ResultToReturn { get; set; }

        public RelationalDeleteEtagPreconditionCheckResult Evaluate(
            MappingSet mappingSet,
            RelationalWriteTargetContext.ExistingDocument lockedTargetContext,
            WritePrecondition.IfMatch precondition
        )
        {
            ArgumentNullException.ThrowIfNull(mappingSet);
            ArgumentNullException.ThrowIfNull(lockedTargetContext);
            ArgumentNullException.ThrowIfNull(precondition);

            CallCount++;
            CapturedRequest = new CapturedDeleteEtagPreconditionRequest(
                mappingSet,
                lockedTargetContext,
                precondition
            );

            return ResultToReturn
                ?? throw new InvalidOperationException(
                    "ResultToReturn must be configured before the delete precondition is evaluated."
                );
        }
    }

    private sealed class RecordingWriteSessionFactory(IRelationalCommandExecutor commandExecutor)
        : IRelationalWriteSessionFactory
    {
        public RecordingWriteSession Session { get; } = new(commandExecutor);

        public int CreateAsyncCallCount { get; private set; }

        public Exception? CreateAsyncExceptionToThrow { get; set; }

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            CreateAsyncCallCount++;
            if (CreateAsyncExceptionToThrow is not null)
            {
                throw CreateAsyncExceptionToThrow;
            }
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingWriteSession(IRelationalCommandExecutor commandExecutor)
        : IRelationalWriteSession
    {
        private readonly IRelationalCommandExecutor _commandExecutor = commandExecutor;

        public IRelationalCommandExecutor CreateCommandExecutor() => _commandExecutor;

        public DbConnection Connection =>
            throw new InvalidOperationException(
                "RecordingWriteSession exposes the executor via CreateCommandExecutor; Connection is not used in tests."
            );

        public DbTransaction Transaction =>
            throw new InvalidOperationException(
                "RecordingWriteSession exposes the executor via CreateCommandExecutor; Transaction is not used in tests."
            );

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public Exception? CommitExceptionToThrow { get; set; }

        /// <summary>
        /// Every <see cref="RelationalCommand"/> the session was asked to create, in order. The relational
        /// DELETE composes its statements into raw commands rather than going through the command executor,
        /// so this is where its command stream is observed.
        /// </summary>
        public List<RelationalCommand> Commands { get; } = [];

        /// <summary>
        /// Answers a raw command with a reader or an exception. Delete fixtures install
        /// <see cref="DeleteCommandResponder"/>; every other path still uses the command executor.
        /// </summary>
        public Func<RelationalCommand, object>? RawCommandResponder { get; set; }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            Commands.Add(command);

            if (RawCommandResponder is null)
            {
                throw new InvalidOperationException(
                    "RecordingWriteSession was asked for a raw DbCommand but no RawCommandResponder is "
                        + "installed; callers that expect the command executor should use CreateCommandExecutor."
                );
            }

            return new ScriptedDbCommand(RawCommandResponder(command));
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCallCount++;

            if (CommitExceptionToThrow is not null)
            {
                throw CommitExceptionToThrow;
            }

            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static void AssertSupportedRelationshipStrategyNames(string message)
    {
        foreach (
            var expectedStrategyName in RelationshipAuthorizationStrategyCatalog.SupportedRelationshipStrategyNames
        )
        {
            message.Should().Contain(expectedStrategyName);
        }
    }

    private static IGetRequest CreateGetRequest(
        DocumentUuid documentUuid,
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? namespacePrefixes = null
    )
    {
        var getRequest = A.Fake<IGetRequest>();
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
        A.CallTo(() => getRequest.TraceId).Returns(new TraceId("get-trace"));
        A.CallTo(() => getRequest.ReadMode).Returns(readMode);
        A.CallTo(() => getRequest.ReadableProfileProjectionContext).Returns(readableProfileProjectionContext);
        A.CallTo(() => getRequest.AuthorizationStrategyEvaluators)
            .Returns(authorizationStrategyEvaluators ?? []);
        A.CallTo(() => getRequest.AuthorizationContext)
            .Returns(
                new RelationalAuthorizationContext(
                    claimEducationOrganizationIds ?? [],
                    namespacePrefixes ?? []
                )
            );

        return getRequest;
    }

    private static RelationshipAuthorizationFailure CreateRelationshipFailure() =>
        new(
            RelationshipAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            FailedStrategies:
            [
                new RelationshipAuthorizationFailedStrategy(
                    ConfiguredStrategyIndex: 0,
                    RelationshipLocalOrder: 0,
                    StrategyName: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    StrategyKind: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                        "auth.EducationOrganizationIdToEducationOrganizationId",
                        "TargetEducationOrganizationId",
                        "SourceEducationOrganizationId"
                    ),
                    FailedSubjects:
                    [
                        new RelationshipAuthorizationFailedSubject(
                            SubjectIndex: 0,
                            FailureKind: RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                            RootBinding: new RelationshipAuthorizationRootBinding(
                                "Ed-Fi.School",
                                "edfi.School",
                                "SchoolId"
                            ),
                            AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                                "auth.EducationOrganizationIdToEducationOrganizationId",
                                "TargetEducationOrganizationId",
                                "SourceEducationOrganizationId"
                            ),
                            SecurableElements:
                            [
                                new RelationshipAuthorizationSecurableElement(
                                    "EducationOrganization",
                                    "$.schoolId",
                                    "SchoolId"
                                ),
                            ],
                            Hint: "No matching relationship authorization row was found for the subject value and claim EducationOrganizationIds."
                        ),
                    ]
                ),
            ],
            ClaimEducationOrganizationIds: [new EducationOrganizationId(255901)]
        );

    private static RelationshipAuthorizationFailure CreateNoClaimsStoredRelationshipFailure() =>
        new(
            RelationshipAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            FailedStrategies:
            [
                new RelationshipAuthorizationFailedStrategy(
                    ConfiguredStrategyIndex: 0,
                    RelationshipLocalOrder: 0,
                    StrategyName: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    StrategyKind: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                        "auth.EducationOrganizationIdToEducationOrganizationId",
                        "TargetEducationOrganizationId",
                        "SourceEducationOrganizationId"
                    ),
                    FailedSubjects:
                    [
                        new RelationshipAuthorizationFailedSubject(
                            SubjectIndex: 0,
                            FailureKind: RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                            RootBinding: new RelationshipAuthorizationRootBinding(
                                "Ed-Fi.School",
                                "edfi.School",
                                "SchoolId"
                            ),
                            AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                                "auth.EducationOrganizationIdToEducationOrganizationId",
                                "TargetEducationOrganizationId",
                                "SourceEducationOrganizationId"
                            ),
                            SecurableElements:
                            [
                                new RelationshipAuthorizationSecurableElement(
                                    "EducationOrganization",
                                    "$.schoolId",
                                    "SchoolId"
                                ),
                            ],
                            Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                        ),
                    ],
                    Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                ),
            ],
            ClaimEducationOrganizationIds: []
        );

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        DocumentUuid documentUuid,
        long documentId,
        long contentVersion,
        DateTimeOffset? contentLastModifiedAt = null,
        short resourceKeyId = 1
    )
    {
        var lastModifiedAt =
            contentLastModifiedAt ?? new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.FromHours(-5));

        return new DocumentMetadataRow(
            documentId,
            documentUuid.Value,
            contentVersion,
            contentVersion,
            lastModifiedAt,
            lastModifiedAt,
            resourceKeyId
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
            AllowIdentityUpdates: false
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

    private static MappingSet CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(
        ResourceInfo resourceInfo,
        SqlDialect dialect = SqlDialect.Pgsql
    )
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
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.schoolId", []),
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
        var rootPlan = new TableWritePlan(
            rootTable,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SchoolId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(rootTable.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
                new WriteColumnBinding(
                    rootTable.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int64)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    rootTable.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            ),
        };
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var readPlan = CreateReadPlan(resourceModel, rootTable);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: CreateDerivedModelSet(concreteResourceModel),
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

    private static MappingSet CreateNamespaceWriteMappingSet(
        ResourceInfo resourceInfo,
        SqlDialect dialect = SqlDialect.Pgsql
    )
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
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 306),
                    false,
                    new JsonPathExpression("$.namespace", []),
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
        var rootPlan = new TableWritePlan(
            rootTable,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Namespace)",
            UpdateSql: "update edfi.\"School\" set \"Namespace\" = @Namespace where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(rootTable.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
                new WriteColumnBinding(
                    rootTable.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.namespace", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 306)
                    ),
                    "Namespace"
                ),
            ],
            KeyUnificationPlans: []
        );
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements([], ["$.namespace"], [], [], []),
        };
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var readPlan = CreateReadPlan(resourceModel, rootTable);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: CreateDerivedModelSet(concreteResourceModel),
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

    private static MappingSet CreateAuthorizationAwareMappingSetWithSelfStudentSubject(
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
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements([], [], ["$.studentUniqueId"], [], []),
        };
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var readPlan = CreateReadPlan(resourceModel, rootPlan.TableModel);
        var resource = resourceKey.Resource;

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: CreateDerivedModelSet(concreteResourceModel),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [resource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        )
        {
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
    }

    private static MappingSet CreateWriteAuthorizationAwareMappingSetWithSelfStudentSubjectAndPeopleAuthViews()
    {
        var resourceInfo = _studentResourceInfo;
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootPlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootPlan.TableModel,
            ResourceStorageKind.RelationalTables
        );
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements([], [], ["$.studentUniqueId"], [], []),
        };
        var concreteResources = new List<ConcreteResourceModel> { concreteResourceModel };
        concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources(2));
        var resourceKeyIdByResource = concreteResources.ToDictionary(
            static concreteResource => concreteResource.ResourceKey.Resource,
            static concreteResource => concreteResource.ResourceKey.ResourceKeyId
        );
        var resourceKeyById = concreteResources.ToDictionary(
            static concreteResource => concreteResource.ResourceKey.ResourceKeyId,
            static concreteResource => concreteResource.ResourceKey
        );
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var readPlan = CreateReadPlan(resourceModel, rootPlan.TableModel);
        var resource = resourceKey.Resource;

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(concreteResources) with
            {
                AuthEdOrgHierarchy = CreateAuthEdOrgHierarchy(),
            },
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [resource] = readPlan,
            },
            ResourceKeyIdByResource: resourceKeyIdByResource,
            ResourceKeyById: resourceKeyById,
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static MappingSet CreateQuerySupportedMappingSetWithRootEdOrgAndSelfStudentSubject(
        ResourceInfo resourceInfo
    )
    {
        const string schoolPath = "$.schoolReference.schoolId";
        const string studentPath = "$.studentUniqueId";
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = CreateRootTableModel(
            resourceInfo.ResourceName.Value,
            [
                new DbColumnModel(
                    AuthNames.SchoolIdUnified,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression(schoolPath, []),
                    null,
                    new ColumnStorage.Stored()
                ),
            ]
        );
        var resourceModel = new RelationalResourceModel(
            resourceKey.Resource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );
        var concreteResource = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement(schoolPath, "SchoolId")],
                [],
                [studentPath],
                [],
                []
            ),
        };
        var concreteResources = new List<ConcreteResourceModel> { concreteResource };
        concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources(2));
        var readPlan = CreateReadPlan(resourceModel, rootTable);
        var resourceKeyIdByResource = concreteResources.ToDictionary(
            static concreteResourceModel => concreteResourceModel.ResourceKey.Resource,
            static concreteResourceModel => concreteResourceModel.ResourceKey.ResourceKeyId
        );
        var resourceKeyById = concreteResources.ToDictionary(
            static concreteResourceModel => concreteResourceModel.ResourceKey.ResourceKeyId,
            static concreteResourceModel => concreteResourceModel.ResourceKey
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(concreteResources) with
            {
                AuthEdOrgHierarchy = CreateAuthEdOrgHierarchy(),
            },
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [resourceKey.Resource] = readPlan,
            },
            ResourceKeyIdByResource: resourceKeyIdByResource,
            ResourceKeyById: resourceKeyById,
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        )
        {
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resourceKey.Resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
    }

    private static MappingSet CreateQuerySupportedMappingSetWithUnresolvedStudentSubject(
        ResourceInfo resourceInfo,
        string studentPath
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = CreateRootTableModel(resourceInfo.ResourceName.Value);
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements([], [], [studentPath], [], [])
        );
    }

    private static MappingSet CreateQuerySupportedMappingSetWithChildOnlyStudentSubject(
        ResourceInfo resourceInfo,
        string studentPath
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = CreateRootTableModel(resourceInfo.ResourceName.Value);
        var childTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), $"{resourceInfo.ResourceName.Value}StudentReference"),
            new JsonPathExpression(
                "$.studentReferences[*]",
                [new JsonPathSegment.Property("studentReferences"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                $"PK_{resourceInfo.ResourceName.Value}StudentReference",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionStudent_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Student")
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };
        var resourceModel = new RelationalResourceModel(
            resourceKey.Resource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable],
            [
                new DocumentReferenceBinding(
                    true,
                    new JsonPathExpression(
                        "$.studentReferences[*].studentReference",
                        [
                            new JsonPathSegment.Property("studentReferences"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("studentReference"),
                        ]
                    ),
                    childTable.Table,
                    new DbColumnName("CollectionStudent_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            new JsonPathExpression(studentPath, []),
                            new JsonPathExpression(studentPath, []),
                            new DbColumnName("CollectionStudentUniqueId")
                        ),
                    ]
                ),
            ],
            []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements([], [], [studentPath], [], [])
        );
    }

    private static MappingSet CreateNamespaceAndRelationshipWriteMappingSet(
        ResourceInfo resourceInfo,
        SqlDialect dialect = SqlDialect.Pgsql
    )
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
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.schoolId", []),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 306),
                    false,
                    new JsonPathExpression("$.namespace", []),
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
        var rootPlan = new TableWritePlan(
            rootTable,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SchoolId, @Namespace)",
            UpdateSql: "update edfi.\"School\" set \"Namespace\" = @Namespace where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(rootTable.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
                new WriteColumnBinding(
                    rootTable.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int64)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    rootTable.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.namespace", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 306)
                    ),
                    "Namespace"
                ),
            ],
            KeyUnificationPlans: []
        );
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.schoolId", "SchoolId")],
                ["$.namespace"],
                [],
                [],
                []
            ),
        };
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var readPlan = CreateReadPlan(resourceModel, rootTable);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: CreateDerivedModelSet(concreteResourceModel),
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

    private static MappingSet CreateQuerySupportedMappingSetWithRootEdOrgSubject(ResourceInfo resourceInfo)
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
                    new DbColumnName("LocalEducationAgencyId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.localEducationAgencyId", []),
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

        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.localEducationAgencyId", "LocalEducationAgencyId")],
                [],
                [],
                [],
                []
            )
        );
    }

    private static MappingSet CreateQuerySupportedMappingSetWithCustomViewBasisButNoJoinPath(
        ResourceInfo resourceInfo
    ) =>
        WithUnreachableStudentBasis(
            resourceInfo,
            CreateQuerySupportedMappingSetWithRootEdOrgSubject(resourceInfo)
        );

    /// <summary>
    /// The write-path counterpart: the same unreachable Student basis over a mapping set that also carries a
    /// write plan, so a POST/PUT reaches custom-view planning instead of failing to resolve one.
    /// </summary>
    private static MappingSet CreateWriteAwareMappingSetWithCustomViewBasisButNoJoinPath(
        ResourceInfo resourceInfo
    ) =>
        WithUnreachableStudentBasis(
            resourceInfo,
            CreateWriteAuthorizationAwareMappingSetWithRootEdOrgSubject(resourceInfo)
        );

    /// <summary>
    /// Adds a minimal Student concrete resource so a <c>StudentWith…</c> basis resolves but has no DocumentId
    /// join path from the subject, which is what makes the planner report a no-join-path failure rather than
    /// an unknown strategy.
    /// </summary>
    private static MappingSet WithUnreachableStudentBasis(ResourceInfo resourceInfo, MappingSet mappingSet)
    {
        var resource = new QualifiedResourceName(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value
        );
        var concreteResources = mappingSet.Model.ConcreteResourcesInNameOrder.ToList();

        if (
            !concreteResources.Exists(static concreteResource =>
                concreteResource.ResourceKey.Resource == new QualifiedResourceName("Ed-Fi", "Student")
            )
        )
        {
            concreteResources.Add(CreateMinimalConcreteResource(99, "Student"));
        }

        return mappingSet with
        {
            Model = mappingSet.Model with
            {
                EffectiveSchema = mappingSet.Model.EffectiveSchema with
                {
                    ResourceKeyCount = (short)concreteResources.Count,
                    ResourceKeysInIdOrder =
                    [
                        .. concreteResources
                            .OrderBy(static concreteResource => concreteResource.ResourceKey.ResourceKeyId)
                            .Select(static concreteResource => concreteResource.ResourceKey),
                    ],
                },
                ConcreteResourcesInNameOrder =
                [
                    .. concreteResources
                        .OrderBy(
                            static concreteResource => concreteResource.ResourceKey.Resource.ProjectName,
                            StringComparer.Ordinal
                        )
                        .ThenBy(
                            static concreteResource => concreteResource.ResourceKey.Resource.ResourceName,
                            StringComparer.Ordinal
                        ),
                ],
            },
            ResourceKeyIdByResource = concreteResources.ToDictionary(
                static concreteResource => concreteResource.ResourceKey.Resource,
                static concreteResource => concreteResource.ResourceKey.ResourceKeyId
            ),
            ResourceKeyById = concreteResources.ToDictionary(
                static concreteResource => concreteResource.ResourceKey.ResourceKeyId,
                static concreteResource => concreteResource.ResourceKey
            ),
            SecurableElementColumnPathsByResource = new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >
            {
                [resource] = [],
            },
        };
    }

    private static MappingSet CreateQuerySupportedMappingSetWithDuplicatePhysicalRootEdOrgSubjects(
        ResourceInfo resourceInfo
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "TestResource"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_TestResource",
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
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.schoolId", []),
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

        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.schoolId", "SchoolReferenceSchoolId"),
                ],
                [],
                [],
                [],
                []
            )
        );
    }

    private static MappingSet CreateQuerySupportedMappingSetWithSameNameRootEdOrgSubjects(
        ResourceInfo resourceInfo
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "CourseOffering"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_CourseOffering",
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
                    new DbColumnName("CourseOffering_SchoolReferenceSchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.schoolReference.schoolId", []),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CourseOffering_SessionReferenceSchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.sessionReference.schoolId", []),
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

        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements(
                [
                    new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                    new EdOrgSecurableElement("$.sessionReference.schoolId", "SchoolId"),
                ],
                [],
                [],
                [],
                []
            )
        );
    }

    private static MappingSet CreateQuerySupportedMappingSetWithChildOnlyEdOrgSubject(
        ResourceInfo resourceInfo
    )
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

        var childTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolClassPeriod"),
            new JsonPathExpression(
                "$.classPeriods[*]",
                [new JsonPathSegment.Property("classPeriods"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolClassPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("ClassPeriod_DocumentId"),
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "ClassPeriod")
                ),
                new DbColumnModel(
                    new DbColumnName("ClassPeriod_SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
            ],
            []
        );

        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    true,
                    new JsonPathExpression(
                        "$.classPeriods[*].classPeriodReference",
                        [
                            new JsonPathSegment.Property("classPeriods"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("classPeriodReference"),
                        ]
                    ),
                    childTable.Table,
                    new DbColumnName("ClassPeriod_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "ClassPeriod"),
                    [
                        new ReferenceIdentityBinding(
                            new JsonPathExpression("$.schoolReference.schoolId", []),
                            new JsonPathExpression("$.classPeriods[*].classPeriodReference.schoolId", []),
                            new DbColumnName("ClassPeriod_SchoolId")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.classPeriods[*].classPeriodReference.schoolId", "SchoolId")],
                [],
                [],
                [],
                []
            )
        );
    }

    private static MappingSet CreateNamespaceAuthorizationMappingSet(ResourceInfo resourceInfo)
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
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 306),
                    false,
                    new JsonPathExpression("$.namespace", []),
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

        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements([], ["$.namespace"], [], [], [])
        );
    }

    private static MappingSet CreateNamespaceAndRootEdOrgMappingSet(ResourceInfo resourceInfo)
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
                    new DbColumnName("LocalEducationAgencyId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.localEducationAgencyId", []),
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 306),
                    false,
                    new JsonPathExpression("$.namespace", []),
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

        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return CreateAuthorizationAwareQuerySupportedMappingSet(
            resourceInfo,
            resourceModel,
            new ResourceSecurableElements(
                [new EdOrgSecurableElement("$.localEducationAgencyId", "LocalEducationAgencyId")],
                ["$.namespace"],
                [],
                [],
                []
            )
        );
    }

    private static MappingSet CreateAuthorizationAwareQuerySupportedMappingSet(
        ResourceInfo resourceInfo,
        RelationalResourceModel resourceModel,
        ResourceSecurableElements securableElements
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var concreteResourceModel = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = securableElements,
        };
        var readPlan = CreateReadPlan(resourceModel, resourceModel.Root);
        var resource = resourceKey.Resource;

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(concreteResourceModel),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [resource] = readPlan,
            },
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        )
        {
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
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

    private static MappingSet CreateChangeVersionQuerySupportedMappingSet(ResourceInfo resourceInfo)
    {
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var rootTable = CreateRootTableModel(
            resourceInfo.ResourceName.Value,
            [
                new DbColumnModel(
                    new DbColumnName("ContentVersion"),
                    ColumnKind.MirroredContentVersion,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ]
        );
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
        )
        {
            QueryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>
            {
                [resourceKey.Resource] = new RelationalQueryCapability(
                    new RelationalQuerySupport.Supported(),
                    new Dictionary<string, SupportedRelationalQueryField>(StringComparer.OrdinalIgnoreCase),
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

    private static IQueryRequest CreateQueryRequest(
        MappingSet mappingSet,
        QueryElement[] queryElements,
        bool totalCount,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        ResourceInfo? resourceInfo = null,
        IReadOnlyList<string>? namespacePrefixes = null,
        ChangeVersionRange? changeVersionRange = null,
        PaginationParameters? paginationParameters = null
    )
    {
        authorizationStrategyEvaluators ??= [];
        claimEducationOrganizationIds ??= [];
        resourceInfo ??= _schoolResourceInfo;
        changeVersionRange ??= ChangeVersionRange.None;
        paginationParameters ??= new PaginationParameters(
            Limit: 25,
            Offset: 0,
            TotalCount: totalCount,
            MaximumPageSize: 500
        );

        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => queryRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => queryRequest.QueryElements).Returns(queryElements);
        A.CallTo(() => queryRequest.AuthorizationContext)
            .Returns(
                new RelationalAuthorizationContext(claimEducationOrganizationIds, namespacePrefixes ?? [])
            );
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns(authorizationStrategyEvaluators);
        A.CallTo(() => queryRequest.Paging).Returns(new CollectionPaging.Traditional(paginationParameters));
        A.CallTo(() => queryRequest.TraceId).Returns(new TraceId("query-trace"));
        A.CallTo(() => queryRequest.ReadableProfileProjectionContext)
            .Returns(readableProfileProjectionContext);
        A.CallTo(() => queryRequest.ChangeVersionRange).Returns(changeVersionRange);
        return queryRequest;
    }

    private static object? ParameterValue(RelationalCommand command, string parameterName) =>
        command.Parameters.Single(parameter => parameter.Name == parameterName).Value;

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
    ) =>
        CreateDerivedModelSet(
            new ConcreteResourceModel(resourceKey, resourceModel.StorageKind, resourceModel)
        );

    private static DerivedRelationalModelSet CreateDerivedModelSet(
        ConcreteResourceModel concreteResourceModel
    )
    {
        var resourceKey = concreteResourceModel.ResourceKey;

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
            ConcreteResourcesInNameOrder: [concreteResourceModel],
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

    // --- Restored from origin/main (DMS-1095 helpers needed by people-relationship tests) ---

    private static readonly ResourceInfo _studentResourceInfo = CreateResourceInfo("Student");

    private static MappingSet CreateWriteAuthorizationAwareMappingSetWithDirectStudentSubject(
        ResourceInfo resourceInfo
    ) =>
        CreateWriteAuthorizationAwareMappingSetWithUnboundStudentSubject(
            resourceInfo,
            includeStudentDocumentIdBinding: true
        );

    private static MappingSet CreateWriteAuthorizationAwareMappingSetWithUnboundStudentSubject(
        ResourceInfo resourceInfo,
        bool includeStudentDocumentIdBinding = false
    )
    {
        const string studentPath = "$.studentReference.studentUniqueId";
        var resourceKey = CreateResourceKeyEntry(resourceInfo);
        var studentDocumentIdColumn = AuthNames.StudentDocumentId;
        var studentUniqueIdColumn = new DbColumnName("StudentUniqueId");
        var rootTable = CreateRootTableModel(
            resourceInfo.ResourceName.Value,
            [
                new DbColumnModel(
                    studentDocumentIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    new JsonPathExpression("$.studentReference", []),
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    studentUniqueIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    false,
                    new JsonPathExpression(studentPath, []),
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
            ]
        );
        var resourceModel = new RelationalResourceModel(
            resourceKey.Resource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [
                new DocumentReferenceBinding(
                    true,
                    new JsonPathExpression("$.studentReference", []),
                    rootTable.Table,
                    studentDocumentIdColumn,
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [
                        new ReferenceIdentityBinding(
                            new JsonPathExpression(studentPath, []),
                            new JsonPathExpression(studentPath, []),
                            studentUniqueIdColumn
                        ),
                    ]
                ),
            ],
            []
        );
        var concreteResource = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            resourceModel
        )
        {
            SecurableElements = new ResourceSecurableElements([], [], [studentPath], [], []),
        };
        var concreteResources = new List<ConcreteResourceModel>
        {
            concreteResource,
            CreateMinimalConcreteResource(2, "Student"),
        };
        concreteResources.AddRange(CreateRequiredPeopleAuthAssociationResources(3));

        var documentIdColumn = rootTable.Columns.Single(static column =>
            column.ColumnName.Value == "DocumentId"
        );
        var studentDocumentIdColumnModel = rootTable.Columns.Single(column =>
            column.ColumnName == studentDocumentIdColumn
        );
        var nameColumn = rootTable.Columns.Single(static column => column.ColumnName.Value == "Name");
        var columnBindings = new List<WriteColumnBinding>
        {
            new(documentIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
        };

        if (includeStudentDocumentIdBinding)
        {
            columnBindings.Add(
                new WriteColumnBinding(
                    studentDocumentIdColumnModel,
                    new WriteValueSource.DocumentReference(0),
                    studentDocumentIdColumn.Value
                )
            );
        }

        columnBindings.Add(
            new WriteColumnBinding(
                nameColumn,
                new WriteValueSource.Scalar(
                    new JsonPathExpression("$.name", []),
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                "Name"
            )
        );

        var writePlan = new ResourceWritePlan(
            resourceModel,
            [
                new TableWritePlan(
                    rootTable,
                    InsertSql: "",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, columnBindings.Count, 1000),
                    ColumnBindings: columnBindings,
                    KeyUnificationPlans: []
                ),
            ]
        );

        var mappingSet = CreateMappingSet(
            resourceKey.Resource,
            concreteResources,
            writePlan,
            new ResourceReadPlan(
                resourceModel,
                KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
                [new TableReadPlan(rootTable, "select 1")],
                [
                    new ReferenceIdentityProjectionTablePlan(
                        rootTable.Table,
                        [
                            new ReferenceIdentityProjectionBinding(
                                IsIdentityComponent: true,
                                ReferenceObjectPath: new JsonPathExpression("$.studentReference", []),
                                TargetResource: new QualifiedResourceName("Ed-Fi", "Student"),
                                FkColumnOrdinal: 1,
                                IdentityFieldOrdinalsInOrder:
                                [
                                    new ReferenceIdentityProjectionFieldOrdinal(
                                        new JsonPathExpression(studentPath, []),
                                        ColumnOrdinal: 2,
                                        ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32)
                                    ),
                                ]
                            ),
                        ]
                    ),
                ],
                [],
                new DocumentReferenceLookupPlan(
                    SelectByKeysetSql: "select 1",
                    ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
                    SourcesInOrder:
                    [
                        new DocumentReferenceLookupSource(rootTable.Table, studentDocumentIdColumn),
                    ]
                )
            )
        );

        return mappingSet with
        {
            Model = mappingSet.Model with { AuthEdOrgHierarchy = CreateAuthEdOrgHierarchy() },
        };
    }

    private static DbTableModel CreateRootTableModel(
        string tableName,
        IReadOnlyList<DbColumnModel>? columns = null
    )
    {
        var documentIdColumn = new DbColumnModel(
            new DbColumnName("DocumentId"),
            ColumnKind.ParentKeyPart,
            new RelationalScalarType(ScalarKind.Int64),
            false,
            null,
            null,
            new ColumnStorage.Stored()
        );

        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [documentIdColumn, .. (columns ?? [])],
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
    }

    private static ConcreteResourceModel CreateMinimalConcreteResource(
        short resourceKeyId,
        string resourceName
    )
    {
        var resourceKey = CreateResourceKeyEntry(resourceKeyId, resourceName);
        var rootTable = CreateRootTableModel(resourceName);
        var resourceModel = CreateRelationalResourceModel(
            resourceKey,
            rootTable,
            ResourceStorageKind.RelationalTables
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, resourceModel)
        {
            SecurableElements = ResourceSecurableElements.Empty,
        };
    }

    private static IReadOnlyList<ConcreteResourceModel> CreateRequiredPeopleAuthAssociationResources(
        short firstResourceKeyId
    ) =>
        [
            .. AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames.Select(
                (resourceName, index) =>
                    CreateMinimalConcreteResource((short)(firstResourceKeyId + index), resourceName)
            ),
        ];

    private static AuthEdOrgHierarchy CreateAuthEdOrgHierarchy() =>
        new([
            new AuthEdOrgEntity(
                "School",
                new DbTableName(new DbSchemaName("edfi"), "School"),
                new DbColumnName("SchoolId"),
                []
            ),
        ]);

    private static DerivedRelationalModelSet CreateDerivedModelSet(
        IReadOnlyList<ConcreteResourceModel> concreteResourceModels
    )
    {
        var resourceKeys = concreteResourceModels
            .Select(static concreteResource => concreteResource.ResourceKey)
            .OrderBy(static resourceKey => resourceKey.ResourceKeyId)
            .ToArray();

        return new DerivedRelationalModelSet(
            EffectiveSchema: new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: "schema-hash",
                ResourceKeyCount: (short)resourceKeys.Length,
                ResourceKeySeedHash: [1, 2, 3],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                ],
                ResourceKeysInIdOrder: resourceKeys
            ),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
            ],
            ConcreteResourcesInNameOrder:
            [
                .. concreteResourceModels
                    .OrderBy(
                        static concreteResource => concreteResource.ResourceKey.Resource.ProjectName,
                        StringComparer.Ordinal
                    )
                    .ThenBy(
                        static concreteResource => concreteResource.ResourceKey.Resource.ResourceName,
                        StringComparer.Ordinal
                    ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );
    }

    private static ResourceKeyEntry CreateResourceKeyEntry(short resourceKeyId, string resourceName) =>
        new(resourceKeyId, new QualifiedResourceName("Ed-Fi", resourceName), "1.0.0", false);

    private static MappingSet CreateMappingSet(
        QualifiedResourceName requestResource,
        IReadOnlyList<ConcreteResourceModel> concreteResources,
        ResourceWritePlan writePlan,
        ResourceReadPlan readPlan
    )
    {
        var resourceKeyIdByResource = concreteResources.ToDictionary(
            static concreteResource => concreteResource.ResourceKey.Resource,
            static concreteResource => concreteResource.ResourceKey.ResourceKeyId
        );
        var resourceKeyById = concreteResources.ToDictionary(
            static concreteResource => concreteResource.ResourceKey.ResourceKeyId,
            static concreteResource => concreteResource.ResourceKey
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: CreateDerivedModelSet(concreteResources),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [requestResource] = writePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [requestResource] = readPlan,
            },
            ResourceKeyIdByResource: resourceKeyIdByResource,
            ResourceKeyById: resourceKeyById,
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }
}
