// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Seam
{
    private const string ProfilePersistPendingMessage =
        "Profile-aware relational merge/persist pending DMS-1124.";

    private RelationalWriteSeamFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = RelationalWriteSeamFixture.Create();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_routes_post_requests_through_the_relational_seam_for_both_dialects(
        SqlDialect dialect
    )
    {
        var documentInfo = _fixture.CreateDocumentInfo();
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: request => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(
                    ((RelationalWriteTargetRequest.Post)request.TargetRequest).CandidateDocumentUuid
                )
            )
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(dialect),
            documentInfo
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(201);
        harness.WriteExecutor.Requests.Should().ContainSingle();

        var request = harness.WriteExecutor.Requests.Single();
        var createdDocumentUuid = (
            (RelationalWriteTargetRequest.Post)request.TargetRequest
        ).CandidateDocumentUuid;
        request.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        request.SelectedBody.Should().BeSameAs(requestInfo.ParsedBody);
        request
            .TargetRequest.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetRequest.Post(
                    documentInfo.ReferentialId,
                    ((RelationalWriteTargetRequest.Post)request.TargetRequest).CandidateDocumentUuid
                )
            );
        request
            .TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.CreateNew(createdDocumentUuid));
        request.ExistingDocumentReadPlan.Should().NotBeNull();
        request
            .ReferenceResolutionRequest.RequestResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "Student"));
        request.ReferenceResolutionRequest.DocumentReferences.Should().HaveCount(3);
        request.ReferenceResolutionRequest.DescriptorReferences.Should().ContainSingle();

        requestInfo
            .FrontendResponse.LocationHeaderPath.Should()
            .Be($"/ed-fi/students/{createdDocumentUuid.Value}");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_routes_put_requests_through_the_relational_seam_for_both_dialects(SqlDialect dialect)
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-cccccccccccc"));
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: request => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(
                    ((RelationalWriteTargetRequest.Put)request.TargetRequest).DocumentUuid
                )
            )
        );

        var requestInfo = await harness.ExecuteUpdateAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(dialect),
            _fixture.CreateDocumentInfo(),
            existingDocumentUuid
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(204);
        harness.WriteExecutor.Requests.Should().ContainSingle();

        var request = harness.WriteExecutor.Requests.Single();
        request.OperationKind.Should().Be(RelationalWriteOperationKind.Put);
        request
            .TargetRequest.Should()
            .BeEquivalentTo(new RelationalWriteTargetRequest.Put(existingDocumentUuid));
        request
            .TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
            );
        request.ExistingDocumentReadPlan.Should().NotBeNull();
        request.ReferenceResolutionRequest.DocumentReferences.Should().HaveCount(3);
        request.ReferenceResolutionRequest.DescriptorReferences.Should().ContainSingle();
        requestInfo
            .FrontendResponse.LocationHeaderPath.Should()
            .Be($"/ed-fi/students/{existingDocumentUuid.Value}");
    }

    [Test]
    public async Task It_maps_profiled_post_requests_to_the_executor_level_fenced_500_response()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: _ => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UnknownFailure(ProfilePersistPendingMessage)
            )
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo(),
            backendProfileWriteContext: CreateBackendProfileWriteContext(
                RelationalWriteSeamFixture.CreateComplexBody()
            )
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.Body!["error"]!
            .GetValue<string>()
            .Should()
            .Be(ProfilePersistPendingMessage);
        harness.WriteExecutor.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task It_maps_profiled_put_requests_to_the_executor_level_fenced_500_response()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-cccccccccccc"));
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: _ => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UnknownFailure(ProfilePersistPendingMessage)
            )
        );

        var requestInfo = await harness.ExecuteUpdateAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo(),
            existingDocumentUuid,
            backendProfileWriteContext: CreateBackendProfileWriteContext(
                RelationalWriteSeamFixture.CreateComplexBody()
            )
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.Body!["error"]!
            .GetValue<string>()
            .Should()
            .Be(ProfilePersistPendingMessage);
        harness.WriteExecutor.Requests.Should().ContainSingle();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_short_circuits_missing_put_targets_to_not_found_for_both_dialects(SqlDialect dialect)
    {
        var requestedDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            targetLookupResultFactory: _ => new RelationalWriteTargetLookupResult.NotFound(),
            writeResultFactory: _ => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureNotExists()
            )
        );

        var requestInfo = await harness.ExecuteUpdateAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(dialect),
            _fixture.CreateDocumentInfo(),
            requestedDocumentUuid
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        requestInfo.FrontendResponse.Body!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Resource to update was not found");
        harness.WriteExecutor.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_surfaces_executor_owned_reference_failures_through_the_handler()
    {
        var invalidReference = DocumentReferenceFailure.From(
            _fixture.CreateRootSchoolReference(),
            DocumentReferenceFailureReason.Missing
        );
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: _ => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureReference([invalidReference], [])
            )
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            JsonNode.Parse(
                """
                {
                  "schoolYear": 2026,
                  "schoolReference": {
                    "schoolId": 255901
                  }
                }
                """
            )!,
            _fixture.CreateSupportedMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo(
                includeRootSchoolReference: true,
                includeNestedPeriodReferences: false,
                includeProgramTypeDescriptor: false
            )
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(409);
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("$.schoolReference");
        harness.WriteExecutor.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task It_surfaces_executor_owned_validation_failures_through_the_handler()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: _ => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureValidation([
                    new WriteValidationFailure(new JsonPath("$.schoolYear"), "expected scalar kind 'Int32'"),
                ])
            )
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            JsonNode.Parse(
                """
                {
                  "schoolYear": "2026"
                }
                """
            )!,
            _fixture.CreateSupportedMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo(
                includeRootSchoolReference: false,
                includeNestedPeriodReferences: false,
                includeProgramTypeDescriptor: false
            )
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        requestInfo.FrontendResponse.Body!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        requestInfo.FrontendResponse.Body!["validationErrors"]!["$.schoolYear"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("expected scalar kind 'Int32'");
        harness.WriteExecutor.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task It_treats_the_selected_body_as_the_authoritative_input_at_the_core_seam()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: request => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(
                    ((RelationalWriteTargetRequest.Post)request.TargetRequest).CandidateDocumentUuid
                )
            )
        );
        var selectedBody = RelationalWriteSeamFixture.CreateSelectedAuthoritativeBody();

        await harness.ExecuteUpsertAsync(
            selectedBody,
            _fixture.CreateSupportedMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo(),
            originalBody: RelationalWriteSeamFixture.CreateOriginalBodyJson()
        );

        harness.WriteExecutor.Requests.Should().ContainSingle();
        harness.WriteExecutor.Requests.Single().SelectedBody.Should().BeSameAs(selectedBody);
    }

    [Test]
    public async Task It_surfaces_missing_write_plan_guard_rails_through_the_handler()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: _ => throw new AssertionException("Write executor should not be called.")
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateMissingWritePlanMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo()
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.Body!["error"]!
            .GetValue<string>()
            .Should()
            .Contain("Write plan lookup failed for resource 'Ed-Fi.Student'");
        harness.WriteExecutor.Requests.Should().BeEmpty();
    }

    [Test]
    public void It_keeps_missing_mapping_set_as_a_defensive_invariant_for_direct_handler_calls()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            writeResultFactory: _ => throw new AssertionException("Write executor should not be called.")
        );

        Func<Task> act = async () =>
            _ = await harness.ExecuteUpsertAsync(
                RelationalWriteSeamFixture.CreateComplexBody(),
                null,
                _fixture.CreateDocumentInfo()
            );

        act.Should().ThrowAsync<ArgumentNullException>().Result.Which.ParamName.Should().Be("mappingSet");
    }

    private static BackendProfileWriteContext CreateBackendProfileWriteContext(JsonNode requestBody)
    {
        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: [],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-profile",
            CompiledScopeCatalog: [],
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );
    }

    private sealed class RelationalWriteSeamHarness
    {
        private readonly IPipelineStep _upsertHandler;
        private readonly IPipelineStep _updateHandler;
        private readonly ResourceInfo _resourceInfo;
        private readonly IServiceProvider _serviceProvider;

        private RelationalWriteSeamHarness(
            ResourceInfo resourceInfo,
            IDocumentStoreRepository repository,
            CapturingWriteExecutor writeExecutor
        )
        {
            _resourceInfo = resourceInfo;
            WriteExecutor = writeExecutor;
            _serviceProvider = new RepositoryServiceProvider(repository);
            _upsertHandler = new UpsertHandler(
                NullLogger.Instance,
                ResiliencePipeline.Empty,
                new StaticApiSchemaProvider(),
                new NoAuthorizationServiceFactory()
            );
            _updateHandler = new UpdateByIdHandler(
                NullLogger.Instance,
                ResiliencePipeline.Empty,
                new StaticApiSchemaProvider(),
                new NoAuthorizationServiceFactory()
            );
        }

        public CapturingWriteExecutor WriteExecutor { get; }

        public static RelationalWriteSeamHarness Create(
            ResourceInfo resourceInfo,
            Func<RelationalWriteExecutorRequest, RelationalWriteExecutorResult> writeResultFactory,
            Func<RelationalWriteTargetRequest, RelationalWriteTargetLookupResult>? targetLookupResultFactory =
                null
        )
        {
            var writeExecutor = new CapturingWriteExecutor(writeResultFactory);
            var targetLookupService = new RecordingRelationalWriteTargetLookupService(
                targetLookupResultFactory
            );
            var repository = new RelationalDocumentStoreRepository(
                NullLogger<RelationalDocumentStoreRepository>.Instance,
                writeExecutor,
                targetLookupService,
                new DefaultDescriptorWriteHandler()
            );

            return new RelationalWriteSeamHarness(resourceInfo, repository, writeExecutor);
        }

        public async Task<RequestInfo> ExecuteUpsertAsync(
            JsonNode parsedBody,
            MappingSet? mappingSet,
            DocumentInfo documentInfo,
            string? originalBody = null,
            BackendProfileWriteContext? backendProfileWriteContext = null
        )
        {
            var requestInfo = CreateRequestInfo(
                RequestMethod.POST,
                parsedBody,
                mappingSet,
                documentInfo,
                No.DocumentUuid,
                originalBody,
                backendProfileWriteContext
            );
            await _upsertHandler.Execute(requestInfo, NullNext);
            return requestInfo;
        }

        public async Task<RequestInfo> ExecuteUpdateAsync(
            JsonNode parsedBody,
            MappingSet? mappingSet,
            DocumentInfo documentInfo,
            DocumentUuid documentUuid,
            string? originalBody = null,
            BackendProfileWriteContext? backendProfileWriteContext = null
        )
        {
            var requestInfo = CreateRequestInfo(
                RequestMethod.PUT,
                parsedBody,
                mappingSet,
                documentInfo,
                documentUuid,
                originalBody,
                backendProfileWriteContext
            );
            await _updateHandler.Execute(requestInfo, NullNext);
            return requestInfo;
        }

        private RequestInfo CreateRequestInfo(
            RequestMethod method,
            JsonNode parsedBody,
            MappingSet? mappingSet,
            DocumentInfo documentInfo,
            DocumentUuid documentUuid,
            string? originalBody,
            BackendProfileWriteContext? backendProfileWriteContext
        )
        {
            var frontendRequest = new FrontendRequest(
                Body: originalBody ?? parsedBody.ToJsonString(),
                Form: null,
                Headers: [],
                Path: method == RequestMethod.PUT
                    ? $"/ed-fi/students/{documentUuid.Value}"
                    : "/ed-fi/students",
                QueryParameters: [],
                TraceId: new TraceId("relational-write-seam"),
                RouteQualifiers: []
            );

            return new RequestInfo(frontendRequest, method, _serviceProvider)
            {
                ResourceInfo = _resourceInfo,
                DocumentInfo = documentInfo,
                ParsedBody = parsedBody,
                MappingSet = mappingSet,
                BackendProfileWriteContext = backendProfileWriteContext,
                PathComponents = new PathComponents(
                    new ProjectEndpointName("ed-fi"),
                    new EndpointName("students"),
                    documentUuid
                ),
            };
        }
    }

    private sealed class CapturingWriteExecutor(
        Func<RelationalWriteExecutorRequest, RelationalWriteExecutorResult> resultFactory
    ) : IRelationalWriteExecutor
    {
        public List<RelationalWriteExecutorRequest> Requests { get; } = [];

        public Task<RelationalWriteExecutorResult> ExecuteAsync(
            RelationalWriteExecutorRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            return Task.FromResult(resultFactory(request));
        }
    }

    private sealed class RecordingRelationalWriteTargetLookupService(
        Func<RelationalWriteTargetRequest, RelationalWriteTargetLookupResult>? resultFactory
    ) : IRelationalWriteTargetLookupService
    {
        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                (resultFactory ?? DefaultResultFactory)(
                    new RelationalWriteTargetRequest.Post(referentialId, candidateDocumentUuid)
                )
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

            return Task.FromResult(
                (resultFactory ?? DefaultResultFactory)(new RelationalWriteTargetRequest.Put(documentUuid))
            );
        }

        private static RelationalWriteTargetLookupResult DefaultResultFactory(
            RelationalWriteTargetRequest request
        )
        {
            return request switch
            {
                RelationalWriteTargetRequest.Post(_, var candidateDocumentUuid) =>
                    new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid),
                RelationalWriteTargetRequest.Put(var documentUuid) =>
                    new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
                _ => throw new InvalidOperationException(
                    $"Unsupported target request type '{request.GetType().Name}'."
                ),
            };
        }
    }

    private sealed class RepositoryServiceProvider(IDocumentStoreRepository repository) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IDocumentStoreRepository) ? repository : null;
        }
    }

    private sealed class StaticApiSchemaProvider : IApiSchemaProvider
    {
        private static readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{},\"projectSchemas\":{\"ed-fi\":{\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"Test\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"1.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}}}}"
            ) ?? new JsonObject();

        public ApiSchemaDocumentNodes GetApiSchemaNodes()
        {
            return new ApiSchemaDocumentNodes(_apiSchemaRootNode, []);
        }

        public Guid SchemaLoadId => Guid.Empty;

        public bool IsSchemaValid => true;

        public List<ApiSchemaFailure> ApiSchemaFailures => [];
    }
}
