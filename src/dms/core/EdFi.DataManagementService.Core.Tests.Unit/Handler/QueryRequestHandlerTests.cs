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
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.Handler.Utility;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class QueryRequestHandlerTests
{
    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IQueryHandler queryHandler
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryHandler))).Returns(queryHandler);

        var handler = new QueryRequestHandler(NullLogger.Instance, ResiliencePipeline.Empty);

        return (handler, serviceProvider);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonArray ResponseBody = [];
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                CapturedRequest = queryRequest;
                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (queryHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Be(Repository.ResponseBody.ToJsonString());
        }

        [Test]
        public void It_constructs_a_standard_query_request_when_no_mapping_set_is_present()
        {
            _repository.CapturedRequest.Should().BeOfType<QueryRequest>();
            _repository.CapturedRequest.Should().NotBeAssignableTo<IRelationalQueryRequest>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Failure_Invalid_Query : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("Error"));
            }
        }

        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var (queryHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Body.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Unknown_Failure : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.UnknownFailure(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo _requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (queryHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);

            var expected = ToJsonError("FailureMessage", new TraceId(_traceId));

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(_requestInfo.FrontendResponse.Body, expected)
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
    public class Given_A_Repository_That_Returns_Failure_Not_Implemented : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string ResponseBody = "FailureMessage";

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QueryFailureNotImplemented(ResponseBody));
            }
        }

        private static readonly string _traceId = "xyz";
        private readonly RequestInfo _requestInfo = No.RequestInfo(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (queryHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(501);

            var expected = ToJsonError(Repository.ResponseBody, new TraceId(_traceId));

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            JsonNode
                .DeepEquals(_requestInfo.FrontendResponse.Body, expected)
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
    public class Given_A_Request_With_Relational_Query_Metadata : QueryRequestHandlerTests
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
            public IRelationalQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                CapturedRequest = queryRequest as IRelationalQueryRequest;

                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
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

            var (queryHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_constructs_a_relational_query_request()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.MappingSet.Should().BeSameAs(_mappingSet);
            _repository.CapturedRequest.ResourceInfo.Should().BeSameAs(_requestInfo.ResourceInfo);
            _repository
                .CapturedRequest.ResourceInfo.Should()
                .BeEquivalentTo(
                    new ResourceInfo(
                        ProjectName: new ProjectName("SampleExtension"),
                        ResourceName: new ResourceName("Student"),
                        IsDescriptor: false,
                        ResourceVersion: new SemVer("1.0.0"),
                        AllowIdentityUpdates: false,
                        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(
                            false,
                            default,
                            default
                        ),
                        AuthorizationSecurableInfo: []
                    )
                );
            _repository.CapturedRequest.ReadableProfileProjectionContext.Should().NotBeNull();
            _repository
                .CapturedRequest.ReadableProfileProjectionContext!.ContentTypeDefinition.Should()
                .BeSameAs(_readContentType);
            _repository
                .CapturedRequest.ReadableProfileProjectionContext.IdentityPropertyNames.Should()
                .Equal("studentUniqueId", "schoolReference");
        }

        [Test]
        public void It_sets_profile_content_type_for_relational_profile_queries()
        {
            _requestInfo
                .FrontendResponse.ContentType.Should()
                .Be("application/vnd.ed-fi.student.readableprofile.readable+json");
        }
    }
}
