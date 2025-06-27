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
using FluentAssertions;
using Json.More;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.UpdateResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class UpdateByIdHandlerTests
{
    private static readonly JsonNode _apiSchemaRootNode =
        JsonNode.Parse(
            "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
        ) ?? new JsonObject();

    internal class Provider : IApiSchemaProvider
    {
        public ApiSchemaDocumentNodes GetApiSchemaNodes()
        {
            return new(_apiSchemaRootNode, []);
        }

        public Guid ReloadId => Guid.Empty;

        public bool IsSchemaValid => true;

        public List<ApiSchemaFailure> ApiSchemaFailures => [];

        public Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync() =>
            Task.FromResult(new ApiSchemaLoadStatus(true, []));

        public Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(
            JsonNode coreSchema,
            JsonNode[] extensionSchemas
        ) => Task.FromResult(new ApiSchemaLoadStatus(true, []));
    }

    internal static IPipelineStep Handler(IDocumentStoreRepository documentStoreRepository)
    {
        return new UpdateByIdHandler(
            documentStoreRepository,
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            new Provider(),
            new NoAuthorizationServiceFactory()
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateSuccess(updateRequest.DocumentUuid));
            }
        }

        private readonly RequestData requestData = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(204);
            requestData.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureNotExists());
            }
        }

        private readonly RequestData requestData = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(404);
            requestData
                .FrontendResponse.Body?.AsJsonString()
                .Should()
                .Be(
                    """"
                    {"detail":"Resource to update was not found","type":"urn:ed-fi:api:not-found","title":"Not Found","status":404,"correlationId":"","validationErrors":{},"errors":[]}
                    """"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_S_Repository_That_Returns_Failure_Reference : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "ReferencingDocumentInfo";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureReference([new(ResponseBody)]));
            }
        }

        private readonly RequestData requestData = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(409);
            requestData.FrontendResponse.Body?.ToJsonString().Should().Contain(Repository.ResponseBody);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Identity_Conflict : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureIdentityConflict(
                        new(""),
                        [new KeyValuePair<string, string>("key", "value")]
                    )
                );
            }
        }

        private readonly RequestData requestData = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(409);
            requestData.FrontendResponse.Body?.ToJsonString().Should().Contain("key = value");
            requestData.FrontendResponse.Headers.Should().BeEmpty();
            requestData.FrontendResponse.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Write_Conflict : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UpdateFailureWriteConflict());
            }
        }

        private readonly RequestData requestData = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(409);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Immutable_Identity : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(
                    new UpdateFailureImmutableIdentity(
                        "Identifying values for the resource cannot be changed. Delete and recreate the resource item instead."
                    )
                );
            }
        }

        private readonly RequestData requestData = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(400);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Unknown_Failure : UpdateByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                return Task.FromResult<UpdateResult>(new UnknownFailure(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly RequestData requestData = No.RequestData(_traceId);

        [SetUp]
        public async Task Setup()
        {
            IPipelineStep updateByIdHandler = Handler(new Repository());
            await updateByIdHandler.Execute(requestData, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestData.FrontendResponse.StatusCode.Should().Be(500);

            var expected = $$"""
{
  "error": "FailureMessage",
  "correlationId": "{{_traceId}}"
}
""";

            requestData.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(requestData.FrontendResponse.Body, JsonNode.Parse(expected))
                .Should()
                .BeTrue(
                    $"""
expected: {expected}

actual: {requestData.FrontendResponse.Body}
"""
                );
        }
    }
}
