// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
using Polly.CircuitBreaker;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// The partitions handler: what it hands the backend, how it encodes boundaries as page tokens, and
/// how each backend outcome becomes a response.
/// </summary>
[TestFixture]
[Parallelizable]
public class PartitionRequestHandlerTests
{
    private const int MaximumPageSize = 500;
    private const int RequestedPartitionCount = 9;
    private const string TenantKey = "partition-tenant";

    /// <summary>
    /// Request state the handler must carry into the backend request unchanged. Every value here is
    /// deliberately not the one a bare <see cref="RequestInfo" /> already holds, because a field
    /// asserted against its own default passes whether or not the handler ever copied it.
    /// </summary>
    private static readonly QueryElement[] _queryElements =
    [
        new("codeValue", [new JsonPath("$.codeValue")], "Partitioned", "string"),
    ];

    private static readonly ChangeVersionRange _changeVersionRange = new(1_387, 2_387);

    private sealed class Handler(PartitionResult result) : IPartitionQueryHandler
    {
        public IPartitionRequest? CapturedRequest { get; private set; }

        public int CallCount { get; private set; }

        public Task<PartitionResult> QueryPartitions(
            IPartitionRequest partitionRequest,
            CancellationToken cancellationToken = default
        )
        {
            CapturedRequest = partitionRequest;
            CallCount++;

            return Task.FromResult(result);
        }
    }

    private static async Task<RequestInfo> Execute(
        Handler partitionQueryHandler,
        int? requestedPartitionCount = RequestedPartitionCount,
        ICollectionPagingTelemetry? collectionPagingTelemetry = null,
        PageOrderingMode orderingMode = PageOrderingMode.ContentVersion
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IPartitionQueryHandler)))
            .Returns(partitionQueryHandler);

        RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
        requestInfo.ScopedServiceProvider = serviceProvider;
        requestInfo.RequestedPartitionCount = requestedPartitionCount;
        requestInfo.QueryElements = _queryElements;
        requestInfo.ChangeVersionRange = _changeVersionRange;

        // The anchor the validating middleware resolves for that max-bearing window. Stated here rather
        // than left to the default because these tests build RequestInfo directly, and the assertion
        // that it crosses the seam has to be able to fail: DocumentId is the enum's zero value.
        requestInfo.PageOrderingMode = orderingMode;
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Tenant = TenantKey };

        await new PartitionRequestHandler(
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            MaximumPageSize,
            collectionPagingTelemetry ?? NoOpCollectionPagingTelemetry.Instance
        ).Execute(requestInfo, NullNext);

        return requestInfo;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Calculated_Boundaries : PartitionRequestHandlerTests
    {
        /// <summary>
        /// The emitted tokens decoded by the codec that encodes them, so the assertion cannot drift
        /// from the transport encoding by transcribing it.
        /// </summary>
        private static IReadOnlyList<CursorRange> DecodePageTokens(RequestInfo requestInfo)
        {
            List<CursorRange> ranges = [];

            foreach (JsonNode? pageToken in requestInfo.FrontendResponse.Body!["pageTokens"]!.AsArray())
            {
                PageTokenCodec
                    .TryDecode(pageToken!.GetValue<string>(), out var range, out _)
                    .Should()
                    .BeTrue();
                ranges.Add(range!);
            }

            return ranges;
        }

        /// <summary>
        /// The anchor stamped on every emitted token. A partition token is walked by the same cursor
        /// path a page token is, so the units its bounds are in have to travel with it.
        /// </summary>
        private static IReadOnlyList<PageOrderingMode> DecodePageTokenAnchors(RequestInfo requestInfo)
        {
            List<PageOrderingMode> orderingModes = [];

            foreach (JsonNode? pageToken in requestInfo.FrontendResponse.Body!["pageTokens"]!.AsArray())
            {
                PageTokenCodec
                    .TryDecode(pageToken!.GetValue<string>(), out _, out var orderingMode)
                    .Should()
                    .BeTrue();
                orderingModes.Add(orderingMode);
            }

            return orderingModes;
        }

        private static readonly CursorRange[] _ranges =
        [
            new(10, 199),
            new(200, 499),
            new(500, long.MaxValue),
        ];

        private readonly Handler _handler = new(new PartitionResult.PartitionSuccess(_ranges));
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = await Execute(_handler);
        }

        [Test]
        public void It_serves_the_boundaries()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        }

        // Never a profile media type: the body carries tokens, not documents, so no readable profile
        // can shape it.
        [Test]
        public void It_serves_plain_json()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
        }

        [Test]
        public void It_round_trips_every_range_through_the_codec()
        {
            DecodePageTokens(_requestInfo).Should().Equal(_ranges);
        }

        /// <summary>
        /// Every token, not the first: a client walks each partition independently, so one unmarked or
        /// wrongly marked token would break exactly one slice of the walk and no other.
        /// </summary>
        [Test]
        public void It_marks_every_token_of_a_windowed_response_with_the_content_version_anchor()
        {
            DecodePageTokenAnchors(_requestInfo)
                .Should()
                .AllBeEquivalentTo(PageOrderingMode.ContentVersion)
                .And.HaveCount(_ranges.Length);
        }

        /// <summary>
        /// The same boundaries under a request that resolved the <c>DocumentId</c> anchor — an
        /// unwindowed request, or a windowed one on a deployment running with the page-ordering kill
        /// switch on. The marker follows the request's resolved anchor rather than being a constant.
        /// </summary>
        [Test]
        public async Task It_marks_every_token_of_a_document_id_anchored_response()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(new PartitionResult.PartitionSuccess(_ranges)),
                orderingMode: PageOrderingMode.DocumentId
            );

            DecodePageTokenAnchors(requestInfo)
                .Should()
                .AllBeEquivalentTo(PageOrderingMode.DocumentId)
                .And.HaveCount(_ranges.Length);
        }

        // A boundary set is not a page: it has no total count and no successor.
        [Test]
        public void It_emits_no_paging_headers()
        {
            _requestInfo.FrontendResponse.Headers.Should().NotContainKey("Total-Count");
            _requestInfo.FrontendResponse.Headers.Should().NotContainKey("Next-Page-Token");
        }

        [Test]
        public void It_calls_the_backend_once()
        {
            _handler.CallCount.Should().Be(1);
        }

        [Test]
        public void It_hands_the_validated_count_to_the_backend()
        {
            _handler.CapturedRequest!.RequestedPartitionCount.Should().Be(RequestedPartitionCount);
        }

        // Measured from the configured maximum page size, so a partition is never smaller than the
        // pages a walk of it will use.
        [Test]
        public void It_derives_the_minimum_partition_size_from_the_configured_page_size()
        {
            _handler
                .CapturedRequest!.MinimumPartitionSize.Should()
                .Be(CursorPagingLimits.MinimumPartitionSize(MaximumPageSize));
        }

        // Boundaries are calculated over the rows a page of the same request would be selected from, so
        // everything that narrows that candidate set has to reach the backend. Dropping any of the three
        // below would still answer 200 with walkable tokens, just over the wrong set of rows.
        [Test]
        public void It_hands_the_resource_filters_to_the_backend()
        {
            _handler.CapturedRequest!.QueryElements.Should().BeSameAs(_queryElements);
        }

        [Test]
        public void It_hands_the_change_version_window_to_the_backend()
        {
            _handler.CapturedRequest!.ChangeVersionRange.Should().Be(_changeVersionRange);
        }

        /// <summary>
        /// The backend cuts boundaries on the anchor Core resolved rather than one it derives from the
        /// window it receives, so a partition set and a page of the same request cannot end up ordered
        /// differently.
        /// </summary>
        [Test]
        public void It_hands_the_resolved_boundary_anchor_to_the_backend()
        {
            _handler.CapturedRequest!.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        [Test]
        public void It_hands_the_tenant_to_the_backend()
        {
            _handler.CapturedRequest!.TenantKey.Should().Be(TenantKey);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Accessible_Candidates : PartitionRequestHandlerTests
    {
        [Test]
        public async Task It_serves_an_empty_token_array()
        {
            RequestInfo requestInfo = await Execute(new Handler(new PartitionResult.PartitionSuccess([])));

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body!["pageTokens"]!.AsArray().Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Backend_Failure : PartitionRequestHandlerTests
    {
        [Test]
        public async Task It_maps_not_implemented_to_501()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(new PartitionResult.PartitionFailureNotImplemented("no capability"))
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(501);
            requestInfo.FrontendResponse.Body!["error"]!.GetValue<string>().Should().Be("no capability");
        }

        [Test]
        public async Task It_maps_namespace_denial_to_403_problem_json()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(
                    new PartitionResult.PartitionFailureNamespaceNotAuthorized(
                        new NamespaceAuthorizationFailure(
                            NamespaceAuthorizationFailureKind.NoPrefixesConfigured,
                            NamespaceAuthorizationFailureValueSource.Stored,
                            EmittedAuth1Index: 0,
                            AuthorizationStrategyNameConstants.NamespaceBased,
                            []
                        )
                    )
                )
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public async Task It_maps_security_configuration_to_500_problem_json()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(new PartitionResult.PartitionFailureSecurityConfiguration(["bad metadata"]))
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        // Matches the GET-many path: once retries are exhausted the client receives a generic system
        // error rather than a retryable status code.
        [Test]
        public async Task It_maps_retryable_to_500_problem_json()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(new PartitionResult.PartitionFailureRetryable())
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        /// <summary>
        /// The failure message names internal components and is written for diagnosis, so it is logged
        /// rather than served: the client receives the same problem-details envelope every other
        /// handler serves for a failure the backend could not classify.
        /// </summary>
        [Test]
        public async Task It_maps_unknown_to_500_problem_json_without_the_failure_message()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(new PartitionResult.UnknownPartitionFailure("boom"))
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            JsonNode body = requestInfo.FrontendResponse.Body!;
            body.ToJsonString().Should().NotContain("boom");
            JsonNode
                .DeepEquals(body, FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId))
                .Should()
                .BeTrue();
        }
    }

    /// <summary>
    /// A missing count means the validating middleware did not run or did not accept the request, which
    /// is a composition fault rather than a client one.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_No_Validated_Count : PartitionRequestHandlerTests
    {
        [Test]
        public async Task It_throws_rather_than_substituting_a_default()
        {
            var handler = new Handler(new PartitionResult.PartitionSuccess([]));

            var action = () => Execute(handler, requestedPartitionCount: null);

            await action.Should().ThrowAsync<InvalidOperationException>();
            handler.CallCount.Should().Be(0);
        }
    }

    /// <summary>
    /// What each partitions outcome contributes to the collection-paging metric.
    /// </summary>
    /// <remarks>
    /// There is no terminal-page outcome here: a boundary set has no successor, so the continuation
    /// question a GET-many page answers does not arise.
    /// </remarks>
    [TestFixture]
    [Parallelizable]
    public class Given_Collection_Paging_Telemetry_For_Partitions : PartitionRequestHandlerTests
    {
        /// <summary>
        /// A count distinct from <see cref="RequestedPartitionCount" />, standing for the configured
        /// default the validating middleware substitutes when a request names none. The handler sees
        /// only the resolved value, so this is what "requested" means for such a request.
        /// </summary>
        private const int ResolvedDefaultPartitionCount = 4;

        private sealed class ThrowingHandler(Exception fault) : IPartitionQueryHandler
        {
            public Task<PartitionResult> QueryPartitions(
                IPartitionRequest partitionRequest,
                CancellationToken cancellationToken = default
            ) => throw fault;
        }

        private static async Task<RecordingCollectionPagingTelemetry> ExecuteAsync(
            PartitionResult result,
            int? requestedPartitionCount = RequestedPartitionCount
        )
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            await Execute(new Handler(result), requestedPartitionCount, telemetry);

            return telemetry;
        }

        private static async Task<(
            Exception? Fault,
            RecordingCollectionPagingTelemetry Telemetry
        )> ExecuteThrowingAsync(Exception fault, CancellationToken requestCancellationToken = default)
        {
            RecordingCollectionPagingTelemetry telemetry = new();
            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IPartitionQueryHandler)))
                .Returns(new ThrowingHandler(fault));

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.ScopedServiceProvider = serviceProvider;
            requestInfo.RequestedPartitionCount = RequestedPartitionCount;
            requestInfo.RequestCancellationToken = requestCancellationToken;

            try
            {
                await new PartitionRequestHandler(
                    NullLogger.Instance,
                    ResiliencePipeline.Empty,
                    MaximumPageSize,
                    telemetry
                ).Execute(requestInfo, NullNext);

                return (null, telemetry);
            }
            catch (Exception caught)
            {
                return (caught, telemetry);
            }
        }

        [Test]
        public async Task It_records_a_calculated_boundary_set()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteAsync(
                new PartitionResult.PartitionSuccess([
                    new CursorRange(1, 99),
                    new CursorRange(100, long.MaxValue),
                ])
            );

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Kind.Should().Be(CollectionPagingMeasurementKind.Partitions);
            measurement.PagingMode.Should().Be("partition");
            measurement.CommandCategory.Should().Be("boundary");
            measurement.Provider.Should().Be("postgresql");
            measurement.Outcome.Should().Be("success");
            measurement.Requested.Should().Be(RequestedPartitionCount);
            measurement.Returned.Should().Be(2);
            measurement.Duration.Should().NotBeNull().And.BeGreaterThanOrEqualTo(TimeSpan.Zero);
        }

        // The boundary command executed and found no starts. That is a success with a returned count of
        // zero, and it must not be reported as the short-circuit that issued no command at all.
        [Test]
        public async Task It_records_an_executed_empty_boundary_set_as_success()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteAsync(
                new PartitionResult.PartitionSuccess([])
            );

            telemetry.Single.Outcome.Should().Be("success");
            telemetry.Single.CommandCategory.Should().Be("boundary");
            telemetry.Single.Returned.Should().Be(0);
        }

        [Test]
        public async Task It_records_a_skipped_selection_as_early_empty()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteAsync(
                new PartitionResult.PartitionSuccess([]) { SelectionSkipped = true }
            );

            telemetry.Single.Outcome.Should().Be("early_empty");
            telemetry.Single.CommandCategory.Should().Be("none");
            telemetry.Single.Returned.Should().Be(0);
        }

        // The companion to the case above: both halves of what early_empty asserts are checked, not the
        // flag alone. A success carrying ranges was calculated by a boundary command whatever the flag
        // says, so it is reported as the boundary work it did rather than as a skipped selection.
        [Test]
        public async Task It_does_not_record_early_empty_for_a_skipped_selection_that_returned_ranges()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteAsync(
                new PartitionResult.PartitionSuccess([new CursorRange(1, long.MaxValue)])
                {
                    SelectionSkipped = true,
                }
            );

            telemetry.Single.Outcome.Should().Be("success");
            telemetry.Single.CommandCategory.Should().Be("boundary");
            telemetry.Single.Returned.Should().Be(1);
        }

        private static readonly TestCaseData[] _failureOutcomes =
        [
            new TestCaseData(
                new PartitionResult.PartitionFailureNotImplemented("no capability"),
                "not_implemented"
            ).SetName("{m}(not_implemented)"),
            new TestCaseData(
                new PartitionResult.PartitionFailureSecurityConfiguration(["bad metadata"]),
                "security_configuration"
            ).SetName("{m}(security_configuration)"),
            new TestCaseData(
                new PartitionResult.PartitionFailureNamespaceNotAuthorized(
                    new NamespaceAuthorizationFailure(
                        NamespaceAuthorizationFailureKind.NoPrefixesConfigured,
                        NamespaceAuthorizationFailureValueSource.Stored,
                        EmittedAuth1Index: 0,
                        AuthorizationStrategyNameConstants.NamespaceBased,
                        []
                    )
                ),
                "not_authorized"
            ).SetName("{m}(not_authorized)"),
            new TestCaseData(new PartitionResult.PartitionFailureRetryable(), "retry_exhausted").SetName(
                "{m}(retry_exhausted)"
            ),
            new TestCaseData(new PartitionResult.UnknownPartitionFailure("boom"), "unknown_failure").SetName(
                "{m}(unknown_failure)"
            ),
        ];

        [TestCaseSource(nameof(_failureOutcomes))]
        public async Task It_records_every_failure_with_no_command_category(
            PartitionResult failure,
            string expectedOutcome
        )
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteAsync(failure);

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Outcome.Should().Be(expectedOutcome);
            measurement.CommandCategory.Should().Be("none");
            measurement.Returned.Should().BeNull();
            measurement.Requested.Should().Be(RequestedPartitionCount);
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

        // The GET-many twin of this runs in QueryRequestHandlerTests. Both are pinned because the
        // breaker is one shared pipeline instance: when it opens, /partitions is refused alongside
        // GET-many, and each handler classifies that refusal in its own catch.
        [Test]
        public async Task It_records_an_execution_exception_when_the_circuit_is_open()
        {
            RecordingCollectionPagingTelemetry telemetry = new();
            CircuitBreakerManualControl manualControl = new();
            ResiliencePipeline openCircuit = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions { ManualControl = manualControl })
                .Build();

            await manualControl.IsolateAsync();

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IPartitionQueryHandler)))
                .Returns(
                    new ThrowingHandler(
                        new InvalidOperationException("The partition handler must not be reached.")
                    )
                );

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.ScopedServiceProvider = serviceProvider;
            requestInfo.RequestedPartitionCount = RequestedPartitionCount;

            var execute = async () =>
                await new PartitionRequestHandler(
                    NullLogger.Instance,
                    openCircuit,
                    MaximumPageSize,
                    telemetry
                ).Execute(requestInfo, NullNext);

            // The breaker's own exception rather than the backend's: the boundary command was refused
            // before it reached the database.
            await execute.Should().ThrowAsync<BrokenCircuitException>();

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Outcome.Should().Be("execution_exception");
            measurement.CommandCategory.Should().Be("none");
            measurement.Returned.Should().BeNull();
            measurement.Requested.Should().Be(RequestedPartitionCount);
        }

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

        [Test]
        public async Task It_records_an_execution_exception_for_a_cancellation_the_request_did_not_ask_for()
        {
            var (caught, telemetry) = await ExecuteThrowingAsync(new OperationCanceledException());

            caught.Should().BeOfType<OperationCanceledException>();
            telemetry.Single.Outcome.Should().Be("execution_exception");
        }

        // The count a request omitted is the configured default the validating middleware substituted,
        // which is what the client will be served and therefore what it asked for.
        [Test]
        public async Task It_records_the_validated_count_the_middleware_resolved()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteAsync(
                new PartitionResult.PartitionSuccess([new CursorRange(1, long.MaxValue)]),
                requestedPartitionCount: ResolvedDefaultPartitionCount
            );

            telemetry.Single.Requested.Should().Be(ResolvedDefaultPartitionCount);
            telemetry.Single.Returned.Should().Be(1);
        }

        [TestCase(SqlDialect.Pgsql, "postgresql")]
        [TestCase(SqlDialect.Mssql, "sqlserver")]
        public async Task It_reports_the_provider_of_the_resolved_mapping_set(
            SqlDialect dialect,
            string expectedProvider
        )
        {
            RecordingCollectionPagingTelemetry telemetry = new();
            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IPartitionQueryHandler)))
                .Returns(new Handler(new PartitionResult.PartitionSuccess([new CursorRange(1, 99)])));

            RequestInfo requestInfo = No.RequestInfo();
            requestInfo.MappingSet = RelationalWriteSeamFixture.Create().CreateSupportedMappingSet(dialect);
            requestInfo.ScopedServiceProvider = serviceProvider;
            requestInfo.RequestedPartitionCount = RequestedPartitionCount;

            await new PartitionRequestHandler(
                NullLogger.Instance,
                ResiliencePipeline.Empty,
                MaximumPageSize,
                telemetry
            ).Execute(requestInfo, NullNext);

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Provider.Should().Be(expectedProvider);
            measurement.PagingMode.Should().Be("partition");
            measurement.CommandCategory.Should().Be("boundary");
            measurement.Outcome.Should().Be("success");
            measurement.Requested.Should().Be(RequestedPartitionCount);
            measurement.Returned.Should().Be(1);
        }

        [Test]
        public async Task It_leaves_the_response_exactly_as_it_would_be_without_instrumentation()
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            RequestInfo requestInfo = await Execute(
                new Handler(
                    new PartitionResult.PartitionSuccess([
                        new CursorRange(1, 99),
                        new CursorRange(100, long.MaxValue),
                    ])
                ),
                RequestedPartitionCount,
                telemetry
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
            requestInfo.FrontendResponse.Body!["pageTokens"]!.AsArray().Should().HaveCount(2);
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            telemetry.Single.Outcome.Should().Be("success");
        }

        // The other half of "instrumentation must not participate": recording runs after the response is
        // assembled, so a measurement callback that throws would discard a boundary set the request had
        // already earned and answer a system error instead.
        [Test]
        public async Task It_serves_the_boundary_set_when_recording_throws()
        {
            RequestInfo requestInfo = await Execute(
                new Handler(
                    new PartitionResult.PartitionSuccess([
                        new CursorRange(1, 99),
                        new CursorRange(100, long.MaxValue),
                    ])
                ),
                RequestedPartitionCount,
                new ThrowingCollectionPagingTelemetry()
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.FrontendResponse.Body!["pageTokens"]!.AsArray().Should().HaveCount(2);
        }

        // The execution-exception emission runs from inside a catch that is about to rethrow. A telemetry
        // fault there would replace the fault being reported, which is exactly the diagnosis that catch
        // exists to preserve.
        [Test]
        public async Task It_propagates_the_execution_fault_when_recording_throws()
        {
            InvalidOperationException executionFault = new("A configured custom view is not conforming.");

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IPartitionQueryHandler)))
                .Returns(new ThrowingHandler(executionFault));

            RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
            requestInfo.ScopedServiceProvider = serviceProvider;
            requestInfo.RequestedPartitionCount = RequestedPartitionCount;

            Func<Task> execute = () =>
                new PartitionRequestHandler(
                    NullLogger.Instance,
                    ResiliencePipeline.Empty,
                    MaximumPageSize,
                    new ThrowingCollectionPagingTelemetry()
                ).Execute(requestInfo, NullNext);

            (await execute.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should()
                .BeSameAs(executionFault);
        }
    }
}
