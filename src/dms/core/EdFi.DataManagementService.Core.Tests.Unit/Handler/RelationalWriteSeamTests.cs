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

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_routes_post_requests_through_the_relational_seam_for_both_dialects(
        SqlDialect dialect
    )
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: _fixture.ResolvedReferences,
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            terminalStageResultFactory: request => new RelationalWriteTerminalStageResult.Upsert(
                new UpsertResult.InsertSuccess(
                    (
                        (RelationalWriteTargetContext.CreateNew)request.FlatteningInput.TargetContext
                    ).DocumentUuid
                )
            )
        );

        var requestInfo = await harness.ExecuteUpsertAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(dialect),
            _fixture.CreateDocumentInfo()
        );

        requestInfo.FrontendResponse.StatusCode.Should().Be(201);
        harness.TerminalStage.Requests.Should().ContainSingle();

        var request = harness.TerminalStage.Requests.Single();
        request.FlatteningInput.OperationKind.Should().Be(RelationalWriteOperationKind.Post);
        request.FlatteningInput.SelectedBody.Should().BeSameAs(requestInfo.ParsedBody);
        request.FlatteningInput.TargetContext.Should().BeOfType<RelationalWriteTargetContext.CreateNew>();
        request
            .FlattenedWriteSet.RootRow.Values.Should()
            .Equal(
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(2026),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(77L)
            );
        request.FlattenedWriteSet.RootRow.NonCollectionRows.Should().ContainSingle();
        request.FlattenedWriteSet.RootRow.CollectionCandidates.Should().ContainSingle();

        var createdDocumentUuid = (
            (RelationalWriteTargetContext.CreateNew)request.FlatteningInput.TargetContext
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
            resolvedReferences: _fixture.ResolvedReferences,
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: _ => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                existingDocumentUuid
            ),
            terminalStageResultFactory: request => new RelationalWriteTerminalStageResult.Update(
                new UpdateResult.UpdateSuccess(
                    (
                        (RelationalWriteTargetContext.ExistingDocument)request.FlatteningInput.TargetContext
                    ).DocumentUuid
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
        harness.TerminalStage.Requests.Should().ContainSingle();

        var request = harness.TerminalStage.Requests.Single();
        request.FlatteningInput.OperationKind.Should().Be(RelationalWriteOperationKind.Put);
        request
            .FlatteningInput.TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid));
        request
            .FlattenedWriteSet.RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(2026),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(77L)
            );
        requestInfo
            .FrontendResponse.LocationHeaderPath.Should()
            .Be($"/ed-fi/students/{existingDocumentUuid.Value}");
    }

    [Test]
    public async Task It_short_circuits_reference_failures_before_terminal_handoff()
    {
        var invalidReference = DocumentReferenceFailure.From(
            _fixture.CreateRootSchoolReference(),
            DocumentReferenceFailureReason.Missing
        );
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: RelationalWriteSeamFixture.CreateReferenceFailureSet(
                invalidDocumentReferences: [invalidReference]
            ),
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            terminalStageResultFactory: _ =>
                throw new AssertionException("Terminal stage should not be called.")
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
        harness.TerminalStage.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_surfaces_missing_resolved_reference_lookups_before_terminal_handoff()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: RelationalWriteSeamFixture.CreateEmptyResolvedReferences(),
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            terminalStageResultFactory: _ =>
                throw new AssertionException("Terminal stage should not be called.")
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

        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.Body!["error"]!
            .GetValue<string>()
            .Should()
            .Contain("resolved lookup set did not contain a matching 'Ed-Fi.School' entry")
            .And.Contain("$.schoolReference");
        harness.TerminalStage.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_treats_the_selected_body_as_the_authoritative_input_at_the_core_seam()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: _fixture.ResolvedReferences,
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            terminalStageResultFactory: request => new RelationalWriteTerminalStageResult.Upsert(
                new UpsertResult.InsertSuccess(
                    (
                        (RelationalWriteTargetContext.CreateNew)request.FlatteningInput.TargetContext
                    ).DocumentUuid
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

        harness.TerminalStage.Requests.Should().ContainSingle();

        var request = harness.TerminalStage.Requests.Single();
        request.FlatteningInput.SelectedBody.Should().BeSameAs(selectedBody);
        request
            .FlattenedWriteSet.RootRow.Values.Should()
            .Equal(
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(2030),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null)
            );
        request.FlattenedWriteSet.RootRow.NonCollectionRows.Should().BeEmpty();
    }

    [Test]
    public async Task It_surfaces_missing_write_plan_guard_rails_through_the_handler()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: _fixture.ResolvedReferences,
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            terminalStageResultFactory: _ =>
                throw new AssertionException("Terminal stage should not be called.")
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
        harness.TerminalStage.Requests.Should().BeEmpty();
    }

    [Test]
    public void It_allows_missing_mapping_set_exceptions_to_escape_the_handler()
    {
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: _fixture.ResolvedReferences,
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.CreateNew(
                documentUuid
            ),
            putTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            terminalStageResultFactory: _ =>
                throw new AssertionException("Terminal stage should not be called.")
        );

        Func<Task> act = async () =>
            _ = await harness.ExecuteUpsertAsync(
                RelationalWriteSeamFixture.CreateComplexBody(),
                null,
                _fixture.CreateDocumentInfo()
            );

        act.Should().ThrowAsync<ArgumentNullException>().Result.Which.ParamName.Should().Be("mappingSet");
    }

    [Test]
    public async Task It_flattens_complex_nested_collections_and_extensions_before_terminal_handoff()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-1111-2222-3333-eeeeeeeeeeee"));
        var harness = RelationalWriteSeamHarness.Create(
            resourceInfo: _fixture.ResourceInfo,
            resolvedReferences: _fixture.ResolvedReferences,
            postTargetContextFactory: documentUuid => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                documentUuid
            ),
            putTargetContextFactory: _ => new RelationalWriteTargetContext.ExistingDocument(
                345L,
                existingDocumentUuid
            ),
            terminalStageResultFactory: request => new RelationalWriteTerminalStageResult.Update(
                new UpdateResult.UpdateSuccess(
                    (
                        (RelationalWriteTargetContext.ExistingDocument)request.FlatteningInput.TargetContext
                    ).DocumentUuid
                )
            )
        );

        await harness.ExecuteUpdateAsync(
            RelationalWriteSeamFixture.CreateComplexBody(),
            _fixture.CreateSupportedMappingSet(SqlDialect.Pgsql),
            _fixture.CreateDocumentInfo(),
            existingDocumentUuid
        );

        harness.TerminalStage.Requests.Should().ContainSingle();

        var request = harness.TerminalStage.Requests.Single();
        var rootRow = request.FlattenedWriteSet.RootRow;
        var rootExtensionRow = rootRow.NonCollectionRows.Single();
        var addressCandidate = rootRow.CollectionCandidates.Single();
        var alignedScope = addressCandidate.AttachedAlignedScopeData.Single();

        rootRow
            .Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(2026),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(77L)
            );

        rootExtensionRow
            .Values.Should()
            .Equal(new FlattenedWriteValue.Literal(345L), new FlattenedWriteValue.Literal("Green"));
        rootExtensionRow.CollectionCandidates.Should().HaveCount(2);
        rootExtensionRow
            .CollectionCandidates[0]
            .Values.Should()
            .Equal(
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Attendance")
            );
        rootExtensionRow.CollectionCandidates[1].SemanticIdentityValues.Should().Equal("Behavior");

        addressCandidate.OrdinalPath.Should().Equal(0);
        addressCandidate
            .Values.Should()
            .Equal(
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Home"),
                new FlattenedWriteValue.Literal("1 Main St")
            );
        addressCandidate.CollectionCandidates.Should().HaveCount(2);
        addressCandidate.CollectionCandidates[0].OrdinalPath.Should().Equal(0, 0);
        addressCandidate
            .CollectionCandidates[0]
            .Values.Should()
            .Equal(
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                new FlattenedWriteValue.Literal(345L),
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal(new DateOnly(2026, 8, 20)),
                new FlattenedWriteValue.Literal(9901L)
            );
        addressCandidate
            .CollectionCandidates[1]
            .Values[5]
            .Should()
            .Be(new FlattenedWriteValue.Literal(9902L));

        alignedScope
            .Values.Should()
            .Equal(
                FlattenedWriteValue.UnresolvedCollectionItemId.Instance,
                new FlattenedWriteValue.Literal("Purple")
            );
        alignedScope.CollectionCandidates.Should().HaveCount(2);
        alignedScope.CollectionCandidates[0].OrdinalPath.Should().Equal(0, 0);
        alignedScope
            .CollectionCandidates.Select(candidate => candidate.SemanticIdentityValues[0])
            .Should()
            .Equal("Bus", "Meal");
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
            CapturingTerminalStage terminalStage
        )
        {
            _resourceInfo = resourceInfo;
            TerminalStage = terminalStage;
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

        public CapturingTerminalStage TerminalStage { get; }

        public static RelationalWriteSeamHarness Create(
            ResourceInfo resourceInfo,
            ResolvedReferenceSet resolvedReferences,
            Func<DocumentUuid, RelationalWriteTargetContext> postTargetContextFactory,
            Func<DocumentUuid, RelationalWriteTargetContext> putTargetContextFactory,
            Func<
                RelationalWriteTerminalStageRequest,
                RelationalWriteTerminalStageResult
            > terminalStageResultFactory
        )
        {
            var targetContextResolver = A.Fake<IRelationalWriteTargetContextResolver>();
            A.CallTo(() =>
                    targetContextResolver.ResolveForPostAsync(
                        A<MappingSet>._,
                        A<QualifiedResourceName>._,
                        A<ReferentialId>._,
                        A<DocumentUuid>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(call =>
                    Task.FromResult(postTargetContextFactory(call.GetArgument<DocumentUuid>(3)))
                );
            A.CallTo(() =>
                    targetContextResolver.ResolveForPutAsync(
                        A<MappingSet>._,
                        A<QualifiedResourceName>._,
                        A<DocumentUuid>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(call =>
                    Task.FromResult(putTargetContextFactory(call.GetArgument<DocumentUuid>(2)))
                );

            var referenceResolver = A.Fake<IReferenceResolver>();
            A.CallTo(() =>
                    referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(resolvedReferences));

            var terminalStage = new CapturingTerminalStage(terminalStageResultFactory);
            var repository = new RelationalDocumentStoreRepository(
                NullLogger<RelationalDocumentStoreRepository>.Instance,
                targetContextResolver,
                referenceResolver,
                new RelationalWriteFlattener(),
                terminalStage
            );

            return new RelationalWriteSeamHarness(resourceInfo, repository, terminalStage);
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

    private sealed class CapturingTerminalStage(
        Func<RelationalWriteTerminalStageRequest, RelationalWriteTerminalStageResult> resultFactory
    ) : IRelationalWriteTerminalStage
    {
        public List<RelationalWriteTerminalStageRequest> Requests { get; } = [];

        public Task<RelationalWriteTerminalStageResult> ExecuteAsync(
            RelationalWriteTerminalStageRequest request,
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
