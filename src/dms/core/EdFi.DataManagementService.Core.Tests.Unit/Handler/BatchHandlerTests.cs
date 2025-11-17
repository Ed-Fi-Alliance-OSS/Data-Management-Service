// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;
using ProjectEndpointNameModel = EdFi.DataManagementService.Core.ApiSchema.Model.ProjectEndpointName;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class BatchHandlerTests
{
    private const string StudentResource = "Student";

    private sealed class TestApiSchemaProvider(ApiSchemaDocumentNodes nodes) : IApiSchemaProvider
    {
        public ApiSchemaDocumentNodes GetApiSchemaNodes() => nodes;

        public Guid ReloadId => Guid.Empty;
        public bool IsSchemaValid => true;
        public List<ApiSchemaFailure> ApiSchemaFailures { get; } = [];

        public Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync() =>
            Task.FromResult(new ApiSchemaLoadStatus(true, []));

        public Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(
            JsonNode coreSchema,
            JsonNode[] extensionSchemas
        ) => Task.FromResult(new ApiSchemaLoadStatus(true, []));
    }

    private sealed class TestBatchUnitOfWorkFactory(Func<TestBatchUnitOfWork> factory)
        : IBatchUnitOfWorkFactory
    {
        public TestBatchUnitOfWork? LastCreated { get; private set; }

        public Task<IBatchUnitOfWork> BeginAsync(TraceId traceId, IReadOnlyDictionary<string, string> headers)
        {
            LastCreated = factory();
            return Task.FromResult<IBatchUnitOfWork>(LastCreated);
        }
    }

    private sealed class TestBatchUnitOfWork : IBatchUnitOfWork
    {
        public Func<IUpsertRequest, Task<UpsertResult>> OnUpsert { get; set; } =
            req => Task.FromResult<UpsertResult>(new UpsertResult.InsertSuccess(req.DocumentUuid));
        public Func<IUpdateRequest, Task<UpdateResult>> OnUpdate { get; set; } =
            req => Task.FromResult<UpdateResult>(new UpdateResult.UpdateSuccess(req.DocumentUuid));
        public Func<IDeleteRequest, Task<DeleteResult>> OnDelete { get; set; } =
            req => Task.FromResult<DeleteResult>(new DeleteResult.DeleteSuccess());
        public Func<ResourceInfo, DocumentIdentity, TraceId, Task<DocumentUuid?>> OnResolve { get; set; } =
            (info, identity, traceId) => Task.FromResult<DocumentUuid?>(new DocumentUuid(Guid.NewGuid()));

        public bool CommitCalled { get; private set; }
        public bool RollbackCalled { get; private set; }

        public Task<UpsertResult> UpsertDocumentAsync(IUpsertRequest request) => OnUpsert(request);

        public Task<UpdateResult> UpdateDocumentByIdAsync(IUpdateRequest request) => OnUpdate(request);

        public Task<DeleteResult> DeleteDocumentByIdAsync(IDeleteRequest request) => OnDelete(request);

        public Task<DocumentUuid?> ResolveDocumentUuidAsync(
            ResourceInfo resourceInfo,
            DocumentIdentity identity,
            TraceId traceId
        ) => OnResolve(resourceInfo, identity, traceId);

        public Task CommitAsync()
        {
            CommitCalled = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync()
        {
            RollbackCalled = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelegatePipelineStep(Action<RequestInfo> action) : IPipelineStep
    {
        public Task Execute(RequestInfo requestInfo, Func<Task> next)
        {
            action(requestInfo);
            return Task.CompletedTask;
        }
    }

    private sealed class TestValidationPipelines
    {
        private readonly PipelineProvider _createPipeline;
        private readonly PipelineProvider _updatePipeline;
        private readonly PipelineProvider _deletePipeline;

        public TestValidationPipelines(Action<RequestInfo> action)
        {
            _createPipeline = new PipelineProvider([new DelegatePipelineStep(action)]);
            _updatePipeline = new PipelineProvider([new DelegatePipelineStep(action)]);
            _deletePipeline = new PipelineProvider([new DelegatePipelineStep(action)]);
        }

        public VersionedLazy<PipelineProvider> CreateUpsert() =>
            new(() => _createPipeline, () => Guid.NewGuid());

        public VersionedLazy<PipelineProvider> CreateUpdate() =>
            new(() => _updatePipeline, () => Guid.NewGuid());

        public VersionedLazy<PipelineProvider> CreateDelete() =>
            new(() => _deletePipeline, () => Guid.NewGuid());
    }

    private sealed class BatchHandlerTestContext
    {
        public ApiSchemaDocuments ApiSchemaDocuments { get; }
        public ProjectSchema ProjectSchema { get; }
        public ResourceSchema ResourceSchema { get; }
        public TestValidationPipelines ValidationPipelines { get; }
        public TestBatchUnitOfWorkFactory UnitOfWorkFactory { get; }
        public AppSettings Settings { get; }
        public TestApiSchemaProvider SchemaProvider { get; }

        public BatchHandlerTestContext(
            bool allowIdentityUpdates = false,
            Func<TestBatchUnitOfWork>? factory = null
        )
        {
            var documentsBuilder = CreateBuilder(allowIdentityUpdates);
            var providerBuilder = CreateBuilder(allowIdentityUpdates);
            ApiSchemaDocuments = documentsBuilder.ToApiSchemaDocuments();
            ProjectSchema = ApiSchemaDocuments.GetAllProjectSchemas().Single();
            ResourceSchema = new ResourceSchema(
                ProjectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(StudentResource))!
            );
            SchemaProvider = new TestApiSchemaProvider(providerBuilder.AsApiSchemaNodes());
            Settings = new AppSettings
            {
                AllowIdentityUpdateOverrides = string.Empty,
                BatchMaxOperations = 100,
                MaskRequestBodyInLogs = true,
            };

            ValidationPipelines = new TestValidationPipelines(requestInfo =>
            {
                PopulateRequestInfo(requestInfo);
            });

            UnitOfWorkFactory = new TestBatchUnitOfWorkFactory(factory ?? (() => new TestBatchUnitOfWork()));
        }

        private static ApiSchemaBuilder CreateBuilder(bool allowIdentityUpdates)
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource(StudentResource, allowIdentityUpdates: allowIdentityUpdates)
                .WithIdentityJsonPaths(["$.studentUniqueId"])
                .WithEndResource()
                .WithEndProject();
        }

        private void PopulateRequestInfo(RequestInfo requestInfo)
        {
            var segments = requestInfo
                .FrontendRequest.Path.TrimStart('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var documentUuid =
                requestInfo.Method == RequestMethod.POST || segments.Length < 3
                    ? No.DocumentUuid
                    : new DocumentUuid(Guid.Parse(segments[2]));

            var projectEndpoint = new ProjectEndpointNameModel(ProjectSchema.ProjectEndpointName.Value);
            var endpoint = new EndpointName(segments.Length > 1 ? segments[1] : string.Empty);

            requestInfo.PathComponents = new PathComponents(projectEndpoint, endpoint, documentUuid);
            requestInfo.ProjectSchema = ProjectSchema;
            requestInfo.ResourceSchema = ResourceSchema;

            if (!string.IsNullOrWhiteSpace(requestInfo.FrontendRequest.Body))
            {
                requestInfo.ParsedBody = JsonNode.Parse(requestInfo.FrontendRequest.Body!)!;
            }

            if (requestInfo.ParsedBody is JsonObject document)
            {
                string identityValue = document["studentUniqueId"]?.GetValue<string>() ?? string.Empty;
                var identity = new DocumentIdentity(
                    [new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), identityValue)]
                );
                requestInfo.DocumentInfo = new DocumentInfo(
                    identity,
                    new ReferentialId(Guid.NewGuid()),
                    [],
                    [],
                    [],
                    null
                );
            }
            else
            {
                requestInfo.DocumentInfo = new DocumentInfo(
                    new DocumentIdentity([]),
                    new ReferentialId(Guid.Empty),
                    [],
                    [],
                    [],
                    null
                );
            }

            requestInfo.ResourceInfo = new ResourceInfo(
                ProjectSchema.ProjectName,
                new ResourceName(StudentResource),
                IsDescriptor: false,
                ProjectSchema.ResourceVersion,
                AllowIdentityUpdates: ResourceSchema.AllowIdentityUpdates,
                EducationOrganizationHierarchyInfo: No.EducationOrganizationHierarchyInfo,
                AuthorizationSecurableInfo: []
            );

            requestInfo.DocumentSecurityElements = new DocumentSecurityElements([], [], [], [], []);
            requestInfo.AuthorizationStrategyEvaluators = [];
            requestInfo.AuthorizationSecurableInfo = [];
            requestInfo.AuthorizationPathways = [];
        }

        public RequestInfo CreateBatchRequest(string json)
        {
            var frontendRequest = new FrontendRequest(
                Path: "/batch",
                Body: json,
                Headers: new Dictionary<string, string>(),
                QueryParameters: new Dictionary<string, string>(),
                TraceId: new TraceId(Guid.NewGuid().ToString())
            );

            return new RequestInfo(frontendRequest, RequestMethod.POST)
            {
                ApiSchemaDocuments = ApiSchemaDocuments,
                ApiSchemaReloadId = Guid.NewGuid(),
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: string.Empty,
                    ClaimSetName: string.Empty,
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                ),
            };
        }

        public BatchHandler CreateHandler(IBatchUnitOfWorkFactory? factoryOverride = null)
        {
            return new BatchHandler(
                NullLogger<BatchHandler>.Instance,
                Options.Create(Settings),
                ResiliencePipeline.Empty,
                factoryOverride,
                SchemaProvider,
                new NoAuthorizationServiceFactory(),
                ValidationPipelines.CreateUpsert(),
                ValidationPipelines.CreateUpdate(),
                ValidationPipelines.CreateDelete()
            );
        }
    }

    [Test]
    public async Task When_Batch_Exceeds_Limit_Returns_413()
    {
        var context = new BatchHandlerTestContext();
        context.Settings.BatchMaxOperations = 1;
        var requestInfo = context.CreateBatchRequest(
            """
            [
              { "op": "create", "resource": "students", "document": { "studentUniqueId": "1", "_etag": "1" } },
              { "op": "create", "resource": "students", "document": { "studentUniqueId": "2", "_etag": "2" } }
            ]
            """
        );

        var handler = context.CreateHandler(context.UnitOfWorkFactory);

        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(413);
        context.UnitOfWorkFactory.LastCreated.Should().BeNull();
    }

    [Test]
    public async Task When_Batch_Has_No_Operations_Returns_Success()
    {
        var context = new BatchHandlerTestContext();
        var requestInfo = context.CreateBatchRequest("[]");
        var handler = context.CreateHandler(context.UnitOfWorkFactory);

        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        requestInfo.FrontendResponse.Body?.AsArray().Count.Should().Be(0);
    }

    [Test]
    public async Task When_Backend_Does_Not_Support_Batch_Returns_501()
    {
        var context = new BatchHandlerTestContext();
        var requestInfo = context.CreateBatchRequest(
            """
            [{ "op": "create", "resource": "students", "document": { "studentUniqueId": "1", "_etag": "1" } }]
            """
        );
        var handler = context.CreateHandler(factoryOverride: null);

        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(501);
    }

    [Test]
    public async Task When_Validation_Pipeline_Returns_Response_Batch_Fails()
    {
        var context = new BatchHandlerTestContext();
        var pipeline = new TestValidationPipelines(requestInfo =>
        {
            requestInfo.FrontendResponse = new FrontendResponse(400, null, []);
        });

        var handler = new BatchHandler(
            NullLogger<BatchHandler>.Instance,
            Options.Create(context.Settings),
            ResiliencePipeline.Empty,
            context.UnitOfWorkFactory,
            context.SchemaProvider,
            new NoAuthorizationServiceFactory(),
            pipeline.CreateUpsert(),
            pipeline.CreateUpdate(),
            pipeline.CreateDelete()
        );

        var requestInfo = context.CreateBatchRequest(
            """
            [{ "op": "create", "resource": "students", "document": { "studentUniqueId": "1", "_etag": "1" } }]
            """
        );

        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        context.UnitOfWorkFactory.LastCreated.Should().NotBeNull();
        context.UnitOfWorkFactory.LastCreated!.RollbackCalled.Should().BeTrue();
    }

    [Test]
    public async Task Update_With_Mismatched_Natural_Key_For_Immutable_Resource_Returns_400()
    {
        var context = new BatchHandlerTestContext(allowIdentityUpdates: false);
        var requestInfo = context.CreateBatchRequest(
            """
            [
              {
                "op": "update",
                "resource": "students",
                "naturalKey": { "studentUniqueId": "1" },
                "document": { "studentUniqueId": "different", "_etag": "123" }
              }
            ]
            """
        );

        var handler = context.CreateHandler(context.UnitOfWorkFactory);
        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        context.UnitOfWorkFactory.LastCreated.Should().NotBeNull();
        context.UnitOfWorkFactory.LastCreated!.RollbackCalled.Should().BeTrue();
    }

    [Test]
    public async Task Update_With_Etag_Mismatch_Returns_412()
    {
        var context = new BatchHandlerTestContext();
        var unitOfWork = new TestBatchUnitOfWork
        {
            OnResolve = (info, identity, traceId) =>
                Task.FromResult<DocumentUuid?>(new DocumentUuid(Guid.NewGuid())),
            OnUpdate = request => Task.FromResult<UpdateResult>(new UpdateResult.UpdateFailureETagMisMatch()),
        };
        var handler = context.CreateHandler(new TestBatchUnitOfWorkFactory(() => unitOfWork));

        var requestInfo = context.CreateBatchRequest(
            """
            [
              {
                "op": "update",
                "resource": "students",
                "documentId": "0478a8a4-5cde-42d8-8a58-5a2e5a428702",
                "document": { "studentUniqueId": "1", "_etag": "bad-etag" }
              }
            ]
            """
        );

        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(412);
        unitOfWork.RollbackCalled.Should().BeTrue();
    }

    [Test]
    public async Task Upsert_NotAuthorized_Returns_403()
    {
        var context = new BatchHandlerTestContext();
        var unitOfWork = new TestBatchUnitOfWork
        {
            OnUpsert = request =>
                Task.FromResult<UpsertResult>(
                    new UpsertResult.UpsertFailureNotAuthorized(
                        ["Access denied"],
                        new[] { "Contact your admin" }
                    )
                ),
        };

        var handler = context.CreateHandler(new TestBatchUnitOfWorkFactory(() => unitOfWork));

        var requestInfo = context.CreateBatchRequest(
            """
            [{ "op": "create", "resource": "students", "document": { "studentUniqueId": "1", "_etag": "1" } }]
            """
        );

        await handler.Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        unitOfWork.RollbackCalled.Should().BeTrue();
    }
}
