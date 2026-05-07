// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Backend;
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
using static EdFi.DataManagementService.Core.External.Backend.DeleteResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class DeleteByIdHandlerTests
{
    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new DeleteByIdHandler(
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            new NoAuthorizationServiceFactory()
        );

        return (handler, serviceProvider);
    }

    internal static ResourceSchema GetResourceSchema()
    {
        ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Assessment")
            .WithNamespaceSecurityElements(["$.namespace"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathScalar("Namespace", "$.namespace")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "assessments");
        return resourceSchema;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteSuccess());
            }
        }

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(204);
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureNotExists());
            }
        }

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Reference : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseBody = ["ReferencingDocumentInfo"];

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureReference(ResponseBody));
            }
        }

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);

            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(409);
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(string.Join(", ", Repository.ResponseBody));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new DeleteFailureWriteConflict());
            }
        }

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Unknown_Failure : DeleteByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                return Task.FromResult<DeleteResult>(new UnknownFailure(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo _requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var projectSchemaNode = new JsonObject
            {
                ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
            };
            _requestInfo.ProjectSchema = new ProjectSchema(projectSchemaNode, NullLogger.Instance);
            var (deleteByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);

            var expected = $$"""
{
  "error": "FailureMessage",
  "correlationId": "{{_traceId}}"
}
""";

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(_requestInfo.FrontendResponse.Body, JsonNode.Parse(expected))
                .Should()
                .BeTrue(
                    $"""
expected: {expected}

actual: {_requestInfo.FrontendResponse.Body}
"""
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Profiled_Delete_Request : DeleteByIdHandlerTests
    {
        internal sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IDeleteRequest CapturedRequest { get; private set; } = null!;

            public override Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
            {
                CapturedRequest = deleteRequest;
                return Task.FromResult<DeleteResult>(new DeleteSuccess());
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = No.RequestInfo("delete-profile-trace");

        private static ResourceInfo CreateResourceInfo() =>
            new(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("Assessment"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false,
                EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(
                    false,
                    default,
                    default
                ),
                AuthorizationSecurableInfo: []
            );

        private static ProfileContext CreateWriteProfileContext() =>
            new(
                ProfileName: "ReadableProfile",
                ContentType: ProfileContentType.Write,
                ResourceProfile: new ResourceProfile(
                    ResourceName: "Assessment",
                    LogicalSchema: null,
                    ReadContentType: new ContentTypeDefinition(
                        MemberSelection.IncludeOnly,
                        [new PropertyRule("assessmentTitle")],
                        [],
                        [],
                        []
                    ),
                    WriteContentType: new ContentTypeDefinition(
                        MemberSelection.IncludeOnly,
                        [new PropertyRule("assessmentTitle")],
                        [],
                        [],
                        []
                    )
                ),
                WasExplicitlySpecified: true
            );

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.FrontendRequest = new FrontendRequest(
                Body: "{}",
                Form: [],
                Headers: new Dictionary<string, string> { ["If-Match"] = "\"72\"" },
                Path: "/ed-fi/assessments/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
                QueryParameters: [],
                TraceId: new TraceId("delete-profile-trace"),
                RouteQualifiers: []
            );
            _requestInfo.ProjectSchema = new ProjectSchema(
                new JsonObject
                {
                    ["educationOrganizationTypes"] = new JsonArray { "Type1", "Type2" },
                },
                NullLogger.Instance
            );
            _requestInfo.PathComponents = new PathComponents(
                new ProjectEndpointName("ed-fi"),
                new EndpointName("assessments"),
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            );
            _requestInfo.ResourceInfo = CreateResourceInfo();
            _requestInfo.ResourceSchema = GetResourceSchema();
            _requestInfo.ProfileContext = CreateWriteProfileContext();

            var (deleteByIdHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_preserves_if_match_but_does_not_attach_a_profile_etag_surface()
        {
            _repository
                .CapturedRequest.WritePrecondition.Should()
                .Be(new WritePrecondition.IfMatch("\"72\""));
            _repository.CapturedRequest.WritePrecondition.EtagProjectionContext.Should().BeNull();
        }
    }
}
