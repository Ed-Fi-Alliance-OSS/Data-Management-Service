// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
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
    internal static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        return new DeleteByIdHandler(
            serviceProvider,
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            new NoAuthorizationServiceFactory()
        );
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
            IPipelineStep deleteByIdHandler = Handler(new Repository());
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

            IPipelineStep deleteByIdHandler = Handler(new Repository());
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

            IPipelineStep deleteByIdHandler = Handler(new Repository());
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
            IPipelineStep deleteByIdHandler = Handler(new Repository());
            _requestInfo.ResourceSchema = GetResourceSchema();
            await deleteByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(409);
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
            IPipelineStep deleteByIdHandler = Handler(new Repository());
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
}
