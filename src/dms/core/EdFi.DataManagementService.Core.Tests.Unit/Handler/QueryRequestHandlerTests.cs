// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
        IQueryHandler queryHandler,
        ILogger? logger = null
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryHandler))).Returns(queryHandler);

        var handler = new QueryRequestHandler(logger ?? NullLogger.Instance, ResiliencePipeline.Empty);

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
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with
            {
                ResponseContentCoding = ResponseContentCoding.Brotli,
            };
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
        public void It_constructs_a_relational_query_request()
        {
            var relationalRequest = _repository
                .CapturedRequest.Should()
                .BeAssignableTo<IQueryRequest>()
                .Subject;
            relationalRequest.MappingSet.Should().BeSameAs(_requestInfo.MappingSet);
            relationalRequest.ResponseContentCoding.Should().Be(ResponseContentCoding.Brotli);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_No_Mapping_Set : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                CapturedRequest = queryRequest;
                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = No.RequestInfo();
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            var (queryHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;

            try
            {
                await queryHandler.Execute(_requestInfo, NullNext);
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
            _exception!.Message.Should().Contain("query requests").And.Contain("ResolveMappingSetMiddleware");
        }

        [Test]
        public void It_does_not_call_the_repository()
        {
            _repository.CapturedRequest.Should().BeNull();
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

        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

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
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

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
    public class Given_A_Repository_That_Returns_A_Security_Configuration_Failure : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseErrors =
            [
                "Relational query authorization metadata is invalid for resource 'Ed-Fi.School'. "
                    + "Strategy 'CustomAuthorizationStrategy' is not a recognized built-in strategy and "
                    + "does not match the {BasisResource}With... custom-view convention.",
            ];

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(
                    new QueryResult.QueryFailureSecurityConfiguration(
                        ResponseErrors,
                        [
                            new SecurityConfigurationFailureDiagnostic(
                                ProviderOrPlannerFailureKind: "RelationshipAuthorization.InvalidAuthorizationStrategy",
                                ResourceFullName: "Ed-Fi.School",
                                ConfiguredStrategyNames: ["CustomAuthorizationStrategy"],
                                ConfiguredStrategyIndexes: [1],
                                RequestSurface: "GetManyResource",
                                CmsAction: "Read"
                            ),
                        ]
                    )
                );
            }
        }

        private static readonly string _traceId = "security-config";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);
        private RecordingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger();
            var (queryHandler, serviceProvider) = Handler(new Repository(), _logger);
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with
            {
                Path = "ed-fi/schools",
                Tenant = "tenant-a",
            };
            _requestInfo.ClientAuthorizations = new ClientAuthorizations("", "", "SIS-Vendor", [], [], []);
            _requestInfo.PathComponents = new PathComponents(
                new ProjectEndpointName("ed-fi"),
                new EndpointName("schools"),
                new DocumentUuid()
            );
            _requestInfo.ResourceInfo = new ResourceInfo(
                ProjectName: new ProjectName("Ed-Fi"),
                ResourceName: new ResourceName("School"),
                IsDescriptor: false,
                ResourceVersion: new SemVer("5.0.0"),
                AllowIdentityUpdates: false
            );
            _requestInfo.ResourceActionAuthStrategies = ["OwnershipBased", "CustomAuthorizationStrategy"];
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_the_correct_response()
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

        [Test]
        public void It_logs_security_configuration_failure_with_backend_diagnostics()
        {
            var logRecord = _logger
                .Records.Where(static record => record.Level == LogLevel.Error)
                .Should()
                .ContainSingle()
                .Subject;

            logRecord.Message.Should().Contain("SecurityConfigurationFailure");
            logRecord.Properties["Tenant"].Should().Be("tenant-a");
            logRecord.Properties["CorrelationId"].Should().Be(_traceId);
            logRecord.Properties["HttpMethod"].Should().Be("GET");
            logRecord.Properties["RoutePath"].Should().Be("ed-fi/schools");
            logRecord.Properties["RequestSurface"].Should().Be("GetManyResource");
            logRecord.Properties["CmsAction"].Should().Be("Read");
            logRecord.Properties["AssignedClaimSet"].Should().Be("SIS-Vendor");
            logRecord.Properties["ResourceFullName"].Should().Be("Ed-Fi.School");
            ((IEnumerable<string>)logRecord.Properties["ConfiguredStrategyNames"]!)
                .Should()
                .Equal("CustomAuthorizationStrategy", "OwnershipBased");
            ((IEnumerable<int>)logRecord.Properties["ConfiguredStrategyIndexes"]!).Should().Equal(1);
            ((IEnumerable<string>)logRecord.Properties["ProviderOrPlannerFailureKinds"]!)
                .Should()
                .Equal("RelationshipAuthorization.InvalidAuthorizationStrategy");
            ((IEnumerable<string>)logRecord.Properties["SecurityConfigurationErrors"]!)
                .Should()
                .Equal(Repository.ResponseErrors);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_A_ReadChanges_Security_Configuration_Failure
        : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly string[] ResponseErrors =
            [
                "Change query authorization metadata is invalid for resource 'Ed-Fi.School'.",
            ];

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(
                    new QueryResult.QueryFailureSecurityConfiguration(
                        ResponseErrors,
                        [
                            new SecurityConfigurationFailureDiagnostic(
                                ProviderOrPlannerFailureKind: "ChangeQueryAuthorization.InvalidAuthorizationStrategy",
                                ResourceFullName: "Ed-Fi.School",
                                RequestSurface: "ReadChangesResource",
                                CmsAction: "ReadChanges"
                            ),
                        ]
                    )
                );
            }
        }

        private static readonly string _traceId = "readchanges-security-config";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);
        private RecordingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger();
            var (queryHandler, serviceProvider) = Handler(new Repository(), _logger);
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with
            {
                Path = "ed-fi/schools/deletes",
            };
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_logs_the_backend_diagnostic_cms_action_instead_of_the_get_fallback()
        {
            var logRecord = _logger
                .Records.Where(static record => record.Level == LogLevel.Error)
                .Should()
                .ContainSingle()
                .Subject;

            logRecord.Properties["HttpMethod"].Should().Be("GET");
            logRecord.Properties["RoutePath"].Should().Be("ed-fi/schools/deletes");
            logRecord.Properties["RequestSurface"].Should().Be("ReadChangesResource");
            logRecord.Properties["CmsAction"].Should().Be("ReadChanges");
            ((IEnumerable<string>)logRecord.Properties["SecurityConfigurationErrors"]!)
                .Should()
                .Equal(Repository.ResponseErrors);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Namespace_Not_Authorized : QueryRequestHandlerTests
    {
        internal static readonly NamespaceAuthorizationFailure Failure = new(
            NamespaceAuthorizationFailureKind.NoPrefixesConfigured,
            ValueSource: null,
            EmittedAuth1Index: null,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: []
        );

        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(
                    new QueryResult.QueryFailureNamespaceNotAuthorized(Failure)
                );
            }
        }

        private static readonly string _traceId = "namespace-query-403";
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

        [SetUp]
        public async Task Setup()
        {
            var (queryHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
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
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet(_traceId);

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
    public class Given_A_Descriptor_Relational_Query_That_Returns_Failure_Not_Implemented_For_Authorization
        : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public const string ResponseBody =
                "Relational query authorization is not implemented for resource "
                + "'Ed-Fi.SchoolTypeDescriptor' when effective GET-many authorization requires "
                + "filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests "
                + "with no authorization strategies or with 'NamespaceBased' and/or "
                + "'NoFurtherAuthorizationRequired' are currently supported.";

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QueryFailureNotImplemented(ResponseBody));
            }
        }

        private static readonly string _traceId = "descriptor-query-auth-501";
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

            var (queryHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_descriptor_query_authorization_failures_to_http_501()
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
    public class Given_A_Descriptor_Relational_Query_That_Returns_Failure_Not_Implemented_For_Omitted_Capability
        : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public const string ResponseBody =
                "Descriptor query capability for resource 'Ed-Fi.SchoolTypeDescriptor' was intentionally "
                + "omitted: descriptor query support was intentionally omitted for the test fixture.";

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                return Task.FromResult<QueryResult>(new QueryResult.QueryFailureNotImplemented(ResponseBody));
            }
        }

        private static readonly string _traceId = "descriptor-query-omission-501";
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

            var (queryHandler, serviceProvider) = Handler(new Repository());
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_maps_descriptor_query_capability_omissions_to_http_501()
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
                AllowIdentityUpdates: false
            );
        }

        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                CapturedRequest = queryRequest;

                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
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
        private readonly QueryElement[] _queryElements =
        [
            new("schoolId", [new JsonPath("$.schoolReference.schoolId")], "255901", "integer"),
            new("studentUniqueId", [new JsonPath("$.studentUniqueId")], "800000001", "string"),
        ];
        private readonly PaginationParameters _paginationParameters = new(
            Limit: 25,
            Offset: 10,
            TotalCount: true,
            MaximumPageSize: 500
        );
        private readonly AuthorizationStrategyEvaluator[] _authorizationStrategyEvaluators =
        [
            new(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired, [], FilterOperator.Or),
            new(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                [new AuthorizationFilter.EducationOrganization("999999")],
                FilterOperator.Or
            ),
            new(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                [new AuthorizationFilter.EducationOrganization("111111")],
                FilterOperator.And
            ),
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
            _requestInfo.QueryElements = _queryElements;
            _requestInfo.PaginationParameters = _paginationParameters;
            _requestInfo.AuthorizationStrategyEvaluators = _authorizationStrategyEvaluators;
            _requestInfo.ChangeVersionRange = new ChangeVersionRange(100L, 200L);
            _requestInfo.ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId: "client-id",
                ClaimSetName: "claim-set",
                EducationOrganizationIds:
                [
                    new EducationOrganizationId(255902),
                    new EducationOrganizationId(255901),
                    new EducationOrganizationId(255902),
                    new EducationOrganizationId(255900),
                ],
                NamespacePrefixes:
                [
                    new NamespacePrefix("uri://sample-b.org"),
                    new NamespacePrefix("uri://sample-a.org"),
                    new NamespacePrefix("uri://sample-b.org"),
                ],
                DataStoreIds: []
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
            _repository
                .CapturedRequest.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .Equal(255900L, 255901L, 255902L);
            _repository
                .CapturedRequest.AuthorizationContext.NamespacePrefixes.Should()
                .Equal("uri://sample-a.org", "uri://sample-b.org");
            _repository.CapturedRequest.ResourceInfo.Should().BeSameAs(_requestInfo.ResourceInfo);
            _repository.CapturedRequest.QueryElements.Should().BeSameAs(_queryElements);
            _repository.CapturedRequest.PaginationParameters.Should().BeSameAs(_paginationParameters);
            _repository
                .CapturedRequest.AuthorizationStrategyEvaluators.Should()
                .BeSameAs(_authorizationStrategyEvaluators);
            _repository
                .CapturedRequest.AuthorizationStrategyEvaluators.Select(static evaluator =>
                    evaluator.AuthorizationStrategyName
                )
                .Should()
                .Equal(
                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                );
            _repository
                .CapturedRequest.ResourceInfo.Should()
                .BeEquivalentTo(
                    new ResourceInfo(
                        ProjectName: new ProjectName("SampleExtension"),
                        ResourceName: new ResourceName("Student"),
                        IsDescriptor: false,
                        ResourceVersion: new SemVer("1.0.0"),
                        AllowIdentityUpdates: false
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
        public void It_copies_the_change_version_range_onto_the_relational_query_request()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.ChangeVersionRange.Should().Be(new ChangeVersionRange(100L, 200L));
        }

        [Test]
        public void It_builds_relational_authorization_context_from_client_authorizations_instead_of_strategy_filters()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository
                .CapturedRequest!.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .Equal(255900L, 255901L, 255902L);
            _repository
                .CapturedRequest.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .NotContain(111111L)
                .And.NotContain(999999L);
            _repository
                .CapturedRequest.AuthorizationContext.NamespacePrefixes.Should()
                .Equal("uri://sample-a.org", "uri://sample-b.org");
        }

        [Test]
        public void It_normalizes_direct_and_client_authorization_creation_paths_to_the_same_values()
        {
            var directlyConstructedContext = new RelationalAuthorizationContext(
                [255902L, 255901L, 255902L, 255900L],
                ["uri://sample-b.org", "uri://sample-a.org", "uri://sample-b.org"]
            );

            _repository.CapturedRequest.Should().NotBeNull();
            _repository
                .CapturedRequest!.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .Equal(directlyConstructedContext.ClaimEducationOrganizationIds);
            _repository
                .CapturedRequest.AuthorizationContext.NamespacePrefixes.Should()
                .Equal(directlyConstructedContext.NamespacePrefixes);
        }

        [Test]
        public void It_sets_profile_content_type_for_relational_profile_queries()
        {
            _requestInfo
                .FrontendResponse.ContentType.Should()
                .Be("application/vnd.ed-fi.student.readableprofile.readable+json");
        }

        [Test]
        public void It_centralizes_the_claim_education_organization_parameter_name()
        {
            RelationalAuthorizationParameterNameConstants
                .ClaimEducationOrganizationIds.Should()
                .Be(nameof(RelationalAuthorizationContext.ClaimEducationOrganizationIds));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Descriptor_Request_With_Relational_Query_Metadata : QueryRequestHandlerTests
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
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                CapturedRequest = queryRequest;

                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
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

            var (queryHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_adds_descriptor_identity_fields_to_the_query_readable_profile_projection_context()
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

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Query_Request_With_No_EdOrg_Claims : QueryRequestHandlerTests
    {
        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
            {
                CapturedRequest = queryRequest;

                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();
        private readonly MappingSet _mappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.MappingSet = _mappingSet;

            var (queryHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_preserves_an_empty_claim_education_organization_list()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository
                .CapturedRequest!.AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .BeEmpty();
        }

        [Test]
        public void It_normalizes_an_unset_change_version_range_to_none()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.ChangeVersionRange.Should().Be(ChangeVersionRange.None);
        }
    }
}
