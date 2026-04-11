// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
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
using static EdFi.DataManagementService.Core.External.Backend.GetResult;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class GetByIdHandlerTests
{
    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new GetByIdHandler(
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            new NoAuthorizationServiceFactory()
        );

        return (handler, serviceProvider);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new() { ["value"] = "expected" };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body?.Should().BeEquivalentTo(Repository.ResponseBody);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_A_Null_LastModifiedTraceId : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new() { ["value"] = "expected" };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, null)
                );
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body?.Should().BeEquivalentTo(Repository.ResponseBody);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Exists : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new GetFailureNotExists());
            }
        }

        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Not_Implemented : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new GetFailureNotImplemented(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(501);

            var expected = Utility.ToJsonError(Repository.ResponseBody, new TraceId(_traceId));

            requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(requestInfo.FrontendResponse.Body, expected)
                .Should()
                .BeTrue(
                    $"""
                    expected: {expected}

                    actual: {requestInfo.FrontendResponse.Body}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Unknown_Failure : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new UnknownFailure(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
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

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Relational_Read_Metadata : GetByIdHandlerTests
    {
        private static ResourceInfo CreateResourceInfo(
            string projectName = "Ed-Fi",
            string resourceName = "Student",
            bool isDescriptor = false
        )
        {
            return new ResourceInfo(
                ProjectName: new ProjectName(projectName),
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

        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IRelationalGetRequest? CapturedRequest { get; private set; }

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest as IRelationalGetRequest;

                return Task.FromResult<GetResult>(
                    new GetSuccess(
                        No.DocumentUuid,
                        new JsonObject(),
                        DateTime.UtcNow,
                        getRequest.TraceId.Value
                    )
                );
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = No.RequestInfo();
        private readonly MappingSet _mappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);
        private readonly ContentTypeDefinition _readContentType = new(
            MemberSelection.IncludeOnly,
            [new PropertyRule("firstName")],
            [],
            [],
            []
        );

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ResourceInfo = CreateResourceInfo(projectName: "SampleExtension");
            _requestInfo.ResourceSchema = new ResourceSchema(
                new JsonObject
                {
                    ["resourceName"] = "Student",
                    ["isDescriptor"] = false,
                    ["identityJsonPaths"] = new JsonArray
                    {
                        "$.studentUniqueId",
                        "$.schoolReference.schoolId",
                    },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                    },
                }
            );
            _requestInfo.MappingSet = _mappingSet;
            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "ReadableProfile",
                ContentType: ProfileContentType.Read,
                ResourceProfile: new ResourceProfile(
                    ResourceName: "Student",
                    LogicalSchema: null,
                    ReadContentType: _readContentType,
                    WriteContentType: null
                ),
                WasExplicitlySpecified: true
            );

            var (getByIdHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await getByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_constructs_a_relational_get_request_for_external_reads()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.MappingSet.Should().BeSameAs(_mappingSet);
            _repository
                .CapturedRequest.ResourceInfo.Should()
                .BeEquivalentTo(
                    new BaseResourceInfo(
                        new ProjectName("SampleExtension"),
                        new ResourceName("Student"),
                        false
                    )
                );
            _repository.CapturedRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.ExternalResponse);
            _repository.CapturedRequest.ReadableProfileProjectionContext.Should().NotBeNull();
            _repository
                .CapturedRequest.ReadableProfileProjectionContext!.ContentTypeDefinition.Should()
                .BeSameAs(_readContentType);
            _repository
                .CapturedRequest.ReadableProfileProjectionContext.IdentityPropertyNames.Should()
                .Equal("studentUniqueId", "schoolReference");
            _repository.CapturedRequest.ResourceName.Should().Be(new ResourceName("Student"));
        }
    }
}
