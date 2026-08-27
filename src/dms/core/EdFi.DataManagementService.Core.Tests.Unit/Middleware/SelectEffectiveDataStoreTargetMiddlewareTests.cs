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
/// assigns, when it records the outcome, and what a rejected request never reaches.
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
    ) =>
        new(
            policy,
            new DefaultEffectiveTargetSelectionResponseFactory(),
            NullLogger<SelectEffectiveDataStoreTargetMiddleware>.Instance
        );

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
        public void It_records_the_outcome()
        {
            _requestInfo
                .EffectiveTargetSelection.Should()
                .BeOfType<EffectiveTargetSelectionResult.Selected>()
                .Which.Target.Kind.Should()
                .Be(EffectiveTargetKind.Primary);
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

        [Test]
        public void It_records_the_missing_snapshot_outcome()
        {
            _requestInfo
                .EffectiveTargetSelection.Should()
                .BeOfType<EffectiveTargetSelectionResult.MissingSnapshot>()
                .Which.ParentDataStoreId.Should()
                .Be(7);
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

        [Test]
        public void It_returns_the_snapshot_not_found_body()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Snapshot not found.");
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
        public void It_records_the_mutation_rejection_outcome()
        {
            _requestInfo
                .EffectiveTargetSelection.Should()
                .BeOfType<EffectiveTargetSelectionResult.RejectedAsMutation>()
                .Which.ParentDataStoreId.Should()
                .Be(7);
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

        [Test]
        public void It_returns_the_method_not_allowed_problem_type()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("method-not-allowed");
        }
    }

    /// <summary>
    /// The resolver answers 503 for an instance with no connection string before it records a
    /// selection, and the selection itself refuses to record one, so this guard is defensive. It is
    /// here rather than further down the pipeline because this step is now the first reader of the
    /// parent's connection string.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Parent_Without_A_Connection_String : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private IDataStoreSelection _selection = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _selection = A.Fake<IDataStoreSelection>();
            A.CallTo(() => _selection.GetSelectedDataStore())
                .Returns(DataStoreWith() with { ConnectionString = null });

            _requestInfo = RequestInfoFor(_selection, useSnapshotHeader: false);

            await CreateMiddleware(_readPolicy)
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_assigns_no_target()
        {
            A.CallTo(() => _selection.SetEffectiveTarget(A<EffectiveDataStoreTarget>.Ignored))
                .MustNotHaveHappened();
        }

        [Test]
        public void It_records_no_outcome()
        {
            _requestInfo.EffectiveTargetSelection.Should().BeNull();
        }

        [Test]
        public void It_returns_503_service_unavailable()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_a_service_configuration_error()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Service Configuration Error");
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("Database connection not configured");
        }
    }

    /// <summary>
    /// The recorded outcome must be observable to whatever inspects the request afterwards, which
    /// means it is written before the response is produced rather than after.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Rejection_Is_Being_Turned_Into_A_Response
        : SelectEffectiveDataStoreTargetMiddlewareTests
    {
        private sealed class OutcomeObservingResponseFactory : IEffectiveTargetSelectionResponseFactory
        {
            public EffectiveTargetSelectionResult? ObservedAtMissingSnapshot { get; private set; }

            public EffectiveTargetSelectionResult? ObservedAtRejectedAsMutation { get; private set; }

            public IFrontendResponse ForMissingSnapshot(RequestInfo requestInfo)
            {
                ObservedAtMissingSnapshot = requestInfo.EffectiveTargetSelection;
                return No.FrontendResponse;
            }

            public IFrontendResponse ForRejectedAsMutation(RequestInfo requestInfo)
            {
                ObservedAtRejectedAsMutation = requestInfo.EffectiveTargetSelection;
                return No.FrontendResponse;
            }
        }

        private static async Task<OutcomeObservingResponseFactory> ExecuteWithObservingFactory(
            DerivativeRoutingPolicy policy,
            DataStore parent
        )
        {
            OutcomeObservingResponseFactory factory = new();
            DataStoreSelection selection = new();
            selection.SetSelectedDataStore(parent);

            SelectEffectiveDataStoreTargetMiddleware middleware = new(
                policy,
                factory,
                NullLogger<SelectEffectiveDataStoreTargetMiddleware>.Instance
            );

            await middleware.Execute(
                RequestInfoFor(selection, useSnapshotHeader: true),
                () => Task.CompletedTask
            );

            return factory;
        }

        [Test]
        public async Task It_has_already_recorded_a_missing_snapshot()
        {
            var factory = await ExecuteWithObservingFactory(_readPolicy, DataStoreWith());

            factory
                .ObservedAtMissingSnapshot.Should()
                .BeOfType<EffectiveTargetSelectionResult.MissingSnapshot>();
        }

        [Test]
        public async Task It_has_already_recorded_a_mutation_rejection()
        {
            var factory = await ExecuteWithObservingFactory(_mutationPolicy, DataStoreWith());

            factory
                .ObservedAtRejectedAsMutation.Should()
                .BeOfType<EffectiveTargetSelectionResult.RejectedAsMutation>();
        }
    }
}
