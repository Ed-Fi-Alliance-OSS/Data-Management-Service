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
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Telemetry;
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
        ILogger? logger = null,
        ICollectionPagingTelemetry? collectionPagingTelemetry = null
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IQueryHandler))).Returns(queryHandler);

        var handler = new QueryRequestHandler(
            logger ?? NullLogger.Instance,
            ResiliencePipeline.Empty,
            collectionPagingTelemetry ?? NoOpCollectionPagingTelemetry.Instance
        );

        return (handler, serviceProvider);
    }

    /// <summary>
    /// The emitted continuation range, decoded by the codec that encodes it, so the assertion cannot
    /// drift from the transport encoding by transcribing it.
    /// </summary>
    internal static CursorRange? DecodeNextPageToken(RequestInfo requestInfo)
    {
        if (!requestInfo.FrontendResponse.Headers.TryGetValue("Next-Page-Token", out var nextPageToken))
        {
            return null;
        }

        PageTokenCodec.TryDecode(nextPageToken, out var range).Should().BeTrue();
        return range;
    }

    /// <summary>
    /// Collection paging reaches the backend as the typed choice request validation produced, and the
    /// page it selects comes back with a continuation the client can walk from.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Cursor_Paged_Request : QueryRequestHandlerTests
    {
        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
            {
                CapturedRequest = queryRequest;

                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], null, 21L));
            }
        }

        private readonly Repository _repository = new();
        private readonly RequestInfo _requestInfo = RequestInfoWithRelationalMappingSet();

        private static readonly CollectionPaging.Cursor _cursorPaging = new(
            new CursorRange(7, 42),
            new PageSize(25)
        );

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.CollectionPaging = _cursorPaging;

            var (queryHandler, serviceProvider) = Handler(_repository);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_hands_the_typed_cursor_paging_to_the_backend()
        {
            _repository.CapturedRequest!.Paging.Should().Be(_cursorPaging);
        }

        [Test]
        public void It_serves_the_selected_page()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        }

        // The next range starts after the keys this page selected and keeps the request's own upper
        // bound, which is how a walk that entered through a partition stays inside it.
        [Test]
        public void It_continues_after_the_selected_keys_within_the_requested_bound()
        {
            DecodeNextPageToken(_requestInfo).Should().Be(new CursorRange(22, 42));
        }
    }

    /// <summary>
    /// One gate decides the continuation header for every GET-many response: the page selected keys,
    /// and the maximum it selected can anchor a DocumentId continuation. Nothing about the response
    /// body participates, and neither resource family has a rule of its own.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Query_Success_Reaching_The_Continuation_Gate : QueryRequestHandlerTests
    {
        private sealed class Repository(QueryResult.QuerySuccess success)
            : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            ) => Task.FromResult<QueryResult>(success);
        }

        private static readonly CollectionPaging _traditionalPaging = new CollectionPaging.Traditional(
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
        );

        private static async Task<RequestInfo> ExecuteAsync(
            QueryResult.QuerySuccess success,
            CollectionPaging? paging = null,
            RequestInfo? requestInfo = null
        )
        {
            requestInfo ??= RequestInfoWithRelationalMappingSet();
            requestInfo.CollectionPaging = paging ?? _traditionalPaging;

            var (queryHandler, serviceProvider) = Handler(new Repository(success));
            requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(requestInfo, NullNext);

            return requestInfo;
        }

        // A traditional response can begin a cursor walk. It carried no upper bound, so the walk it
        // starts is unbounded above.
        [Test]
        public async Task It_starts_an_unbounded_walk_from_a_traditional_page()
        {
            var requestInfo = await ExecuteAsync(new QueryResult.QuerySuccess([], null, 2509L));

            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
        }

        [Test]
        public async Task It_emits_no_continuation_when_page_selection_chose_nothing()
        {
            var requestInfo = await ExecuteAsync(new QueryResult.QuerySuccess([], null));

            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
        }

        // The boundary describes selected keys, not surviving rows: a page whose every selected row was
        // deleted before hydration still advances the walk past them. A client that stopped on an empty
        // body would stop early, which is why the gate never asks about the body.
        [Test]
        public async Task It_continues_past_selected_keys_whose_rows_were_all_deleted()
        {
            var requestInfo = await ExecuteAsync(new QueryResult.QuerySuccess([], null, 2509L));

            requestInfo.FrontendResponse.Body!.AsArray().Should().BeEmpty();
            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
        }

        // A zero-size page selects no keys, so it cannot advance a walk — by contract, not by accident.
        [Test]
        public async Task It_emits_no_continuation_for_a_zero_size_page()
        {
            var requestInfo = await ExecuteAsync(
                new QueryResult.QuerySuccess([], null),
                new CollectionPaging.Cursor(CursorRange.From(1), new PageSize(0))
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body!.AsArray().Should().BeEmpty();
            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
        }

        // The terminal page of a walk: the range selected nothing, so the client learns the walk is
        // over by receiving no continuation rather than by an extra fetched row or a count.
        [Test]
        public async Task It_emits_no_continuation_for_an_empty_terminal_page()
        {
            var requestInfo = await ExecuteAsync(
                new QueryResult.QuerySuccess([], null),
                new CollectionPaging.Cursor(new CursorRange(2510, 42), new PageSize(25))
            );

            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
        }

        // Advancing past Int64.MaxValue would overflow, so there is no next range to name.
        [Test]
        public async Task It_emits_no_continuation_at_the_maximum_document_id()
        {
            var requestInfo = await ExecuteAsync(new QueryResult.QuerySuccess([], null, long.MaxValue));

            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
        }

        // A page ordered by something other than DocumentId really did select keys, and reports their
        // maximum, but that maximum does not say where the page ended, so it cannot anchor a walk.
        [Test]
        public async Task It_emits_no_continuation_when_the_page_cannot_anchor_one()
        {
            var requestInfo = await ExecuteAsync(
                new QueryResult.QuerySuccess([], null, 2509L) { AllowsDocumentIdContinuation = false }
            );

            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
        }

        [Test]
        public async Task It_keeps_total_count_alongside_a_continuation()
        {
            var requestInfo = await ExecuteAsync(
                new QueryResult.QuerySuccess([], 7, 2509L),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
                )
            );

            requestInfo
                .FrontendResponse.Headers.Should()
                .ContainKey("Total-Count")
                .WhoseValue.Should()
                .Be("7");
            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
        }

        [Test]
        public async Task It_emits_no_total_count_for_a_cursor_page()
        {
            var requestInfo = await ExecuteAsync(
                new QueryResult.QuerySuccess([], null, 2509L),
                new CollectionPaging.Cursor(CursorRange.From(1), new PageSize(25))
            );

            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Total-Count");
            requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
        }

        // Regular-resource and descriptor results reach the handler as the same QuerySuccess, so the
        // header they receive is decided once rather than by two rules that could drift apart.
        [Test]
        public async Task It_decides_the_continuation_the_same_way_for_both_resource_families()
        {
            QueryResult.QuerySuccess success = new([], null, 2509L);

            var regularResourceRequestInfo = await ExecuteAsync(success);

            var descriptorRequestInfo = RequestInfoWithRelationalMappingSet();
            descriptorRequestInfo.ResourceInfo = descriptorRequestInfo.ResourceInfo with
            {
                ResourceName = new ResourceName("SchoolTypeDescriptor"),
                IsDescriptor = true,
            };

            await ExecuteAsync(success, requestInfo: descriptorRequestInfo);

            descriptorRequestInfo
                .FrontendResponse.Headers.Should()
                .Equal(regularResourceRequestInfo.FrontendResponse.Headers);
            DecodeNextPageToken(descriptorRequestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repository_That_Returns_Success : QueryRequestHandlerTests
    {
        internal class Repository : NotImplementedDocumentStoreRepository
        {
            public static readonly JsonArray ResponseBody = [];
            public IQueryRequest? CapturedRequest { get; private set; }

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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
            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            var expected = FailureResponse.ForSystemError(new TraceId(_traceId));

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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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
                ProjectEndpointName: new ProjectEndpointName("ed-fi"),
                EndpointName: new EndpointName("schools"),
                Operation: ResourcePathOperation.Collection.Instance
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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
            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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
            _requestInfo.CollectionPaging = new CollectionPaging.Traditional(_paginationParameters);
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
            _repository
                .CapturedRequest.Paging.Should()
                .BeOfType<CollectionPaging.Traditional>()
                .Which.Parameters.Should()
                .BeSameAs(_paginationParameters);
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
    public class Given_A_Request_Cancellation_Token : QueryRequestHandlerTests
    {
        private sealed class Repository : NotImplementedDocumentStoreRepository
        {
            public CancellationToken CapturedCancellationToken { get; private set; }

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
            {
                CapturedCancellationToken = cancellationToken;
                return Task.FromResult<QueryResult>(new QueryResult.QuerySuccess([], 0));
            }
        }

        [Test]
        public async Task It_passes_the_request_token_to_the_query_repository()
        {
            using var cancellationSource = new CancellationTokenSource();
            var repository = new Repository();
            var requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.RequestCancellationToken = cancellationSource.Token;

            var (queryHandler, serviceProvider) = Handler(repository);
            requestInfo.ScopedServiceProvider = serviceProvider;

            await queryHandler.Execute(requestInfo, NullNext);

            repository.CapturedCancellationToken.Should().Be(cancellationSource.Token);
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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

            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            )
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
