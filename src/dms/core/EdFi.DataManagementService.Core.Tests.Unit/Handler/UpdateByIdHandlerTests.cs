// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
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

        public Guid SchemaLoadId => Guid.Empty;

        public bool IsSchemaValid => true;

        public List<ApiSchemaFailure> ApiSchemaFailures => [];
    }

    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new UpdateByIdHandler(
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            new Provider(),
            new NoAuthorizationServiceFactory()
        );

        return (handler, serviceProvider);
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

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(204);
            requestInfo.FrontendResponse.Body.Should().BeNull();
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

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo
                .FrontendResponse.Body?.ToJsonString()
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
            private static readonly BaseResourceInfo _targetResource = new(
                new ProjectName("ed-fi"),
                new ResourceName("School"),
                false
            );

            public override Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
            {
                var sharedReferentialId = new ReferentialId(Guid.NewGuid());

                return Task.FromResult<UpdateResult>(
                    new UpdateFailureReference([
                        new(
                            Path: new JsonPath("$.schoolReference"),
                            TargetResource: _targetResource,
                            DocumentIdentity: new([]),
                            ReferentialId: sharedReferentialId,
                            Reason: DocumentReferenceFailureReason.Missing
                        ),
                        new(
                            Path: new JsonPath("$.sessionReference.schoolReference"),
                            TargetResource: _targetResource,
                            DocumentIdentity: new([]),
                            ReferentialId: sharedReferentialId,
                            Reason: DocumentReferenceFailureReason.Missing
                        ),
                    ])
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(409);

            var body = requestInfo.FrontendResponse.Body!.AsObject();
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("One or more references could not be resolved. See 'validationErrors' for details.");
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:data-conflict:unresolved-reference");
            body["title"]!.GetValue<string>().Should().Be("Unresolved Reference");

            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(2);
            validationErrors["$.schoolReference"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The referenced School item does not exist.");
            validationErrors["$.sessionReference.schoolReference"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The referenced School item does not exist.");
            body["errors"]!.AsArray().Count.Should().Be(0);
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

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(409);
            requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("key = value");
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            requestInfo.FrontendResponse.LocationHeaderPath.Should().BeNull();
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

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.Body.Should().NotBeNull();
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

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
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
        private readonly RequestInfo requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (updateByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await updateByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);

            var expected = $$"""
{
  "error": "FailureMessage",
  "correlationId": "{{_traceId}}"
}
""";

            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(requestInfo.FrontendResponse.Body, JsonNode.Parse(expected))
                .Should()
                .BeTrue(
                    $"""
expected: {expected}

actual: {requestInfo.FrontendResponse.Body}
"""
                );
        }
    }
}
