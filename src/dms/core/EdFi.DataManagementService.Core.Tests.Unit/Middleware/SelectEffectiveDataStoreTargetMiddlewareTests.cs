// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// The step that turns the routing verdict into request state. The verdicts themselves are covered by
/// EffectiveTargetSelectorTests; what these fixtures pin is what the step does with one - what it
/// assigns, what it answers, and what a rejected request never reaches.
/// </summary>
[TestFixture]
[Parallelizable]
public class SelectEffectiveDataStoreTargetMiddlewareTests
{
    private const string PrimaryConnectionString = "Server=primary;Database=edfi";
    private const string ReplicaConnectionString = "Server=replica;Database=edfi";
    private const string SnapshotConnectionString = "Server=snapshot;Database=edfi";

    private static readonly DerivativeRoutingPolicy _readPolicy = new(
        DatabaseAccessIntent.ReadOnly,
        SnapshotEligibility.Allowed,
        ReplicaEligibility.Allowed
    );

    private static readonly DerivativeRoutingPolicy _mutationPolicy = new(
        DatabaseAccessIntent.ReadWrite,
        SnapshotEligibility.RejectedAsMutation,
        ReplicaEligibility.NotApplicable
    );

    private static DataStore DataStoreWith(
        params KeyValuePair<DataStoreDerivativeType, string>[] derivatives
    ) =>
        new(
            Id: 7,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: PrimaryConnectionString,
            RouteContext: [],
            DerivativeConnectionStrings: derivatives
        );

    private static RequestInfo RequestInfoFor(IDataStoreSelection dataStoreSelection, bool useSnapshotHeader)
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(dataStoreSelection);

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (useSnapshotHeader)
        {
            headers["Use-Snapshot"] = "true";
        }

        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Form: null,
            Headers: headers,
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id"),
            RouteQualifiers: []
        );

        return new RequestInfo(frontendRequest, RequestMethod.GET, serviceProvider);
    }

    private static SelectEffectiveDataStoreTargetMiddleware CreateMiddleware(
        DerivativeRoutingPolicy policy
    ) => new(policy, NullLogger<SelectEffectiveDataStoreTargetMiddleware>.Instance);

    /// <summary>
    /// Runs the step over the real production selection, so what the assertions observe is the
    /// contract every later consumer reads through, not a fake that would answer anything.
    /// </summary>
    private static async Task<(
        RequestInfo requestInfo,
        DataStoreSelection selection,
        bool nextCalled
    )> Execute(DerivativeRoutingPolicy policy, DataStore parent, bool useSnapshotHeader)
    {
        DataStoreSelection selection = new();
        selection.SetSelectedDataStore(parent);

        var requestInfo = RequestInfoFor(selection, useSnapshotHeader);
        bool nextCalled = false;

        await CreateMiddleware(policy)
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        return (requestInfo, selection, nextCalled);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Read_With_No_Derivatives : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private DataStoreSelection _selection = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            (_requestInfo, _selection, _nextCalled) = await Execute(
                _readPolicy,
                DataStoreWith(),
                useSnapshotHeader: false
            );
        }

        /// <summary>
        /// The primary is chosen explicitly rather than left unassigned for a later consumer to
        /// default to, which is what makes a missing selection step detectable.
        /// </summary>
        [Test]
        public void It_assigns_the_primary_explicitly()
        {
            _selection.IsEffectiveTargetSet.Should().BeTrue();
            _selection.GetEffectiveTarget().Kind.Should().Be(EffectiveTargetKind.Primary);
            _selection.GetEffectiveTarget().ConnectionString.Should().Be(PrimaryConnectionString);
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_no_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Read_With_A_Configured_Replica : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private DataStoreSelection _selection = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var executed = await Execute(
                _readPolicy,
                DataStoreWith(
                    KeyValuePair.Create(DataStoreDerivativeType.ReadReplica, ReplicaConnectionString)
                ),
                useSnapshotHeader: false
            );
            _selection = executed.selection;
            _nextCalled = executed.nextCalled;
        }

        [Test]
        public void It_assigns_the_replica()
        {
            _selection.GetEffectiveTarget().Kind.Should().Be(EffectiveTargetKind.ReadReplica);
            _selection.GetEffectiveTarget().ConnectionString.Should().Be(ReplicaConnectionString);
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Requested_Snapshot_That_Is_Configured : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private DataStoreSelection _selection = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var executed = await Execute(
                _readPolicy,
                DataStoreWith(
                    KeyValuePair.Create(DataStoreDerivativeType.Snapshot, SnapshotConnectionString),
                    KeyValuePair.Create(DataStoreDerivativeType.ReadReplica, ReplicaConnectionString)
                ),
                useSnapshotHeader: true
            );
            _selection = executed.selection;
            _nextCalled = executed.nextCalled;
        }

        [Test]
        public void It_assigns_the_snapshot()
        {
            _selection.GetEffectiveTarget().Kind.Should().Be(EffectiveTargetKind.Snapshot);
            _selection.GetEffectiveTarget().ConnectionString.Should().Be(SnapshotConnectionString);
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Requested_Snapshot_That_Is_Not_Configured
        : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private DataStoreSelection _selection = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            (_requestInfo, _selection, _nextCalled) = await Execute(
                _readPolicy,
                DataStoreWith(
                    KeyValuePair.Create(DataStoreDerivativeType.ReadReplica, ReplicaConnectionString)
                ),
                useSnapshotHeader: true
            );
        }

        /// <summary>
        /// Nothing downstream can be served the primary or the replica by accident, because no target
        /// exists for anything to read.
        /// </summary>
        [Test]
        public void It_assigns_no_target()
        {
            _selection.IsEffectiveTargetSet.Should().BeFalse();
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_404()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        }

        /// <summary>
        /// The shared not-found type, not a snapshot-specific one: the design reuses the existing
        /// not-found factory for this response, so only the detail names the snapshot.
        /// </summary>
        [Test]
        public void It_returns_the_not_found_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!.ToString().Should().Be("urn:ed-fi:api:not-found");
        }

        [Test]
        public void It_returns_the_not_found_title()
        {
            _requestInfo.FrontendResponse.Body!["title"]!.ToString().Should().Be("Not Found");
        }

        [Test]
        public void It_returns_the_snapshot_not_found_detail()
        {
            _requestInfo.FrontendResponse.Body!["detail"]!.ToString().Should().Be("Snapshot not found.");
        }

        [Test]
        public void It_returns_404_in_the_body_status()
        {
            _requestInfo.FrontendResponse.Body!["status"]!.GetValue<int>().Should().Be(404);
        }

        [Test]
        public void It_carries_the_request_correlation_id()
        {
            _requestInfo.FrontendResponse.Body!["correlationId"]!.ToString().Should().Be("test-trace-id");
        }

        [Test]
        public void It_uses_the_problem_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Requested_Snapshot_On_A_Mutation : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private DataStoreSelection _selection = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            (_requestInfo, _selection, _nextCalled) = await Execute(
                _mutationPolicy,
                DataStoreWith(
                    KeyValuePair.Create(DataStoreDerivativeType.Snapshot, SnapshotConnectionString)
                ),
                useSnapshotHeader: true
            );
        }

        [Test]
        public void It_assigns_no_target()
        {
            _selection.IsEffectiveTargetSet.Should().BeFalse();
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_405()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(405);
        }

        /// <summary>
        /// Asserted as the whole type rather than a substring: the route-semantics 405's
        /// urn:ed-fi:api:method-not-allowed is a substring of this one, so a Contains assertion
        /// accepts either body and cannot tell the two contracts apart.
        /// </summary>
        [Test]
        public void It_returns_the_snapshot_method_not_allowed_type()
        {
            _requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be("urn:ed-fi:api:snapshots:method-not-allowed");
        }

        [Test]
        public void It_returns_the_snapshot_method_not_allowed_title()
        {
            _requestInfo.FrontendResponse.Body!["title"]!
                .ToString()
                .Should()
                .Be("Method Not Allowed with Snapshots");
        }

        /// <summary>
        /// The detail names the read-only target rather than the request construction, because the
        /// same route is valid against the primary.
        /// </summary>
        [Test]
        public void It_returns_the_read_only_detail()
        {
            _requestInfo.FrontendResponse.Body!["detail"]!
                .ToString()
                .Should()
                .Be("An attempt was made to modify data in a Snapshot, but this data is read-only.");
        }

        [Test]
        public void It_returns_405_in_the_body_status()
        {
            _requestInfo.FrontendResponse.Body!["status"]!.GetValue<int>().Should().Be(405);
        }

        [Test]
        public void It_carries_the_request_correlation_id()
        {
            _requestInfo.FrontendResponse.Body!["correlationId"]!.ToString().Should().Be("test-trace-id");
        }

        /// <summary>
        /// Allow states what is permitted in snapshot context, where the target is read-only, rather
        /// than what this route would permit on the primary - so it is GET even though the route's own
        /// set is GET, POST.
        /// </summary>
        [Test]
        public void It_sends_allow_get()
        {
            _requestInfo.FrontendResponse.Headers.Should().ContainKey("Allow").WhoseValue.Should().Be("GET");
        }

        [Test]
        public void It_uses_the_problem_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }
    }
}
