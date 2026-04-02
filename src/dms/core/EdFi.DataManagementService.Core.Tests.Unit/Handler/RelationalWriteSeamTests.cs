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
    private RelationalWriteSeamFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = RelationalWriteSeamFixture.Create();
    }

    private static RelationalWriteTargetLookupResult CreateCreateNewLookupResult(DocumentUuid documentUuid) =>
        new RelationalWriteTargetLookupResult.CreateNew(documentUuid);

    private static RelationalWriteTargetLookupResult CreateExistingDocumentLookupResult(
        long documentId,
        DocumentUuid documentUuid,
        long observedContentVersion = 0L
    ) =>
        new RelationalWriteTargetLookupResult.ExistingDocument(
            documentId,
            documentUuid,
            observedContentVersion
        );

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_routes_post_requests_through_the_relational_seam_for_both_dialects(
        SqlDialect dialect
    )
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: documentUuid => CreateExistingDocumentLookupResult(345L, documentUuid),
            writeResultFactory: request => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(
                    ((RelationalWriteTargetContext.CreateNew)request.TargetContext).DocumentUuid
                )
            )
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(dialect),
            _fixture.CreateDocumentInfo()
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(201);
        harness.WriteExecutor.Requests.Should().ContainSingle();

        var request = harness.WriteExecutor.Requests.Single();
        request.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        request.SelectedBody.Should().BeSameAs(requestInfo.ParsedBody);
        request.TargetContext.Should().BeOfType<RelationalWriteTargetContext.CreateNew>();
        request.ReadPlan.Should().BeNull();
        request
            .ReferenceResolutionRequest.RequestResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "Student"));
        request.ReferenceResolutionRequest.DocumentReferences.Should().HaveCount(3);
        request.ReferenceResolutionRequest.DescriptorReferences.Should().ContainSingle();

        var createdDocumentUuid = (
            (RelationalWriteTargetContext.CreateNew)request.TargetContext
        ).DocumentUuid;
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
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: _ => CreateExistingDocumentLookupResult(345L, existingDocumentUuid, 18L),
            writeResultFactory: request => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateSuccess(
                    ((RelationalWriteTargetContext.ExistingDocument)request.TargetContext).DocumentUuid
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
            .TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 18L)
            );
        request.ReadPlan.Should().NotBeNull();
        request.ReferenceResolutionRequest.DocumentReferences.Should().HaveCount(3);
        request.ReferenceResolutionRequest.DescriptorReferences.Should().ContainSingle();
        requestInfo
            .FrontendResponse.LocationHeaderPath.Should()
            .Be($"/ed-fi/students/{existingDocumentUuid.Value}");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_short_circuits_missing_put_targets_to_not_found_for_both_dialects(SqlDialect dialect)
    {
        var requestedDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: _ => new RelationalWriteTargetLookupResult.NotFound(),
            writeResultFactory: _ => throw new AssertionException("Write executor should not be called.")
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
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: documentUuid => CreateExistingDocumentLookupResult(345L, documentUuid),
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
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: documentUuid => CreateExistingDocumentLookupResult(345L, documentUuid),
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
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: documentUuid => CreateExistingDocumentLookupResult(345L, documentUuid),
            writeResultFactory: request => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.InsertSuccess(
                    ((RelationalWriteTargetContext.CreateNew)request.TargetContext).DocumentUuid
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
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: documentUuid => CreateExistingDocumentLookupResult(345L, documentUuid),
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
            postTargetLookupFactory: CreateCreateNewLookupResult,
            putTargetLookupFactory: documentUuid => CreateExistingDocumentLookupResult(345L, documentUuid),
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
            Func<DocumentUuid, RelationalWriteTargetLookupResult> postTargetLookupFactory,
            Func<DocumentUuid, RelationalWriteTargetLookupResult> putTargetLookupFactory,
            Func<RelationalWriteExecutorRequest, RelationalWriteExecutorResult> writeResultFactory
        )
        {
            var targetLookupResolver = A.Fake<IRelationalWriteTargetLookupResolver>();
            A.CallTo(() =>
                    targetLookupResolver.ResolveForPostAsync(
                        A<MappingSet>._,
                        A<QualifiedResourceName>._,
                        A<ReferentialId>._,
                        A<DocumentUuid>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(call =>
                    Task.FromResult(postTargetLookupFactory(call.GetArgument<DocumentUuid>(3)))
                );
            A.CallTo(() =>
                    targetLookupResolver.ResolveForPutAsync(
                        A<MappingSet>._,
                        A<QualifiedResourceName>._,
                        A<DocumentUuid>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(call =>
                    Task.FromResult(putTargetLookupFactory(call.GetArgument<DocumentUuid>(2)))
                );

            var writeExecutor = new CapturingWriteExecutor(writeResultFactory);
            var repository = new RelationalDocumentStoreRepository(
                NullLogger<RelationalDocumentStoreRepository>.Instance,
                targetLookupResolver,
                writeExecutor
            );

            return new RelationalWriteSeamHarness(resourceInfo, repository, writeExecutor);
        }

        public async Task<RequestInfo> ExecuteUpsertAsync(
            JsonNode parsedBody,
            MappingSet? mappingSet,
            DocumentInfo documentInfo,
            string? originalBody = null
        )
        {
            var requestInfo = CreateRequestInfo(
                RequestMethod.POST,
                parsedBody,
                mappingSet,
                documentInfo,
                No.DocumentUuid,
                originalBody
            );
            await _upsertHandler.Execute(requestInfo, NullNext);
            return requestInfo;
        }

        public async Task<RequestInfo> ExecuteUpdateAsync(
            JsonNode parsedBody,
            MappingSet? mappingSet,
            DocumentInfo documentInfo,
            DocumentUuid documentUuid,
            string? originalBody = null
        )
        {
            var requestInfo = CreateRequestInfo(
                RequestMethod.PUT,
                parsedBody,
                mappingSet,
                documentInfo,
                documentUuid,
                originalBody
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
            string? originalBody
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
