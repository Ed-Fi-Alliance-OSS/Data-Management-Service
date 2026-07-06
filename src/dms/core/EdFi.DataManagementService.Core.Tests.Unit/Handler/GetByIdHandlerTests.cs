// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
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
    internal static RelationshipAuthorizationFailure CreateRelationshipFailure() =>
        new(
            RelationshipAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 12,
            FailedStrategies:
            [
                new RelationshipAuthorizationFailedStrategy(
                    ConfiguredStrategyIndex: 0,
                    RelationshipLocalOrder: 0,
                    StrategyName: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    StrategyKind: "RelationshipsWithEdOrgsOnly",
                    AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                        Name: "auth.EdOrgIdToEdOrgId",
                        SubjectValueColumn: "TargetEdOrgId",
                        ClaimEducationOrganizationIdColumn: "SourceEdOrgId"
                    ),
                    FailedSubjects:
                    [
                        new RelationshipAuthorizationFailedSubject(
                            SubjectIndex: 0,
                            FailureKind: RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                            RootBinding: new RelationshipAuthorizationRootBinding(
                                ResourceName: "School",
                                TableName: "edfi.School",
                                ColumnName: "SchoolId"
                            ),
                            AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                                Name: "auth.EdOrgIdToEdOrgId",
                                SubjectValueColumn: "TargetEdOrgId",
                                ClaimEducationOrganizationIdColumn: "SourceEdOrgId"
                            ),
                            SecurableElements:
                            [
                                new RelationshipAuthorizationSecurableElement(
                                    Kind: "EducationOrganization",
                                    JsonPath: "$.schoolId",
                                    ReadableName: "SchoolId"
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            ClaimEducationOrganizationIds: [new EducationOrganizationId(255901)]
        );

    internal static (IPipelineStep handler, IServiceProvider serviceProvider) Handler(
        IDocumentStoreRepository documentStoreRepository
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository)))
            .Returns(documentStoreRepository);

        var handler = new GetByIdHandler(NullLogger.Instance, ResiliencePipeline.Empty);

        return (handler, serviceProvider);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new() { ["value"] = "expected" };
            public IGetRequest? CapturedRequest { get; private set; }

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest;
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(_repository);
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body?.Should().BeEquivalentTo(Repository.ResponseBody);
        }

        [Test]
        public void It_constructs_a_relational_get_request()
        {
            var relationalRequest = _repository
                .CapturedRequest.Should()
                .BeAssignableTo<IGetRequest>()
                .Subject;
            relationalRequest.MappingSet.Should().BeSameAs(requestInfo.MappingSet);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_No_Mapping_Set : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public IGetRequest? CapturedRequest { get; private set; }

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest;
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
        private readonly RequestInfo requestInfo = No.RequestInfo();
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(_repository);
            requestInfo.ScopedServiceProvider = serviceProvider;

            try
            {
                await getByIdHandler.Execute(requestInfo, NullNext);
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
        }

        [Test]
        public void It_fails_fast_with_an_actionable_configuration_error()
        {
            _exception.Should().BeOfType<InvalidOperationException>();
            _exception!
                .Message.Should()
                .Contain("get by id requests")
                .And.Contain("ResolveMappingSetMiddleware");
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            _repository.CapturedRequest.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_An_Etag : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

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

        [Test]
        public void It_emits_the_served_etag_as_a_response_header()
        {
            requestInfo.FrontendResponse.Headers.Should().ContainKey("etag");
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_A_Quoted_Matching_If_None_Match_Tag
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "\"5-a1b2c3d4.j._.l\"" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_returns_a_304_with_no_body_and_the_served_etag()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(304);
            requestInfo.FrontendResponse.Body.Should().BeNull();
            requestInfo.FrontendResponse.Headers.Should().ContainKey("etag");
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_An_Unquoted_Matching_If_None_Match_Tag
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "5-a1b2c3d4.j._.l" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_returns_a_304_with_no_body_and_the_served_etag()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(304);
            requestInfo.FrontendResponse.Body.Should().BeNull();
            requestInfo.FrontendResponse.Headers.Should().ContainKey("etag");
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_A_Weak_Matching_If_None_Match_Tag
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "W/\"5-a1b2c3d4.j._.l\"" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_returns_a_304_with_no_body_and_the_served_etag()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(304);
            requestInfo.FrontendResponse.Body.Should().BeNull();
            requestInfo.FrontendResponse.Headers.Should().ContainKey("etag");
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_A_Non_Matching_If_None_Match_Tag
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "\"9-does-not-match\"" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_normal_200_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body?.Should().BeEquivalentTo(Repository.ResponseBody);
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_An_If_None_Match_Tag_Differing_Only_In_Variant_Key
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            // Client's cached tag differs only in the variantKey tail (format/profile/links) -- the
            // full opaque tag must be compared, not a projection, so this must NOT match.
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "\"5-a1b2c3d4.j._.n\"" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_normal_200_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body?.Should().BeEquivalentTo(Repository.ResponseBody);
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_A_Bare_Wildcard_If_None_Match
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "*" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_returns_a_304_with_no_body_and_the_served_etag()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(304);
            requestInfo.FrontendResponse.Body.Should().BeNull();
            requestInfo.FrontendResponse.Headers.Should().ContainKey("etag");
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success_With_A_Quoted_Wildcard_If_None_Match
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonObject ResponseBody = new()
            {
                ["value"] = "expected",
                ["_etag"] = "5-a1b2c3d4.j._.l",
            };

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetSuccess(No.DocumentUuid, ResponseBody, DateTime.UtcNow, getRequest.TraceId.Value)
                );
            }
        }

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            // A quoted "*" is an ordinary opaque tag, not the RFC 9110 bare wildcard -- it must mismatch
            // the served etag and fall through to the normal 200.
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Headers = new Dictionary<string, string> { ["If-None-Match"] = "\"*\"" },
            };
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            requestInfo.ScopedServiceProvider = serviceProvider;
            await getByIdHandler.Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_normal_200_response()
        {
            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body?.Should().BeEquivalentTo(Repository.ResponseBody);
            requestInfo.FrontendResponse.Headers["etag"].Should().Be("5-a1b2c3d4.j._.l");
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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

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

        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();

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
    public class Given_A_Repository_That_Returns_Relationship_Not_Authorized : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(
                    new GetFailureRelationshipNotAuthorized(CreateRelationshipFailure())
                );
            }
        }

        private static readonly string _traceId = "relationship-get-403";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await getByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_relationship_failure_to_http_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be("urn:ed-fi:api:security:authorization");
            _requestInfo.FrontendResponse.Body!["title"]!.ToString().Should().Be("Authorization Denied");
            _requestInfo.FrontendResponse.Body!["status"]!.GetValue<int>().Should().Be(403);
            _requestInfo.FrontendResponse.Body!["detail"]!
                .ToString()
                .Should()
                .Be("Access to the requested data could not be authorized.");
            _requestInfo.FrontendResponse.Body!["correlationId"]!.ToString().Should().Be(_traceId);
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(static error => error!.ToString())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    "No relationships have been established between the caller's education organization id claim ('255901') and the resource item's 'SchoolId' value."
                );
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
        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

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
    public class Given_A_Repository_That_Returns_Security_Configuration_Failure : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseErrors =
            [
                "Resource 'Ed-Fi.School' has relationship authorization metadata that cannot be resolved.",
            ];

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new GetFailureSecurityConfiguration(ResponseErrors));
            }
        }

        private static readonly string _traceId = "relationship-get-500";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await getByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_security_configuration_failure_to_the_canonical_http_500()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            var expected = FailureResponse.ForSecurityConfiguration(
                new TraceId(_traceId),
                Repository.ResponseErrors
            );

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
    public class Given_A_Repository_That_Returns_Namespace_Not_Authorized : GetByIdHandlerTests
    {
        internal static readonly NamespaceAuthorizationFailure Failure = new(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new GetFailureNamespaceNotAuthorized(Failure));
            }
        }

        private static readonly string _traceId = "namespace-get-403";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await getByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_the_namespace_failure_to_the_canonical_namespace_problem_details_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            var expected = NamespaceAuthorizationFailureResponse.ForFailure(Failure, new TraceId(_traceId));

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
    public class Given_A_Descriptor_Relational_Request_That_Returns_Failure_Not_Implemented
        : GetByIdHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public const string ResponseBody =
                "Relational descriptor GET authorization is not implemented for resource "
                + "'Ed-Fi.SchoolTypeDescriptor' when effective GET authorization requires filtering. "
                + "Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no "
                + "authorization strategies or with 'NamespaceBased' and/or "
                + "'NoFurtherAuthorizationRequired' are currently supported.";

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new GetFailureNotImplemented(ResponseBody));
            }
        }

        private static readonly string _traceId = "descriptor-get-501";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);
        private readonly MappingSet _mappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ResourceInfo = new ResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("SchoolTypeDescriptor"),
                IsDescriptor: true,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false
            );
            _requestInfo.ResourceSchema = new ResourceSchema(
                new JsonObject
                {
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["isDescriptor"] = true,
                    ["identityJsonPaths"] = new JsonArray { "$.uri" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                    },
                }
            );
            _requestInfo.MappingSet = _mappingSet;
            _requestInfo.AuthorizationStrategyEvaluators =
            [
                new("RelationshipsWithEdOrgsOnly", [], FilterOperator.And),
            ];

            var (getByIdHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await getByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_descriptor_not_implemented_failures_to_http_501()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(501);

            var expected = Utility.ToJsonError(Repository.ResponseBody, new TraceId(_traceId));

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
        private readonly RequestInfo requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

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
                AllowIdentityUpdates: false
            );
        }

        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IGetRequest? CapturedRequest { get; private set; }

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest;

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
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();
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
        private readonly AuthorizationStrategyEvaluator[] _authorizationStrategyEvaluators =
        [
            new(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired, [], FilterOperator.Or),
        ];

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
            _requestInfo.AuthorizationStrategyEvaluators = _authorizationStrategyEvaluators;
            _requestInfo.ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId: "client-id",
                ClaimSetName: "claim-set",
                EducationOrganizationIds:
                [
                    new EducationOrganizationId(255902),
                    new EducationOrganizationId(255901),
                    new EducationOrganizationId(255902),
                ],
                NamespacePrefixes:
                [
                    new NamespacePrefix("uri://sample-b.org"),
                    new NamespacePrefix("uri://sample-a.org"),
                    new NamespacePrefix("uri://sample-b.org"),
                ],
                DataStoreIds: []
            );
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
            _repository
                .CapturedRequest.AuthorizationStrategyEvaluators.Should()
                .BeSameAs(_authorizationStrategyEvaluators);
            _repository
                .CapturedRequest.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .Equal(255901L, 255902L);
            _repository
                .CapturedRequest.AuthorizationContext.NamespacePrefixes.Should()
                .Equal("uri://sample-a.org", "uri://sample-b.org");
            _repository.CapturedRequest.ReadableProfileProjectionContext.Should().NotBeNull();
            _repository
                .CapturedRequest.ReadableProfileProjectionContext!.ContentTypeDefinition.Should()
                .BeSameAs(_readContentType);
            _repository
                .CapturedRequest.ReadableProfileProjectionContext.IdentityPropertyNames.Should()
                .Equal("studentUniqueId", "schoolReference");
            _repository.CapturedRequest.ResourceName.Should().Be(new ResourceName("Student"));
        }

        [Test]
        public void It_sets_profile_content_type_for_relational_profile_reads()
        {
            _requestInfo
                .FrontendResponse.ContentType.Should()
                .Be("application/vnd.ed-fi.student.readableprofile.readable+json");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Get_Request_With_Empty_EdOrg_Claims : GetByIdHandlerTests
    {
        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IGetRequest? CapturedRequest { get; private set; }

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest;

                return Task.FromResult<GetResult>(
                    new GetFailureRelationshipNotAuthorized(CreateEmptyClaimsRelationshipFailure())
                );
            }

            private static RelationshipAuthorizationFailure CreateEmptyClaimsRelationshipFailure() =>
                new(
                    RelationshipAuthorizationFailureValueSource.Stored,
                    EmittedAuth1Index: 0,
                    FailedStrategies:
                    [
                        new RelationshipAuthorizationFailedStrategy(
                            ConfiguredStrategyIndex: 0,
                            RelationshipLocalOrder: 0,
                            StrategyName: AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                            StrategyKind: "RelationshipsWithEdOrgsOnly",
                            AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                                Name: "auth.EdOrgIdToEdOrgId",
                                SubjectValueColumn: "TargetEdOrgId",
                                ClaimEducationOrganizationIdColumn: "SourceEdOrgId"
                            ),
                            FailedSubjects:
                            [
                                new RelationshipAuthorizationFailedSubject(
                                    SubjectIndex: 0,
                                    FailureKind: RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                                    RootBinding: new RelationshipAuthorizationRootBinding(
                                        ResourceName: "SampleExtension.Student",
                                        TableName: "sample.Student",
                                        ColumnName: "SchoolId"
                                    ),
                                    AuthObject: new RelationshipAuthorizationAuthObjectInfo(
                                        Name: "auth.EdOrgIdToEdOrgId",
                                        SubjectValueColumn: "TargetEdOrgId",
                                        ClaimEducationOrganizationIdColumn: "SourceEdOrgId"
                                    ),
                                    SecurableElements:
                                    [
                                        new RelationshipAuthorizationSecurableElement(
                                            Kind: "EducationOrganization",
                                            JsonPath: "$.schoolReference.schoolId",
                                            ReadableName: "SchoolId"
                                        ),
                                    ]
                                ),
                            ]
                        ),
                    ],
                    ClaimEducationOrganizationIds: []
                );
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(
            "empty-claims-get-by-id"
        );
        private readonly MappingSet _mappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ResourceInfo = new ResourceInfo(
                ProjectName: new ProjectName("SampleExtension"),
                ResourceName: new ResourceName("Student"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false
            );
            _requestInfo.MappingSet = _mappingSet;
            _requestInfo.AuthorizationStrategyEvaluators =
            [
                new(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly, [], FilterOperator.Or),
            ];
            _requestInfo.ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId: "client-id",
                ClaimSetName: "claim-set",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: []
            );

            var (getByIdHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;

            await getByIdHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_passes_empty_edorg_claims_through_the_relational_authorization_context()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository
                .CapturedRequest!.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .BeEmpty();
        }

        [Test]
        public void It_maps_the_empty_claims_relationship_denial_to_http_403()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be("urn:ed-fi:api:security:authorization");
            _requestInfo.FrontendResponse.Body!["detail"]!
                .ToString()
                .Should()
                .Be("Access to the requested data could not be authorized.");
            _requestInfo.FrontendResponse.Body!["correlationId"]!
                .ToString()
                .Should()
                .Be("empty-claims-get-by-id");
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(static error => error!.ToString())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    "No relationships have been established between the caller's education organization id claims (none) and the resource item's 'SchoolId' value."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Descriptor_Request_With_Relational_Read_Metadata : GetByIdHandlerTests
    {
        private static ResourceInfo CreateResourceInfo(
            string projectName = "Ed-Fi",
            string resourceName = "SchoolTypeDescriptor",
            bool isDescriptor = true
        )
        {
            return new ResourceInfo(
                ProjectName: new ProjectName(projectName),
                ResourceName: new ResourceName(resourceName),
                IsDescriptor: isDescriptor,
                ResourceVersion: new SemVer("1.0.0"),
                AllowIdentityUpdates: false
            );
        }

        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IGetRequest? CapturedRequest { get; private set; }

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                CapturedRequest = getRequest;

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
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();
        private readonly MappingSet _mappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);
        private readonly ContentTypeDefinition _readContentType = new(
            MemberSelection.IncludeOnly,
            [new PropertyRule("description")],
            [],
            [],
            []
        );
        private readonly AuthorizationStrategyEvaluator[] _authorizationStrategyEvaluators =
        [
            new(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired, [], FilterOperator.Or),
        ];

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ResourceInfo = CreateResourceInfo(projectName: "SampleExtension");
            _requestInfo.ResourceSchema = new ResourceSchema(
                new JsonObject
                {
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["isDescriptor"] = true,
                    ["identityJsonPaths"] = new JsonArray { "$.uri" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                    },
                }
            );
            _requestInfo.MappingSet = _mappingSet;
            _requestInfo.AuthorizationStrategyEvaluators = _authorizationStrategyEvaluators;
            _requestInfo.ProfileContext = new ProfileContext(
                ProfileName: "ReadableProfile",
                ContentType: ProfileContentType.Read,
                ResourceProfile: new ResourceProfile(
                    ResourceName: "SchoolTypeDescriptor",
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
        public void It_adds_descriptor_identity_fields_to_the_readable_profile_projection_context()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.ReadableProfileProjectionContext.Should().NotBeNull();
            _repository
                .CapturedRequest.ReadableProfileProjectionContext!.IdentityPropertyNames.Should()
                .Contain("uri")
                .And.Contain("namespace")
                .And.Contain("codeValue");
        }
    }
}
