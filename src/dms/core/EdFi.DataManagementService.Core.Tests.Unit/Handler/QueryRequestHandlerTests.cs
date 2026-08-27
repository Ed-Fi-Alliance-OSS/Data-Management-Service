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
using Polly.CircuitBreaker;
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

        PageTokenCodec.TryDecode(nextPageToken, out var range, out _).Should().BeTrue();
        return range;
    }

    /// <summary>
    /// The anchor stamped on the emitted continuation. A token names a range in one column's units, and
    /// this is the only thing that says which, so it is read back through the codec for the same reason
    /// the range is.
    /// </summary>
    internal static PageOrderingMode? DecodeNextPageTokenAnchor(RequestInfo requestInfo)
    {
        if (!requestInfo.FrontendResponse.Headers.TryGetValue("Next-Page-Token", out var nextPageToken))
        {
            return null;
        }

        PageTokenCodec.TryDecode(nextPageToken, out _, out var orderingMode).Should().BeTrue();
        return orderingMode;
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
    /// One gate decides the continuation header for every GET-many response: the page selected keys.
    /// Nothing about the response body participates, neither resource family has a rule of its own, and
    /// the ordering no longer withholds a token — the anchor the page was selected on is stamped onto
    /// the token instead.
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

        // A traditional page over a max-bearing change-version window really did select keys, and
        // reports their maximum in ContentVersion. The token says so, which is what lets the range be
        // handed out at all: replayed under the same window it resumes on the same column, and replayed
        // under a different one it is rejected rather than read as a DocumentId range.
        [Test]
        public async Task It_marks_a_windowed_traditional_continuation_with_the_content_version_anchor()
        {
            var requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.PageOrderingMode = PageOrderingMode.ContentVersion;

            await ExecuteAsync(new QueryResult.QuerySuccess([], null, 2509L), requestInfo: requestInfo);

            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
            DecodeNextPageTokenAnchor(requestInfo).Should().Be(PageOrderingMode.ContentVersion);
        }

        // The unwindowed twin of the case above, so the marker is proven to follow the request's
        // resolved anchor rather than being stamped the same way on every token.
        [Test]
        public async Task It_marks_an_unwindowed_continuation_with_the_document_id_anchor()
        {
            var requestInfo = await ExecuteAsync(new QueryResult.QuerySuccess([], null, 2509L));

            DecodeNextPageTokenAnchor(requestInfo).Should().Be(PageOrderingMode.DocumentId);
        }

        // A windowed cursor page keeps its own upper bound, in the anchor's units, and carries the same
        // marker forward — which is what keeps a walk that entered through a windowed partition inside
        // that partition and replayable.
        [Test]
        public async Task It_marks_a_windowed_cursor_continuation_and_retains_its_upper_bound()
        {
            var requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.PageOrderingMode = PageOrderingMode.ContentVersion;

            await ExecuteAsync(
                new QueryResult.QuerySuccess([], null, 2509L),
                new CollectionPaging.Cursor(new CursorRange(7, 4200), new PageSize(25)),
                requestInfo
            );

            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, 4200));
            DecodeNextPageTokenAnchor(requestInfo).Should().Be(PageOrderingMode.ContentVersion);
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

            // The anchor the validating middleware resolves for that max-bearing window. Set here
            // rather than left to the default because this fixture builds RequestInfo directly, and
            // the assertion below has to be able to fail: DocumentId is the enum's zero value.
            _requestInfo.PageOrderingMode = PageOrderingMode.ContentVersion;
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
            _requestInfo.ApplicationContext = new(
                Id: 1,
                ApplicationId: 2,
                ClientId: "client-id",
                ClientUuid: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                DataStoreIds: [],
                CreatorOwnershipTokenId: 303,
                OwnershipTokenIds: [404, 202, 404]
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
            _repository.CapturedRequest.AuthorizationContext.CreatorOwnershipTokenId.Should().Be(303);
            _repository.CapturedRequest.AuthorizationContext.OwnershipTokenIds.Should().Equal(404, 202, 404);
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

        /// <summary>
        /// The backend no longer resolves the anchor from the window it receives; it reads the one Core
        /// resolved. This is the assertion that the resolution actually crosses the seam, and the only
        /// thing standing between a windowed page and one selected in the wrong order.
        /// </summary>
        [Test]
        public void It_copies_the_resolved_page_ordering_mode_onto_the_relational_query_request()
        {
            _repository.CapturedRequest.Should().NotBeNull();
            _repository.CapturedRequest!.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
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
        public void It_snapshots_the_optional_ownership_projection_without_normalizing_CMS_order()
        {
            short[] ownershipTokenIds = [404, 202, 404];

            RelationalAuthorizationContext context = RelationalAuthorizationContext.Create(
                _requestInfo.ClientAuthorizations,
                creatorOwnershipTokenId: 303,
                ownershipTokenIds
            );
            ownershipTokenIds[0] = 999;

            context.CreatorOwnershipTokenId.Should().Be(303);
            context.OwnershipTokenIds.Should().Equal(404, 202, 404);
            context.ClaimEducationOrganizationIds.Should().Equal(255900L, 255901L, 255902L);
            context.NamespacePrefixes.Should().Equal("uri://sample-a.org", "uri://sample-b.org");
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

    /// <summary>
    /// What each GET-many outcome contributes to the collection-paging metric.
    /// </summary>
    /// <remarks>
    /// One request contributes exactly one measurement set, which every test here asserts by reading
    /// <see cref="RecordingCollectionPagingTelemetry.Single" />. Instrument names, units, and tag-set
    /// cardinality are pinned in the telemetry component's own tests; what is under test here is which
    /// outcome and command category this handler chose for a given backend result.
    /// </remarks>
    [TestFixture]
    [Parallelizable]
    public class Given_Collection_Paging_Telemetry_For_A_Query : QueryRequestHandlerTests
    {
        private sealed class Repository(QueryResult result) : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            ) => Task.FromResult(result);
        }

        private sealed class ThrowingRepository(Exception fault) : NotImplementedDocumentStoreRepository
        {
            public override Task<QueryResult> QueryDocuments(
                IQueryRequest queryRequest,
                CancellationToken cancellationToken = default
            ) => throw fault;
        }

        private static readonly CollectionPaging _traditionalPaging = new CollectionPaging.Traditional(
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
        );

        private static readonly CollectionPaging _cursorPaging = new CollectionPaging.Cursor(
            new CursorRange(7, 4200),
            new PageSize(64)
        );

        private static JsonArray Documents(int count) =>
            [.. Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject { ["i"] = index })];

        private static async Task<(
            RequestInfo RequestInfo,
            RecordingCollectionPagingTelemetry Telemetry
        )> ExecuteAsync(QueryResult result, CollectionPaging? paging = null, RequestInfo? requestInfo = null)
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            requestInfo ??= RequestInfoWithRelationalMappingSet();
            requestInfo.CollectionPaging = paging ?? _traditionalPaging;

            var (queryHandler, serviceProvider) = Handler(
                new Repository(result),
                collectionPagingTelemetry: telemetry
            );
            requestInfo.ScopedServiceProvider = serviceProvider;
            await queryHandler.Execute(requestInfo, NullNext);

            return (requestInfo, telemetry);
        }

        private static async Task<(
            Exception? Fault,
            RecordingCollectionPagingTelemetry Telemetry
        )> ExecuteThrowingAsync(Exception fault, CancellationToken requestCancellationToken = default)
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.CollectionPaging = _traditionalPaging;
            requestInfo.RequestCancellationToken = requestCancellationToken;

            var (queryHandler, serviceProvider) = Handler(
                new ThrowingRepository(fault),
                collectionPagingTelemetry: telemetry
            );
            requestInfo.ScopedServiceProvider = serviceProvider;

            try
            {
                await queryHandler.Execute(requestInfo, NullNext);
                return (null, telemetry);
            }
            catch (Exception caught)
            {
                return (caught, telemetry);
            }
        }

        [Test]
        public async Task It_records_a_served_page_that_can_be_walked_from()
        {
            var (_, telemetry) = await ExecuteAsync(new QueryResult.QuerySuccess(Documents(3), null, 2509L));

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Kind.Should().Be(CollectionPagingMeasurementKind.Page);
            measurement.PagingMode.Should().Be("traditional");
            measurement.CommandCategory.Should().Be("page");
            measurement.Provider.Should().Be("postgresql");
            measurement.Outcome.Should().Be("success");
            measurement.Requested.Should().Be(25);
            measurement.Returned.Should().Be(3);
            measurement.Duration.Should().NotBeNull().And.BeGreaterThanOrEqualTo(TimeSpan.Zero);
        }

        // The total count is compiled into the same selection command, which is the command-shape
        // difference the category exists to separate.
        [Test]
        public async Task It_records_page_with_count_when_the_request_asked_for_a_total_count()
        {
            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(2), 7, 2509L),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
                )
            );

            telemetry.Single.CommandCategory.Should().Be("page_with_count");
            telemetry.Single.Outcome.Should().Be("success");
        }

        // First of the two terminal-page boundaries: selection chose nothing, so there is nothing
        // after it.
        [Test]
        public async Task It_records_terminal_page_when_selection_chose_nothing()
        {
            var (_, telemetry) = await ExecuteAsync(new QueryResult.QuerySuccess([], null));

            telemetry.Single.Outcome.Should().Be("terminal_page");
            telemetry.Single.CommandCategory.Should().Be("page");
            telemetry.Single.Returned.Should().Be(0);
        }

        // Second: the codec cannot advance past Int64.MaxValue, so no next range can be named.
        [Test]
        public async Task It_records_terminal_page_at_the_maximum_document_id()
        {
            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(1), null, long.MaxValue)
            );

            telemetry.Single.Outcome.Should().Be("terminal_page");
        }

        // The distinction the outcome now draws, on the page that used to be excused from it: a
        // traditional page over a max-bearing change-version window is ordered by ContentVersion and
        // hands out a token in its own anchor, so it is a healthy walk in progress and says so. The
        // page is the same one that used to withhold a token and be reported as success anyway.
        [Test]
        public async Task It_records_success_for_a_windowed_page_that_hands_out_a_continuation()
        {
            var windowedRequestInfo = RequestInfoWithRelationalMappingSet();
            windowedRequestInfo.PageOrderingMode = PageOrderingMode.ContentVersion;

            var (requestInfo, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(4), null, 2509L),
                requestInfo: windowedRequestInfo
            );

            requestInfo.FrontendResponse.Headers.Should().ContainKey("Next-Page-Token");
            telemetry.Single.Outcome.Should().Be("success");
            telemetry.Single.Returned.Should().Be(4);
        }

        // The other half of that distinction, which the old flag blurred: the same windowed request on
        // a page that selected nothing produces no token, and that really is the end of the walk.
        [Test]
        public async Task It_records_terminal_page_for_a_windowed_page_that_hands_out_none()
        {
            var windowedRequestInfo = RequestInfoWithRelationalMappingSet();
            windowedRequestInfo.PageOrderingMode = PageOrderingMode.ContentVersion;

            var (requestInfo, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess([], null),
                requestInfo: windowedRequestInfo
            );

            requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
            telemetry.Single.Outcome.Should().Be("terminal_page");
        }

        // Early-empty outranks the terminal-page question: no command was issued, so no command shape
        // can be attributed to it.
        [Test]
        public async Task It_records_early_empty_ahead_of_terminal_page_for_a_skipped_selection()
        {
            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess([], null) { SelectionSkipped = true }
            );

            telemetry.Single.Outcome.Should().Be("early_empty");
            telemetry.Single.CommandCategory.Should().Be("none");
            telemetry.Single.Returned.Should().Be(0);
        }

        // early_empty names an empty result no selection command produced, so both halves are checked
        // rather than the flag alone. A page carrying documents was built from rows something selected,
        // and reporting it as the short-circuit would attach the one outcome whose name is a claim about
        // database work to a request that plainly did some — and then hide it from the size-gap
        // comparisons, which exclude command_category=none on both sides.
        [Test]
        public async Task It_does_not_record_early_empty_for_a_skipped_selection_that_served_documents()
        {
            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(3), null, 2509L) { SelectionSkipped = true }
            );

            telemetry.Single.Outcome.Should().Be("success");
            telemetry.Single.CommandCategory.Should().Be("page");
            telemetry.Single.Returned.Should().Be(3);
        }

        private static readonly TestCaseData[] _failureOutcomes =
        [
            new TestCaseData(
                new QueryResult.QueryFailureNotImplemented("not implemented"),
                "not_implemented"
            ).SetName("{m}(not_implemented)"),
            new TestCaseData(
                new QueryResult.QueryFailureSecurityConfiguration(["invalid metadata"]),
                "security_configuration"
            ).SetName("{m}(security_configuration)"),
            new TestCaseData(
                new QueryResult.QueryFailureNamespaceNotAuthorized(
                    Given_A_Repository_That_Returns_Namespace_Not_Authorized.Failure
                ),
                "not_authorized"
            ).SetName("{m}(not_authorized)"),
            new TestCaseData(new QueryResult.QueryFailureRetryable(), "retry_exhausted").SetName(
                "{m}(retry_exhausted)"
            ),
            new TestCaseData(new QueryResult.UnknownFailure("unknown"), "unknown_failure").SetName(
                "{m}(unknown_failure)"
            ),
            // A known error reports query terms that evaded validation. The bounded outcome set has no
            // value of its own for it, and counting it as validation_rejected would dilute the
            // middleware-rejection rate operators watch, so it is an unclassified backend failure.
            new TestCaseData(
                new QueryResult.QueryFailureKnownError("invalid query terms"),
                "unknown_failure"
            ).SetName("{m}(known_error)"),
        ];

        [TestCaseSource(nameof(_failureOutcomes))]
        public async Task It_records_every_failure_with_no_command_category(
            QueryResult failure,
            string expectedOutcome
        )
        {
            var (_, telemetry) = await ExecuteAsync(failure);

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Outcome.Should().Be(expectedOutcome);
            measurement.CommandCategory.Should().Be("none");

            // No page was produced, so nothing may be contributed to the returned-size histogram: a zero
            // there would be indistinguishable from a successful empty page.
            measurement.Returned.Should().BeNull();
        }

        [Test]
        public async Task It_records_an_execution_exception_and_still_propagates_it()
        {
            InvalidOperationException fault = new("custom view is not conforming");

            var (caught, telemetry) = await ExecuteThrowingAsync(fault);

            caught.Should().BeSameAs(fault);
            telemetry.Single.Outcome.Should().Be("execution_exception");
            telemetry.Single.CommandCategory.Should().Be("none");
            telemetry.Single.Returned.Should().BeNull();
        }

        // While the circuit is open every collection read lands here, so this is the outcome's dominant
        // population in production rather than an edge of it - which is what the operator documentation
        // now says, and what this pins. The breaker is the outermost strategy on the pipeline these
        // handlers share, so the refusal comes from the pipeline itself and the repository is never
        // reached; a fault handed to the throwing repository would exercise the same catch but not that
        // shape, so the pipeline here is a real breaker held open. Narrowing the handler to let a broken
        // circuit past the general catch would stop counting the outage-dominant population, and this is
        // what fails when it does.
        [Test]
        public async Task It_records_an_execution_exception_when_the_circuit_is_open()
        {
            RecordingCollectionPagingTelemetry telemetry = new();
            CircuitBreakerManualControl manualControl = new();
            ResiliencePipeline openCircuit = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions { ManualControl = manualControl })
                .Build();

            await manualControl.IsolateAsync();

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.CollectionPaging = _traditionalPaging;

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IQueryHandler)))
                .Returns(
                    new ThrowingRepository(
                        new InvalidOperationException("The repository must not be reached.")
                    )
                );
            requestInfo.ScopedServiceProvider = serviceProvider;

            var execute = async () =>
                await new QueryRequestHandler(NullLogger.Instance, openCircuit, telemetry).Execute(
                    requestInfo,
                    NullNext
                );

            // The breaker's own exception rather than the repository's, which is the "refused before it
            // reached the database" half of what this outcome is documented to cover. Reaching the
            // repository would surface as its InvalidOperationException here instead.
            await execute.Should().ThrowAsync<BrokenCircuitException>();

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Outcome.Should().Be("execution_exception");
            measurement.CommandCategory.Should().Be("none");
            measurement.Returned.Should().BeNull();
        }

        // A disconnected client is the absence of a completed read, not a kind of one, and its duration
        // would measure how long the client waited rather than how long a read took.
        [Test]
        public async Task It_records_nothing_when_the_request_token_was_cancelled()
        {
            using CancellationTokenSource cancellationSource = new();
            await cancellationSource.CancelAsync();

            var (caught, telemetry) = await ExecuteThrowingAsync(
                new OperationCanceledException(cancellationSource.Token),
                cancellationSource.Token
            );

            caught.Should().BeOfType<OperationCanceledException>();
            telemetry.Measurements.Should().BeEmpty();
        }

        // The companion to the test above: the filter narrows the case to a client disconnect rather
        // than opening a hole for every cancellation. A cancellation the request did not ask for is a
        // genuine internal fault.
        [Test]
        public async Task It_records_an_execution_exception_for_a_cancellation_the_request_did_not_ask_for()
        {
            var (caught, telemetry) = await ExecuteThrowingAsync(new OperationCanceledException());

            caught.Should().BeOfType<OperationCanceledException>();
            telemetry.Single.Outcome.Should().Be("execution_exception");
        }

        [Test]
        public async Task It_records_the_cursor_page_size_as_requested()
        {
            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(1), null, 21L),
                _cursorPaging
            );

            telemetry.Single.PagingMode.Should().Be("cursor");
            telemetry.Single.Requested.Should().Be(64);
        }

        // A traditional request that named no limit will be served at most the configured maximum, so
        // that is the size it asked for.
        [Test]
        public async Task It_records_the_configured_maximum_when_traditional_paging_named_no_limit()
        {
            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(1), null, 21L),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: null, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                )
            );

            telemetry.Single.Requested.Should().Be(500);
        }

        [TestCase(SqlDialect.Pgsql, "postgresql")]
        [TestCase(SqlDialect.Mssql, "sqlserver")]
        public async Task It_reports_the_provider_of_the_resolved_mapping_set(
            SqlDialect dialect,
            string expectedProvider
        )
        {
            RequestInfo requestInfo = No.RequestInfo();
            requestInfo.MappingSet = RelationalWriteSeamFixture.Create().CreateSupportedMappingSet(dialect);

            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(2), null, 2509L),
                requestInfo: requestInfo
            );

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Provider.Should().Be(expectedProvider);

            // Only the provider differs across dialects. Everything else about the same operation has to
            // match, or an operator could not compare the two engines on one dashboard.
            measurement.PagingMode.Should().Be("traditional");
            measurement.CommandCategory.Should().Be("page");
            measurement.Outcome.Should().Be("success");
            measurement.Requested.Should().Be(25);
            measurement.Returned.Should().Be(2);
        }

        // The metric describes traffic shape, never who asked or what they asked for. Sentinels stand in
        // for every request-derived value the emission site can reach.
        [Test]
        public async Task It_carries_no_request_data_into_any_label()
        {
            const string ResourceNameSentinel = "SentinelResourceName";
            const string TenantSentinel = "SentinelTenantKey";
            const string NamespaceSentinel = "uri://sentinel-namespace.org";
            const string ClientSentinel = "SentinelClientId";
            const string FilterValueSentinel = "SentinelFilterValue";
            const string PageTokenSentinel = "SentinelPageToken";

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.ResourceInfo = requestInfo.ResourceInfo with
            {
                ResourceName = new ResourceName(ResourceNameSentinel),
            };
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                Tenant = TenantSentinel,
                QueryParameters = new Dictionary<string, string>
                {
                    ["pageToken"] = PageTokenSentinel,
                    ["name"] = FilterValueSentinel,
                },
            };
            requestInfo.ClientAuthorizations = new ClientAuthorizations(
                ClientId: ClientSentinel,
                TokenId: "sentinel-token",
                ClaimSetName: "SentinelClaimSet",
                EducationOrganizationIds: [],
                NamespacePrefixes: [new NamespacePrefix(NamespaceSentinel)],
                DataStoreIds: []
            );
            requestInfo.QueryElements =
            [
                new QueryElement("name", [new JsonPath("$.name")], FilterValueSentinel, "string"),
            ];

            var (_, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(1), null, 21L),
                requestInfo: requestInfo
            );

            string[] sentinels =
            [
                ResourceNameSentinel,
                TenantSentinel,
                NamespaceSentinel,
                ClientSentinel,
                FilterValueSentinel,
                PageTokenSentinel,
            ];
            CollectionPagingMeasurement measurement = telemetry.Single;
            string[] labels =
            [
                measurement.PagingMode,
                measurement.CommandCategory,
                measurement.Provider,
                measurement.Outcome,
            ];

            foreach (string label in labels)
            {
                foreach (string sentinel in sentinels)
                {
                    label.Should().NotContain(sentinel);
                }
            }
        }

        // Instrumentation observes; it must not participate. The full response contract is asserted
        // alongside an emission so a change that recorded a measurement by altering a header or a body
        // could not pass.
        [Test]
        public async Task It_leaves_the_response_exactly_as_it_would_be_without_instrumentation()
        {
            var (requestInfo, telemetry) = await ExecuteAsync(
                new QueryResult.QuerySuccess(Documents(2), 7, 2509L),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
                )
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
            requestInfo.FrontendResponse.Body!.AsArray().Should().HaveCount(2);
            requestInfo.FrontendResponse.Headers["Total-Count"].Should().Be("7");
            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
            telemetry.Single.Outcome.Should().Be("success");
        }

        // The other half of "instrumentation must not participate": recording runs after the response is
        // assembled, so a measurement callback that throws would discard a page the request had already
        // earned and answer a system error instead.
        [Test]
        public async Task It_serves_the_page_when_recording_throws()
        {
            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.CollectionPaging = _traditionalPaging;

            var (queryHandler, serviceProvider) = Handler(
                new Repository(new QueryResult.QuerySuccess(Documents(2), null, 2509L)),
                collectionPagingTelemetry: new ThrowingCollectionPagingTelemetry()
            );
            requestInfo.ScopedServiceProvider = serviceProvider;

            await queryHandler.Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body!.AsArray().Should().HaveCount(2);
            DecodeNextPageToken(requestInfo).Should().Be(new CursorRange(2510, long.MaxValue));
        }

        // The execution-exception emission runs from inside a catch that is about to rethrow. A telemetry
        // fault there would replace the fault being reported, which is exactly the diagnosis that catch
        // exists to preserve.
        [Test]
        public async Task It_propagates_the_execution_fault_when_recording_throws()
        {
            InvalidOperationException executionFault = new("A configured custom view is not conforming.");

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.CollectionPaging = _traditionalPaging;

            var (queryHandler, serviceProvider) = Handler(
                new ThrowingRepository(executionFault),
                collectionPagingTelemetry: new ThrowingCollectionPagingTelemetry()
            );
            requestInfo.ScopedServiceProvider = serviceProvider;

            Func<Task> execute = () => queryHandler.Execute(requestInfo, NullNext);

            (await execute.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should()
                .BeSameAs(executionFault);
        }
    }
}
