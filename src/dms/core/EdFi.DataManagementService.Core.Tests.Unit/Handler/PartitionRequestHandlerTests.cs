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
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly;
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
        int? requestedPartitionCount = RequestedPartitionCount
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IPartitionQueryHandler)))
            .Returns(partitionQueryHandler);

        RequestInfo requestInfo = RequestInfoWithRelationalMappingSet();
        requestInfo.ScopedServiceProvider = serviceProvider;
        requestInfo.RequestedPartitionCount = requestedPartitionCount;

        await new PartitionRequestHandler(
            NullLogger.Instance,
            ResiliencePipeline.Empty,
            MaximumPageSize
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
                PageTokenCodec.TryDecode(pageToken!.GetValue<string>(), out var range).Should().BeTrue();
                ranges.Add(range!);
            }

            return ranges;
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
}
